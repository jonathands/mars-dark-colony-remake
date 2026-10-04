using DarkColony.Engine.Commands;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>The four task groups (<c>krusty_army.c</c>, <c>krusty_defend.c</c>, <c>krusty_scout.c</c>).</summary>
public sealed partial class KrustyBrain
{
    private const int HarvesterType = 6;
    private const int SlugType = 14;
    private const int VentStuckLimit = 10;
    private const int ArmyStuckLimit = 0x3C;
    private const int ArmyArrivalHops = 3;

    // ---- task lists (0x44BC68 insert, 0x44B928 remove) ----

    private void Insert(int instanceId, int group, int task)
    {
        Groups[group].Tasks[task].MemberList.Insert(0, instanceId);
        membership[instanceId] = (group, task);
    }

    private void Remove(int instanceId)
    {
        if (!membership.Remove(instanceId, out var entry)) return;
        Groups[entry.Group].Tasks[entry.Task].MemberList.Remove(instanceId);
    }

    private SimulatedActor? Member(int instanceId) => world.Actor(instanceId);

    // ---- methods ----

    /// <summary>Method <c>+0</c>: <c>0x4578A0</c> clears the budget; the scouts' <c>0x459F24</c> adds to it.</summary>
    private void PreGroup(int group)
    {
        var state = Groups[group];
        if (group != ScoutGroup)
        {
            state.Budget = 0;
            return;
        }
        // 0x4563E0 holds 40, 30, 20 and 0 for groups 0-3.
        state.Budget += state.Tasks[0].MemberList.Count == 0 ? 1000 : 0;
    }

    /// <summary>
    /// Method <c>+0xC</c> (<c>0x44BA48</c>): dying members leave their task.
    /// Groups 0 and 3 purge task 0 only (<c>0x44BBDC</c>), the armies every
    /// active task (<c>0x44BBEC</c>). A member that left the world without
    /// dying (which the executable asserts never happens) leaves too.
    /// </summary>
    private void PurgeGroup(int group)
    {
        var tasks = Groups[group].Tasks;
        for (var index = 0; index < TaskCount; index++)
        {
            if (index > 0 && group is HarvestGroup or ScoutGroup) break;
            if (!tasks[index].Active) continue;
            foreach (var id in tasks[index].MemberList.ToArray())
                if (Member(id) is not { } actor || actor.IsDestroyed) Remove(id);
        }
    }

    /// <summary>Method <c>+4</c>.</summary>
    private void UpdateGroup(int group)
    {
        if (group == GuardGroup) UpdateGuards();
        else if (group == AttackGroup) UpdateAttackers();
    }

    /// <summary>Method <c>+8</c>.</summary>
    private void PostGroup(int group)
    {
        switch (group)
        {
            case HarvestGroup:
                PostHarvesters();
                break;
            case ScoutGroup:
                PostScouts();
                break;
            default:
                // 0x463E78: every active task moves along its route.
                for (var task = 0; task < TaskCount; task++)
                    if (Groups[group].Tasks[task].Active) PostArmyTask(group, task);
                break;
        }
    }

    /// <summary>
    /// Method <c>+0x10</c>: the group's members by category. The harvesters
    /// (<c>0x459D98</c>) count every task 0 member as a harvester, deployed or not.
    /// </summary>
    private void CountGroup(int group)
    {
        var state = Groups[group];
        Array.Clear(state.Counts);
        if (group == HarvestGroup)
        {
            state.Counts[HarvesterCategory] = state.Tasks[0].MemberList.Count;
            return;
        }
        // 0x44B6A4
        foreach (var task in state.Tasks)
        {
            Array.Clear(task.CountsByCategory);
            if (!task.Active) continue;
            foreach (var id in task.MemberList)
            {
                if (Member(id) is not { } actor) continue;
                var category = Category(TypeOf(actor));
                task.CountsByCategory[category]++;
                state.Counts[category]++;
            }
        }
    }

