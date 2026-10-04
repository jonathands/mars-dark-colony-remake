using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>The planner's region map (<c>krusty_attack.c</c>): PTH regions as zones.</summary>
public sealed partial class KrustyBrain
{
    private const int UnreachedDistance = 0x100;
    private const int SettledDistance = 0x101;
    private const int CentroidSearchRadius = 100;

    private readonly Dictionary<int, KrustyGhost> ghosts = [];

    /// <summary>
    /// The last place, type and team the player's side saw an actor
    /// (4 bytes per actor slot at state <c>+0x1202</c>; type 0xFF is none).
    /// </summary>
    internal readonly record struct KrustyGhost(int X, int Z, int Type, int Team);

    /// <summary>
    /// <c>0x4571AC</c>: hop distances from the home region over the route
    /// table, and a representative cell for every region.
    /// </summary>
    private void InitializeZones()
    {
        var regions = world.KrustyRegions;
        foreach (var zone in Zones)
        {
            zone.Distance = NoOwner;
            zone.Flags = 0;
        }
        var home = world.KrustyHome(Player);
        var start = world.KrustyFreeCell(home) ?? home;
        var homeRegion = RegionOf(start);
        var distance = new int[ZoneCount];
        Array.Fill(distance, UnreachedDistance);
        distance[homeRegion] = 0;
        while (true)
        {
            // The unsettled region with the least distance, the first on ties.
            var nearest = -1;
            var least = UnreachedDistance;
            for (var region = 1; region < 255; region++)
                if (distance[region] < least)
                {
                    least = distance[region];
                    nearest = region;
                }
            if (least >= UnreachedDistance) break;
            Zones[nearest].Distance = least;
            distance[nearest] = SettledDistance;
            for (var target = 1; target < 255; target++)
            {
                var next = regions.NextRegion((byte)nearest, (byte)target);
                if (next != 0 && distance[next] != SettledDistance && distance[next] > least + 1) distance[next] = least + 1;
            }
        }

        var sumX = new long[ZoneCount];
        var sumZ = new long[ZoneCount];
        var cells = new int[ZoneCount];
        for (var z = 0; z < regions.Height; z++)
            for (var x = 0; x < regions.Width; x++)
            {
                var region = regions.RegionAt(new CellCoordinate(x, z));
                sumX[region] += x;
                sumZ[region] += z;
                cells[region]++;
            }
        for (var region = 1; region < 255; region++)
        {
            if (cells[region] == 0) continue;
            var centerX = (int)(sumX[region] / cells[region]);
            var centerZ = (int)(sumZ[region] / cells[region]);
            Zones[region].Centroid = FindRegionCell(region, centerX, centerZ) ?? new CellCoordinate(centerX, centerZ);
        }
    }

    /// <summary>The first cell of the region on square rings around the centroid, rows outer, radius below 100.</summary>
    private CellCoordinate? FindRegionCell(int region, int centerX, int centerZ)
    {
        var regions = world.KrustyRegions;
        for (var radius = 0; radius < CentroidSearchRadius; radius++)
            for (var z = centerZ - radius; z <= centerZ + radius; z++)
            {
                if ((uint)z >= (uint)regions.Height) continue;
                for (var x = centerX - radius; x <= centerX + radius; x++)
                {
                    if ((uint)x >= (uint)regions.Width) continue;
                    if (regions.RegionAt(new CellCoordinate(x, z)) == region) return new CellCoordinate(x, z);
                }
            }
        return null;
    }

