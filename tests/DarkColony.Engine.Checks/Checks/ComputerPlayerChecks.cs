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
using static CheckHelpers;

/// <summary>The Krusty computer player.</summary>
internal static class ComputerPlayerChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("ComputerPlayer", name, tags, action);

        Check("computer players think on the native cadence", () =>
        {
            // Teams 1 and 2 run the Krusty profile (3), team 3 the passive profile (4).
            var catalog = EntityCatalog.Parse("1\nTRSC 0 10 47 10 8 -1 -1 -1 125 150 6 800 0 15 0 0 0 0 0 0 0 96 0 12 1 130 6 196 0 -1 -1 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n" +
                "TEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n0\n%Race\n0\n%Money\n3\n%AI\nTEAM 2 1\n0\n%Race\n0\n%Money\n3\n%AI\n" +
                "TEAM 3 1\n0\n%Race\n0\n%Money\n4\n%AI\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 4));
            Equal((0, 3, 3, 4), (simulation.AiProfile(0), simulation.AiProfile(1), simulation.AiProfile(2), simulation.AiProfile(3)));
            // 0x41AC2C: update 4 runs every computer player; later multiples of 4
            // advance one player slot each (slot 1 at update 8, slot 2 at 12, ...).
            for (var tick = 0; tick < 3; tick++) simulation.Step([]);
            Equal(false, simulation.KrustyState(1) is not null);
            simulation.Step([]);
            Equal((1, 1), (simulation.KrustyState(1)!.Thinks, simulation.KrustyState(2)!.Thinks));
            while (simulation.TickCount < 40) simulation.Step([]);
            Equal((3, 2), (simulation.KrustyState(1)!.Thinks, simulation.KrustyState(2)!.Thinks));
            Equal(false, simulation.KrustyState(3) is not null);
        });

        Check("Krusty regions use the PTH neighbor lists, hop counts, and two-step path danger", () =>
        {
            // A chain 1-2-3-4: each region's route to any other goes through its neighbor.
            var bytes = new byte[PathRegionMap.RouteTableSize + 4];
            for (var from = 1; from <= 4; from++)
                for (var to = 1; to <= 4; to++)
                    if (from != to) bytes[from * 256 + to] = (byte)(to > from ? from + 1 : from - 1);
            new byte[] { 1, 2, 3, 4 }.CopyTo(bytes, PathRegionMap.RouteTableSize);
            var map = PathRegionMap.Parse(bytes, 4, 1);
            // 0x442D1F: distinct next hops in order of first appearance.
            Equal(new byte[] { 2 }, map.RegionNeighbors(1).ToArray());
            Equal(new byte[] { 1, 3 }, map.RegionNeighbors(2).ToArray());
            Equal(0, map.RegionNeighbors(0).Count);
            var simulation = KrustyWorld("3 0 2 1 -1 0\n", map);
            var brain = new KrustyBrain(simulation, 1);
            Equal((3, 0, 0xFF), (brain.HopDistance(1, 4), brain.HopDistance(2, 2), brain.HopDistance(0, 3)));
            // Hop distances from home (region 1) and a cell of each region.
            Equal(new[] { 0, 1, 2, 3 }, brain.Zones.Skip(1).Take(4).Select(zone => zone.Distance).ToArray());
            Equal(new CellCoordinate(2, 0), brain.Zones[3].Centroid);
            // 0x45817C: the route, its neighbors, and theirs (two steps).
            brain.Zones[4].OwnerB = 0;
            brain.Zones[4].StrengthB = 50;
            Equal((50, 50, 0, 0), (brain.PathDanger(1, 4, 1), brain.PathDanger(1, 2, 1), brain.PathDanger(1, 1, 1), brain.PathDanger(1, 4, 0)));
            Equal(-1, brain.PathDanger(0, 4, 1));
        });

        Check("Krusty influence lets the stronger side own a region's slots and marks contested ones", () =>
        {
            // 0x456AD0, in slot order: the Human soldier claims the region, the first
            // Krusty soldier cancels it and takes the slots, the second adds to them.
            var simulation = KrustyWorld("1 1 0 0 -1 0\n2 1 2 1 -1 0\n3 1 2 1 -1 0\n");
            for (var tick = 0; tick < 4; tick++) simulation.Step([]);
            var zone = simulation.KrustyState(1)!.Zones[1];
            // Ground strength 25 * M[0][1] / M[1][1]; anti-air M[0][2]; both 8.8 factors 256.
            Equal((1, 25, 1, 256, 0xFF), (zone.OwnerB, zone.StrengthB, zone.OwnerA, zone.StrengthA, zone.OwnerC));
            Equal(1, zone.Flags & 1);
            Equal(0, simulation.KrustyState(1)!.PathDanger(1, 1, 1));
        });

        Check("Krusty hands harvesters, fliers, and a quarter of the rest to their groups", () =>
        {
            // 0x457568 with the 0xC0 ratio: guards take a unit while 3 x their count
            // stays within the attackers'. 0x458F3C spreads attackers over tasks 0/1
            // by count x 4 x (task + 1).
            var simulation = KrustyWorld("1 1 2 1 -1 0\n2 1 2 1 -1 0\n3 1 2 1 -1 0\n4 1 2 1 -1 0\n5 1 6 1 -1 0\n6 1 5 1 -1 0\n");
            for (var tick = 0; tick < 4; tick++) simulation.Step([]);
            var brain = simulation.KrustyState(1)!;
            Equal(new[] { 1 }, brain.Groups[1].Tasks[0].Members.ToArray());
            Equal(new[] { 4, 2 }, brain.Groups[2].Tasks[0].Members.ToArray());
            Equal(new[] { 3 }, brain.Groups[2].Tasks[1].Members.ToArray());
            Equal(new[] { 5 }, brain.Groups[0].Tasks[0].Members.ToArray());
            Equal(new[] { 6 }, brain.Groups[3].Tasks[0].Members.ToArray());
            Equal((1, 0), brain.MembershipOf(1)!.Value);
            // The census counts every harvester-group member as a harvester.
            Equal(1, brain.Groups[0].CategoryCounts[6]);
        });

        Check("aimsg settings reach the Krusty planner", () =>
        {
            // 0x44BF54: value 0 sets the guard share in percent of 256; 14 an ally.
            var simulation = KrustyWorld("1 1 2 1 -1 0\n");
            for (var tick = 0; tick < 4; tick++) simulation.Step([]);
            var brain = simulation.KrustyState(1)!;
            Equal(true, brain.HandleMessage([0, 50]));
            Equal(128, brain.GuardShare);
            Equal(false, brain.HandleMessage([7, 1, 1]));
        });

        Check("human05's Krusty base builds, trains, harvests, guards its vents and attacks", () =>
        {
            // Team 2 (Gray, profile 3, 4000 P7) with no player orders at all.
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var (simulation, _) = DeterminismHarness.Load(install, rules, "human/human05");
            var built = 0;
            var trained = 0;
            var attacked = false;
            for (var tick = 0; tick < 6000; tick++)
            {
                simulation.Step([]);
                built += simulation.LastBuildingPlacements.Count(placement => placement.TeamId == 2);
                trained += simulation.LastUnitProductions.Count(production => production.TeamId == 2 && production.Outcome == UnitProductionOutcome.Produced);
                attacked |= simulation.KrustyState(2)?.Groups[2].Tasks.Any(task => task.Active && task.Mode == KrustyTaskMode.Attacking) == true;
            }
            var brain = simulation.KrustyState(2)!;
            if (built < 2 || trained < 8) throw new InvalidOperationException($"Built {built} buildings and trained {trained} troops.");
            // Deployed harvesters keep their vent region; a guard task holds each.
            var vents = brain.Groups[0].Tasks[0].Members.Select(id => simulation.Actor(id)!).Where(actor => actor.DeployedEntityId is not null).ToArray();
            if (vents.Length == 0) throw new InvalidOperationException("No harvester deployed.");
            var guarded = brain.Groups[1].Tasks.Skip(1).Where(task => task.Active).Select(task => task.Target).ToHashSet();
            Equal(true, vents.All(actor => guarded.Contains(actor.KrustyZone)));
            Equal(true, attacked);
        }, CheckTags.Data | CheckTags.Slow);
    }
}