    /// <summary>Method <c>+0x14</c>: the task a new member of the category joins.</summary>
    private int AcceptUnit(int group, int category)
    {
        if (group is HarvestGroup or ScoutGroup) return 0;
        var state = Groups[group];
        var best = NoChoiceScore;
        var bestTask = -1;
        for (var index = 0; index < TaskCount; index++)
        {
            var task = state.Tasks[index];
            if (!task.Active) continue;
            // 0x459660 weighs the guard posts double; 0x458F3C weighs every
            // task by its position and all but gathering ones four times.
            var score = group == GuardGroup
                ? task.CountsByCategory[category] * (index == 0 ? 1 : 2)
                : task.CountsByCategory[category] * (task.Mode == KrustyTaskMode.Gathering ? 1 : 4) * (index + 1);
            if (score < best)
            {
                best = score;
                bestTask = index;
            }
            else if (score == best && bestTask >= 0 && task.CountsByCategory.Sum() < state.Tasks[bestTask].CountsByCategory.Sum())
            {
                bestTask = index;
            }
        }
        if (bestTask < 0) bestTask = 0;
        state.Counts[category] = (ushort)(state.Counts[category] + 1);
        state.Tasks[bestTask].CountsByCategory[category] = (ushort)(state.Tasks[bestTask].CountsByCategory[category] + 1);
        return bestTask;
    }

    // ---- orders (0x40C414: command 7 waypoints, command 5 state) ----

    private void Order(int instanceId, CellCoordinate cell, bool attackMove) =>
        world.QueueNativeUnitOrder(attackMove ? new AttackMoveIntent(instanceId, cell) : new MoveIntent(instanceId, cell));

    private void TrackStuck(SimulatedActor actor, CellCoordinate cell, int? cap)
    {
        if (actor.KrustyLastCell == cell)
        {
            if (cap is null) actor.KrustyStuckChecks = (actor.KrustyStuckChecks + 1) & 0xFF;
            else if (actor.KrustyStuckChecks < cap) actor.KrustyStuckChecks++;
        }
        else
        {
            actor.KrustyStuckChecks = 0;
            actor.KrustyLastCell = cell;
        }
    }

    // ---- group 0: harvesters (0x4598B0) ----

    private void PostHarvesters()
    {
        var members = Groups[HarvestGroup].Tasks[0].MemberList.ToArray();
        var home = Groups[GuardGroup].Tasks[0].Target;
        foreach (var id in members)
        {
            if (Member(id) is not { } actor) continue;
            var cell = CellOf(actor);
            TrackStuck(actor, cell, cap: null);
            var type = TypeOf(actor);
            if (type is not (HarvesterType or SlugType)) continue;
            if (actor.KrustyStuckChecks > VentStuckLimit)
            {
                actor.KrustyZone = 0;
                continue;
            }
            if (actor.KrustyZone == 0 || PathDanger(RegionOf(cell), actor.KrustyZone, actor.Seed.Team) == 0) continue;
            // Danger on the way to its vent: back to the home guard post.
            Order(id, Zones[home].Centroid, attackMove: false);
            actor.KrustyZone = 0;
        }

        SimulatedActor? idle = null;
        foreach (var id in members)
            if (Member(id) is { KrustyZone: 0 } actor) idle = actor;
        if (idle is null) return;

        var taken = new bool[ZoneCount];
        foreach (var id in members)
            if (Member(id) is { } actor) taken[actor.KrustyZone & 0xFF] = true;
        var best = UnreachedDistance;
        int? bestZone = null;
        CellCoordinate bestCell = default;
        var idleRegion = RegionOf(CellOf(idle));
        foreach (var vent in world.PetraVents)
        {
            // A paying vent nobody stands on, in a region no harvester has.
            if (vent.Rate == 0 || world.GroundOccupancy.IsOccupied(vent.Position)) continue;
            var region = RegionOf(vent.Position);
            if (taken[region] || Zones[region].Distance >= best) continue;
            if (PathDanger(idleRegion, region, idle.Seed.Team) != 0) continue;
            best = Zones[region].Distance;
            bestZone = region;
            bestCell = vent.Position;
        }
        if (bestZone is not { } zone) return;
        idle.KrustyZone = zone;
        Order(idle.Seed.InstanceId, bestCell, attackMove: false);
    }