    /// <summary>
    /// <c>0x456AD0</c>: rebuilds every region's owners and strengths from the
    /// units the player's side sees now or saw last.
    /// </summary>
    private void UpdateInfluence()
    {
        foreach (var zone in Zones)
        {
            zone.OwnerA = zone.OwnerB = zone.OwnerC = NoOwner;
            zone.StrengthA = zone.StrengthB = zone.StrengthC = 0;
            zone.Count = 0;
            zone.Flags &= ~1;
            if ((zone.Flags & 4) == 0) continue;
            // An unexplored region stays so until an ally sees its cell.
            for (var ally = 0; ally < allies.Length; ally++)
                if (allies[ally] && world.IsCellVisibleToTeam(ally, zone.Centroid)) zone.Flags &= ~4;
            zone.Count = 1;
        }

        var matrix = world.KrustyDamage;
        foreach (var actor in world.ActorsInNativeSlotOrder())
        {
            if (actor.Seed.Team is < 0 or >= 8) continue;
            var id = actor.Seed.InstanceId;
            var present = !actor.IsDestroyed || actor.IsDying;
            var cell = CellOf(actor);
            var seen = false;
            for (var ally = 0; ally < allies.Length; ally++)
            {
                if (!allies[ally] || !world.IsCellVisibleToTeam(ally, cell)) continue;
                var hiddenMine = world.EffectiveDefinition(actor).UsesNativeMineLayer && actor.Seed.Team != ally &&
                                 (actor.RevealedTeamMask & (1 << ally)) == 0;
                if (hiddenMine) continue;
                if (present) seen = true;
                else ghosts.Remove(id);
            }

            int x, z, type, team;
            if (seen)
            {
                x = cell.X;
                z = cell.Z;
                type = TypeOf(actor);
                team = actor.Seed.Team;
                if (team != Player) ghosts[id] = new KrustyGhost(x, z, type, team);
            }
            else
            {
                if (!ghosts.TryGetValue(id, out var ghost)) continue;
                if (world.IsCellVisibleToTeam(Player, new CellCoordinate(ghost.X, ghost.Z)))
                {
                    // The place is in view and the actor is not there.
                    ghosts.Remove(id);
                    continue;
                }
                (x, z, type, team) = (ghost.X, ghost.Z, ghost.Type, ghost.Team);
            }
            if (team != Player && world.KrustyCooperative(Player, team)) continue;

            var region = RegionOf(new CellCoordinate(x, z));
            if (region == 0 && world.KrustyFreeCell(new CellCoordinate(x, z)) is { } free) region = RegionOf(free);
            var zone = Zones[region];
            var definition = world.KrustyEntity(type);
            var weaponId = definition.WeaponSlots[0];
            if (weaponId == -1 || world.KrustyWeapon(weaponId) is not { } weapon)
            {
                if (weaponId == -1 && team != Player && team < 8 && definition.ArmorClass != 8) zone.Count = (ushort)(zone.Count + 1);
                continue;
            }
            if (matrix is null) continue;
            var strength = Strength(weapon.WeaponClass, definition.ArmorClass, 1);
            var antiAir = matrix.NativeFactor(weapon.WeaponClass, 2);
            if (definition.MovementClass != 0 && strength > 0) Claim(zone, KrustySlot.Fliers, strength, team);
            if (strength > 0) Claim(zone, KrustySlot.Ground, strength, team);
            if (antiAir > 0) Claim(zone, KrustySlot.AntiAir, antiAir, team);
        }

        foreach (var zone in Zones)
        {
            var a = zone.OwnerA;
            var b = zone.OwnerB;
            var c = zone.OwnerC;
            if ((a != NoOwner && ((b != NoOwner && b != a) || (c != NoOwner && c != a))) ||
                (b != NoOwner && c != NoOwner && b != c))
                zone.Flags |= 1;
        }
    }

    /// <summary>
    /// A unit's rating from the 8.8 damage table: <c>count * 25 * M[w][1] / M[1][armor]</c>,
    /// with 50 for armor class 2 (which class 1 cannot hurt). A zero divisor
    /// (an executable fault) rates 0.
    /// </summary>
    private int Strength(int weaponClass, int armorClass, int count)
    {
        var matrix = world.KrustyDamage!;
        var divisor = armorClass == 2 ? 50 : matrix.NativeFactor(1, armorClass);
        return divisor == 0 ? 0 : count * matrix.NativeFactor(weaponClass, 1) * 25 / divisor;
    }

