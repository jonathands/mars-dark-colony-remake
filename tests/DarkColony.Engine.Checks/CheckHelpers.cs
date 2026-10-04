using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Missions;
using DarkColony.Engine.Movement;
using DarkColony.Engine.World;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Commands;
using DarkColony.Engine.Interface;
using DarkColony.Engine.Audio;
using DarkColony.Engine.Video;
using DarkColony.Engine.Network;
using System.Buffers.Binary;

/// <summary>Fixtures and assertions shared by the check files.</summary>
internal static class CheckHelpers
{
    // Shared fixtures for the automatic target selection checks below.
    public const string AcquisitionHeader = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n";

    public const string AcquisitionEntities = "5\n" +
        "GUARD 0 255 25 8 8 1 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
        "INTRUDER 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
        "SHOOTER 0 255 25 8 8 2 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
        "NEARSIGHT 0 255 25 1 1 1 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
        "PROP 0 1 1 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1\n";

    public const string AcquisitionWeapons = "2\n1 BULLET 0 0 1 10 90 3 0 0 0 0 0\n2 BULLET 0 0 1 10 90 8 0 0 0 0 0\n";

    // A Krusty test world: entity ids 0-7 follow the native categories (2 a
    // ground fighter, 5 a flier, 6 the harvester), one region unless given a PTH.
    public static string KrustyEntity(string code, int weapon, int movementClass) =>
        $"{code} 0 255 25 4 4 {weapon} -1 -1 1 1 1 100 {movementClass} 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0";

    public static EntityCatalog KrustyCatalog() => EntityCatalog.Parse("8\n" + string.Join('\n',
        KrustyEntity("TRSC", 1, 0), KrustyEntity("TURR", -1, 0), KrustyEntity("REAP", 1, 0), KrustyEntity("BARR", 1, 0),
        KrustyEntity("SARG", 1, 0), KrustyEntity("SCGM", 1, 1), KrustyEntity("EXPL", -1, 0), KrustyEntity("ATRIL", -1, 0)) + "\n");

    public static ScenarioSimulation KrustyWorld(string placements, PathRegionMap? map = null) => ScenarioSimulation.Create(
        ScenarioDefinition.Parse("tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n" +
            "TEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n0\n%Race\n0\n%Money\n3\n%AI\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n" + placements),
        KrustyCatalog(), map ?? OpenPath(8, 4),
        weaponCatalog: WeaponCatalog.Parse("1\n1 BULLET 0 0 20 100 90 5 0 0 0 0 0\n"),
        damageMatrix: DamageMatrix.Parse("10\n9\n" + string.Concat(Enumerable.Repeat("100 100 100 100 100 100 100 100 100 100\n", 9))));

    // Whole-distance rings (r <= d < r + 1) shaped like dc.exe's 0x434090 table, in a synthetic
    // (Z, then X) order, for checks that run without the executable.
    public static NativeTargetRings EuclideanRings()
    {
        var rings = Enumerable.Range(0, NativeTargetRings.MaximumRing + 1).Select(_ => new List<CellCoordinate>()).ToArray();
        for (var dz = -NativeTargetRings.MaximumRing; dz <= NativeTargetRings.MaximumRing; dz++)
        for (var dx = -NativeTargetRings.MaximumRing; dx <= NativeTargetRings.MaximumRing; dx++)
        {
            var ring = (int)Math.Floor(Math.Sqrt(dx * dx + dz * dz));
            if (ring <= NativeTargetRings.MaximumRing) rings[ring].Add(new CellCoordinate(dx, dz));
        }
        return NativeTargetRings.FromRings(rings);
    }

    public static PathRegionMap OpenPath(int width, int height)
    {
        var bytes = new byte[PathRegionMap.RouteTableSize + width * height];
        bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
        return PathRegionMap.Parse(bytes, width, height);
    }

    public static int[] ValuesWith(int movementSpeed)
    {
        var values = new int[32];
        values[2] = movementSpeed;
        return values;
    }