    // ---- armies (krusty_army.c) ----

    /// <summary><c>0x463EEC</c>: a task starts on the first region within two hops of home.</summary>
    private void CreateArmyTask(int group, int index)
    {
        var region = 255;
        for (var candidate = 1; candidate < 255; candidate++)
            if (Zones[candidate].Distance < 3)
            {
                region = candidate;
                break;
            }
        var task = Groups[group].Tasks[index];
        task.RouteIndex = 0;
        task.Active = true;
        task.MemberList.Clear();
        task.Current = task.Target = region;
        task.Route[0] = (byte)region;
    }

    /// <summary><c>0x464074</c>: the members are released and start over.</summary>
    private void DeleteArmyTask(int group, int index)
    {
        var task = Groups[group].Tasks[index];
        task.Active = false;
        foreach (var id in task.MemberList)
        {
            membership.Remove(id);
            if (Member(id) is { } actor) actor.KrustyState = 0;
        }
        task.MemberList.Clear();
    }

    /// <summary><c>0x463840</c>: a new target, with the given route or the route-table walk.</summary>
    private void SetTaskRoute(int group, int index, int target, IReadOnlyList<byte>? route)
    {
        var task = Groups[group].Tasks[index];
        var steps = route ?? WalkRoute(task.Current, target);
        Array.Clear(task.Route);
        for (var step = 0; step < steps.Count && step < ZoneCount; step++)
        {
            task.Route[step] = steps[step];
            if (steps[step] == target) break;
        }
        task.RouteIndex = target != task.Current ? 1 : 0;
        task.Target = target;
    }

    /// <summary>
    /// <c>0x463A58</c>: members far from the task's region are sent there.
    /// When none of the arrived members is still on its way, the task moves
    /// one region along its route and sends everyone, attacking on the way.
    /// </summary>
    private void PostArmyTask(int group, int index)
    {
        var task = Groups[group].Tasks[index];
        var retarget = world.TakeKrustyRetarget();
        var busy = false;
        var current = task.Current;
        foreach (var id in task.MemberList.ToArray())
        {
            if (Member(id) is not { } actor) continue;
            if (actor.KrustyState == 0) actor.KrustyState = 2;
            var cell = CellOf(actor);
            var region = RegionOf(cell);
            TrackStuck(actor, cell, ArmyStuckLimit);
            var type = TypeOf(actor);
            if (type is 1 or 9 && HopDistance(region, current) < ArmyArrivalHops && actor.KrustyStuckChecks > 3)
            {
                // A turret or Xenowort that has settled near the post deploys (state 13).
                world.QueueNativeUnitOrder(new DeployTowerIntent(id));
                actor.KrustyStuckChecks = 0;
                continue;
            }
            if (HopDistance(region, current) > ArmyArrivalHops || retarget)
            {
                if (actor.KrustyStuckChecks < ArmyStuckLimit && actor.KrustyState != 2) busy = true;
                if (actor.KrustyZone == current) continue;
                actor.KrustyZone = current;
                Order(id, Zones[current].Centroid, attackMove: actor.KrustyState == 2);
            }
            else if (actor.KrustyState == 2)
            {
                actor.KrustyState = 1;
            }
        }
        if (busy) return;
        var next = task.Route[task.RouteIndex];
        if (next != task.Target) task.RouteIndex++;
        task.Current = next;
        foreach (var id in task.MemberList.ToArray())
        {
            if (Member(id) is not { } actor || actor.KrustyZone == next) continue;
            actor.KrustyZone = next;
            Order(id, Zones[next].Centroid, attackMove: true);
        }
    }