    private enum KrustySlot { AntiAir, Ground, Fliers }

    /// <summary>
    /// One owner per slot: the same team adds, another subtracts and takes the
    /// slot when it is at least as strong (a word, so it wraps).
    /// </summary>
    private static void Claim(KrustyZone zone, KrustySlot slot, int value, int team)
    {
        var (owner, strength) = slot switch
        {
            KrustySlot.AntiAir => (zone.OwnerA, zone.StrengthA),
            KrustySlot.Ground => (zone.OwnerB, zone.StrengthB),
            _ => (zone.OwnerC, zone.StrengthC),
        };
        if (owner == team) strength = (ushort)(strength + value);
        else
        {
            if (owner != NoOwner) zone.Flags |= 1;
            if (value < strength) strength = (ushort)(strength - value);
            else
            {
                strength = (ushort)(value - strength);
                owner = team;
            }
        }
        switch (slot)
        {
            case KrustySlot.AntiAir: (zone.OwnerA, zone.StrengthA) = (owner, strength); break;
            case KrustySlot.Ground: (zone.OwnerB, zone.StrengthB) = (owner, strength); break;
            default: (zone.OwnerC, zone.StrengthC) = (owner, strength); break;
        }
    }

    /// <summary>
    /// <c>0x45817C</c>: ground strength not owned by <paramref name="team"/>
    /// within two region steps of the route from <paramref name="from"/> to
    /// <paramref name="to"/> (or of the given route), or -1 when the route breaks.
    /// </summary>
    internal int PathDanger(int from, int to, int team, IReadOnlyList<byte>? route = null)
    {
        var regions = world.KrustyRegions;
        var near = new bool[ZoneCount];
        void MarkWithNeighbors(int region)
        {
            near[region] = true;
            foreach (var neighbor in regions.RegionNeighbors((byte)region)) near[neighbor] = true;
        }
        if (route is null)
        {
            if (from == 0) return -1;
            var region = from;
            for (var step = 0; ; step++)
            {
                MarkWithNeighbors(region);
                if (region == to) break;
                region = regions.NextRegion((byte)region, (byte)to);
                if (region == 0 || step >= ZoneCount) return -1;
            }
        }
        else
        {
            for (var index = 0; index < route.Count && index < ZoneCount; index++)
            {
                MarkWithNeighbors(route[index]);
                if (route[index] == to) break;
            }
        }
        var reach = (bool[])near.Clone();
        for (var region = 1; region < ZoneCount; region++)
            if (near[region])
                foreach (var neighbor in regions.RegionNeighbors((byte)region)) reach[neighbor] = true;
        var danger = 0;
        for (var region = 0; region < ZoneCount; region++)
        {
            if (!reach[region]) continue;
            var owner = Zones[region].OwnerB;
            if (owner != NoOwner && owner != team) danger += Zones[region].StrengthB;
        }
        return danger;
    }

    /// <summary><c>0x44B640</c>: route-table steps from <paramref name="from"/> to <paramref name="to"/>; 0xFF when either is 0 or the route breaks.</summary>
    internal int HopDistance(int from, int to)
    {
        if (from == 0 || to == 0) return NoOwner;
        var region = from;
        var hops = 0;
        while (region != to)
        {
            if (region == 0) return NoOwner;
            region = world.KrustyRegions.NextRegion((byte)region, (byte)to);
            if (++hops >= 0x100) return hops;
        }
        return hops;
    }

    /// <summary>The route-table walk from <paramref name="from"/> to <paramref name="to"/>, inclusive (no zero check, as in <c>0x463840</c>).</summary>
    private byte[] WalkRoute(int from, int to)
    {
        var route = new byte[ZoneCount];
        var region = from;
        for (var index = 0; index < ZoneCount; index++)
        {
            route[index] = (byte)region;
            if (region == to) break;
            region = world.KrustyRegions.NextRegion((byte)region, (byte)to);
        }
        return route;
    }
}