    public static void Equal<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "") where T : notnull
    {
        if (expected is Array expectedArray && actual is Array actualArray)
        {
            if (expectedArray.Length != actualArray.Length ||
                !expectedArray.Cast<object>().SequenceEqual(actualArray.Cast<object>()))
            {
                throw new InvalidOperationException($"Arrays differ ({Path.GetFileName(file)}:{line}).");
            }

            return;
        }

        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual} ({Path.GetFileName(file)}:{line}).");
        }
    }

    public static byte[] SpriteFixture(byte[] payload, ushort width, ushort height, bool compressed)
    {
        var frameDataBytes = compressed ? 4 + payload.Length : payload.Length;
        var data = new byte[8 + 768 + 8 + frameDataBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(data, compressed ? Sprite.CompressedSignature : Sprite.RawSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 1);
        var declared = compressed ? payload.Length : payload.Length + 12;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)declared);
        data[8 + 3] = 63;
        var descriptor = 8 + 768;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(descriptor), width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(descriptor + 2), height);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(descriptor + 4), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(descriptor + 6), 5);
        var position = descriptor + 8;
        if (compressed)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(position), (uint)payload.Length);
            position += 4;
        }

        payload.CopyTo(data, position);
        return data;
    }

    // Loads a campaign mission with its own script plus extra triggers that
    // give team 0 a strike force near the objective, so a headless run can
    // reach the script's own victory without a full playthrough.
    public static ScenarioSimulation LoadMissionWithExtraTriggers(GameInstallation install, SimulationRules rules, string scenario, string extraTriggers)
    {
        var file = install.DataFile(["scenario", .. scenario.Split('/')]) + ".scn";
        var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
        var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
        var source = File.ReadAllText(Path.ChangeExtension(file, ".tro"), System.Text.Encoding.Latin1);
        var script = MissionScript.Compile(ScenarioTriggers.Parse(source + "\n\n" + extraTriggers),
            MissionTripMap.Load(Path.ChangeExtension(file, ".mtg")));
        return ScenarioSimulation.Create(ScenarioDefinition.Load(file), path, rules, script, map);
    }

    // Every 16 updates, each idle armed mobile unit of the team (commanders
    // excluded) attacks the nearest live target.
    public static void Strike(ScenarioSimulation simulation, int team, Func<SimulatedActor, bool> isTarget, Func<bool> done, int maxTicks)
    {
        for (var tick = 0; tick < maxTicks && !done(); tick++)
        {
            var commands = new List<ScheduledWorldCommand>();
            if (tick % 16 == 0)
            {
                var targets = simulation.Actors.Where(actor => !actor.IsDestroyed && isTarget(actor)).ToArray();
                foreach (var attacker in simulation.Actors.Where(actor => !actor.IsDestroyed && actor.Seed.Team == team &&
                             actor.AttackTargetInstanceId is null && simulation.EffectiveDefinition(actor).MovementSpeed > 0 &&
                             simulation.EffectiveDefinition(actor).WeaponSlots[0] >= 0 &&
                             simulation.EffectiveDefinition(actor).Id is < 69 or > 76))
                {
                    var from = attacker.Movement.OccupiedCell;
                    var target = targets.OrderBy(candidate => Math.Abs(candidate.Movement.OccupiedCell.X - from.X) +
                                                              Math.Abs(candidate.Movement.OccupiedCell.Z - from.Z)).FirstOrDefault();
                    if (target is not null)
                        commands.Add(new ScheduledWorldCommand(simulation.TickCount, (ulong)commands.Count,
                            new AttackIntent(attacker.Seed.InstanceId, target.Seed.InstanceId)));
                }
            }
            simulation.Step(commands);
        }
    }
}

internal sealed class FakeTriggerContext : ITriggerExpressionContext
{
    public int Clock => 3;
    public int NextRandom() => 0;
    public (int EntityType, int Team)? Unit => (0, 2);
    public int SlotHealth(int player, int slot) => player == 1 && slot == 3 ? 5 : 0;
    public bool CellVisible(int x, int z, int player) => false;
    public int PlayerStat(int player, int stat) => player * 10 + stat - 13;
    public int TypeStat(int player, int stat, int entityType) => player * 10 + entityType;
    public bool MineAlive(int x, int z) => false;
    public int ScriptWord(int index) => 0;
}