    /// <summary>
    /// <c>0x458680</c>: the arrived members' rating, using the Human entity of
    /// each category (types 0-8) whatever the race.
    /// </summary>
    private int TaskStrength(KrustyTask task)
    {
        var arrived = new int[CategoryCount];
        foreach (var id in task.MemberList)
            if (Member(id) is { KrustyState: 1 } actor) arrived[Category(TypeOf(actor))]++;
        if (world.KrustyDamage is null) return 0;
        var strength = 0;
        for (var category = 0; category < CategoryCount && category < world.KrustyEntityCount; category++)
        {
            var definition = world.KrustyEntity(category);
            if (definition.WeaponSlots[0] is var weaponId && weaponId == -1 || world.KrustyWeapon(weaponId) is not { } weapon) continue;
            strength += Strength(weapon.WeaponClass, definition.ArmorClass, arrived[category]);
        }
        return strength;
    }

    // ---- group 1: guards (0x4593A8) ----

    private void UpdateGuards()
    {
        var group = Groups[GuardGroup];
        if ((world.KrustyRandom() & 0xF) == 0)
        {
            // A new post within one hop of home: each candidate draws, and
            // replaces the choice when the draw divides by the choices so far.
            var best = -1;
            var choices = 1;
            for (var region = 1; region < 255; region++)
            {
                if (Zones[region].Distance >= 2) continue;
                if (world.KrustyRandom() % choices != 0) continue;
                choices++;
                best = region;
            }
            if (best != -1)
            {
                SetTaskRoute(GuardGroup, 0, best, null);
                world.SetKrustyRetarget();
            }
        }
        // One post per harvested vent region (and per protected region, which
        // only the unported messages 6-8 set).
        var guarded = new bool[ZoneCount];
        foreach (var id in Groups[HarvestGroup].Tasks[0].MemberList)
            if (Member(id) is { KrustyZone: not 0 } harvester) guarded[harvester.KrustyZone & 0xFF] = true;
        for (var index = 1; index < TaskCount; index++)
        {
            var task = group.Tasks[index];
            if (!task.Active) continue;
            if (!guarded[task.Target]) DeleteArmyTask(GuardGroup, index);
            guarded[task.Target] = false;
        }
        for (var region = 0; region < ZoneCount; region++)
        {
            if (!guarded[region]) continue;
            for (var index = 1; index < TaskCount; index++)
            {
                if (group.Tasks[index].Active) continue;
                CreateArmyTask(GuardGroup, index);
                SetTaskRoute(GuardGroup, index, region, null);
                break;
            }
        }
    }

    // ---- group 2: attackers (0x458B44) ----

    private void UpdateAttackers()
    {
        var guards = Groups[GuardGroup];
        var attackers = Groups[AttackGroup];
        var guardPosts = new int[ZoneCount];
        var reach = 0;
        for (var index = 0; index < TaskCount; index++)
        {
            var task = guards.Tasks[index];
            if (!task.Active) continue;
            guardPosts[task.Target] = index == 0 ? 1 : 2;
            reach = Math.Max(reach, Zones[task.Target].Distance);
        }
        reach += 3;
        // The executable indexes these two by task here, by region below.
        var targeted = new bool[ZoneCount];
        var holders = new int[ZoneCount];
        var activeTasks = 0;
        for (var index = 0; index < TaskCount; index++)
        {
            var task = attackers.Tasks[index];
            if (!task.Active) continue;
            activeTasks++;
            if (task.Mode == KrustyTaskMode.Attacking) targeted[index] = true;
            if (task.Mode is KrustyTaskMode.Guarding or KrustyTaskMode.Gathering) holders[index] = (byte)(holders[index] + 1);
        }
        for (var index = 0; index < TaskCount; index++)
        {
            var task = attackers.Tasks[index];
            if (!task.Active) continue;
            switch (task.Mode)
            {
                case KrustyTaskMode.Idle or KrustyTaskMode.Guarding:
                {
                    var strength = TaskStrength(task);
                    var (target, route) = ChooseAttackTarget(targeted, reach, strength, task.Current);
                    if (target != -1)
                    {
                        targeted[target] = true;
                        task.Mode = KrustyTaskMode.Attacking;
                        SetTaskRoute(AttackGroup, index, target, route);
                    }
                    else if (task.Mode == KrustyTaskMode.Idle)
                    {
                        task.Mode = KrustyTaskMode.Guarding;
                        SetTaskRoute(AttackGroup, index, ChooseGuardPost(guardPosts, holders, task.Current), null);
                        holders[task.Target] = (byte)(holders[task.Target] + 1);
                    }
                    break;
                }
                case KrustyTaskMode.Attacking:
                {
                    var strength = TaskStrength(task);
                    var danger = PathDanger(task.Current, task.Target, Player, task.Route.AsSpan(task.RouteIndex).ToArray());
                    // The flag test reads the region numbered like the task.
                    if (danger == 0 && (Zones[index].Flags & 1) == 0 && Zones[task.Target].Count == 0)
                        task.Mode = KrustyTaskMode.Idle;
                    else if (2 * strength <= danger)
                        task.Mode = KrustyTaskMode.Idle;
                    break;
                }
                case KrustyTaskMode.Gathering:
                    // Only unreachable code sets this mode; its reinforcement
                    // test (nTasks * 4 / (t + 1) per category) is not ported.
                    break;
            }
        }
    }

    /// <summary>
    /// <c>0x458814</c>: the region worth most: enemy presence within reach of
    /// home (closer is better) or unarmed enemy objects, times the task's
    /// strength over the danger on the way. Regions already targeted are skipped.
    /// </summary>
    private (int Target, IReadOnlyList<byte>? Route) ChooseAttackTarget(bool[] targeted, int reach, int strength, int from)
    {
        var best = -1;
        var bestScore = 0;
        IReadOnlyList<byte>? bestRoute = null;
        for (var region = 0; region < ZoneCount; region++)
        {
            if (targeted[region]) continue;
            var zone = Zones[region];
            var value = 0;
            var local = PathDanger(region, region, Player);
            if (zone.Distance <= reach && (local != 0 || (zone.Flags & 1) != 0))
                value = (zone.Flags & 2) != 0 ? reach / 2 : reach + 1 - zone.Distance;
            if (zone.Count > 0) value += reach / 2;
            if (value == 0) continue;
            var danger = PathDanger(from, region, Player);
            if (danger == 0) danger = 1;
            // With +0x6C34 nonzero (message 13, unused) a costlier search
            // (0x457CC0) would run here; it is zero in every shipped scenario.
            var score = strength * value / danger;
            if (score <= bestScore) continue;
            bestScore = score;
            best = region;
            bestRoute = WalkRoute(from, region);
        }
        return (best, bestRoute);
    }

    /// <summary><c>0x4584EC</c>: the guard post with the fewest attack tasks holding it, then the nearest.</summary>
    private int ChooseGuardPost(int[] guardPosts, int[] holders, int from)
    {
        var bestHops = UnreachedDistance;
        var bestHolders = 100;
        var best = -1;
        for (var region = 0; region < ZoneCount; region++)
        {
            if (guardPosts[region] == 0) continue;
            var hops = HopDistance(region, from);
            if (bestHolders > holders[region] || (bestHolders == holders[region] && hops < bestHops))
            {
                best = region;
                bestHops = hops;
                bestHolders = holders[region];
            }
        }
        return best < 0 ? from : best;
    }

    // ---- group 3: scouts (0x459F80) ----

    private void PostScouts()
    {
        var regions = world.KrustyRegions;
        foreach (var id in Groups[ScoutGroup].Tasks[0].MemberList.ToArray())
        {
            if (Member(id) is not { } actor) continue;
            var reorder = false;
            switch (actor.KrustyState)
            {
                case 0 or 4:
                    reorder = ScenarioSimulation.KrustyIdle(actor);
                    break;
                case 5:
                    if (ScenarioSimulation.KrustyIdle(actor)) actor.KrustyState = 6;
                    break;
                default:
                    if ((world.KrustyRandom() & 0x3F) == 0) reorder = true;
                    if (actor.KrustyHit) reorder = true;
                    break;
            }
            actor.KrustyHit = false;
            if (!reorder) continue;

            CellCoordinate target = default;
            var random = true;
            if ((world.KrustyRandom() & 1) == 0)
            {
                // The richest enemy region with no anti-air within two steps.
                var bestRegion = 0;
                var bestValue = 0;
                for (var region = 1; region < ZoneCount; region++)
                {
                    var marked = new bool[ZoneCount];
                    marked[region] = true;
                    foreach (var neighbor in regions.RegionNeighbors((byte)region)) marked[neighbor] = true;
                    var defended = false;
                    for (var near = 0; near < ZoneCount && !defended; near++)
                    {
                        if (!marked[near]) continue;
                        foreach (var neighbor in regions.RegionNeighbors((byte)near))
                            if (IsEnemy(Zones[neighbor].OwnerA))
                            {
                                defended = true;
                                break;
                            }
                    }
                    if (defended) continue;
                    var zone = Zones[region];
                    var value = 0;
                    if (IsEnemy(zone.OwnerB)) value = zone.StrengthB;
                    if (IsEnemy(zone.OwnerC)) value -= zone.StrengthC;
                    if (zone.Count != 0) value++;
                    if (value <= bestValue) continue;
                    bestRegion = region;
                    bestValue = value;
                }
                if (bestValue != 0)
                {
                    target = Zones[bestRegion].Centroid;
                    random = false;
                    actor.KrustyState = 5;
                }
            }
            if (random)
            {
                if ((world.KrustyRandom() & 3) != 0)
                {
                    target = RandomRegionCell();
                    actor.KrustyState = 4;
                }
                else
                {
                    // A random vent, then nudged away from enemy ground strength.
                    var vents = 0;
                    CellCoordinate? chosen = null;
                    foreach (var vent in world.PetraVents)
                    {
                        vents++;
                        if (0x100 / vents > (world.KrustyRandom() & 0xFF)) chosen = vent.Position;
                    }
                    if (chosen is not { } start)
                    {
                        target = RandomRegionCell();
                    }
                    else
                    {
                        actor.KrustyState = 4;
                        target = NudgeFromEnemies(start);
                    }
                }
            }
            var type = TypeOf(actor);
            Order(id, target, attackMove: type is not (5 or 13));
        }
    }

    private bool IsEnemy(int owner) => owner != NoOwner && owner != Player;

    /// <summary>A random cell off region 0 (rows below the top five), drawing x then z.</summary>
    private CellCoordinate RandomRegionCell()
    {
        var regions = world.KrustyRegions;
        while (true)
        {
            var x = world.KrustyRandom() % regions.Width;
            var z = world.KrustyRandom() % (regions.Height - 5);
            var cell = new CellCoordinate(x, z);
            if (regions.RegionAt(cell) != 0) return cell;
        }
    }

    /// <summary>
    /// Jitters the cell by up to 8 cells per axis until neither its region nor
    /// a neighbor has an enemy ground owner. The executable sums the owners'
    /// team numbers, so an enemy of team 0 never counts.
    /// </summary>
    private CellCoordinate NudgeFromEnemies(CellCoordinate cell)
    {
        var regions = world.KrustyRegions;
        var (x, z) = (cell.X, cell.Z);
        for (var attempt = 0; attempt < 0x10000; attempt++)
        {
            var region = regions.RegionAt(new CellCoordinate(x, z));
            if (region == 0) break;
            var owners = IsEnemy(Zones[region].OwnerB) ? Zones[region].OwnerB : 0;
            foreach (var neighbor in regions.RegionNeighbors(region))
                if (IsEnemy(Zones[neighbor].OwnerB)) owners += Zones[neighbor].OwnerB;
            if (owners == 0) break;
            x += (world.KrustyRandom() & 0xF) - 8;
            z += (world.KrustyRandom() & 0xF) - 8;
            if (x < 0) x = 0;
            if (z < 0) z = 0;
            if (x >= regions.Width) x = regions.Width - 1;
            if (z >= regions.Height) z = regions.Height - 1;
        }
        return new CellCoordinate(x, z);
    }
}
