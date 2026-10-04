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

/// <summary>Sight, fog of war, and allied vision.</summary>
internal static class VisionChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Vision", name, tags, action);

        Check("alliance and vision bits count only when both players set them", () =>
        {
            var catalog = EntityCatalog.Parse("1\nSCOUT 0 255 25 2 2 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n8 1 0 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3));
            var farCell = new CellCoordinate(8, 1);
            Equal(false, simulation.IsCellVisibleToTeam(0, farCell));
            // One direction does nothing (0x41E820 needs both).
            simulation.SetAllianceBit(0, 1, true);
            simulation.SetVisionBit(0, 1, true);
            simulation.Step([]);
            Equal(true, simulation.TeamRelations.IsHostile(0, 1));
            Equal(false, simulation.SharesVision(0, 1));
            simulation.SetAllianceBit(1, 0, true);
            simulation.SetVisionBit(1, 0, true);
            simulation.Step([]);
            Equal(false, simulation.TeamRelations.IsHostile(0, 1));
            Equal(true, simulation.SharesVision(0, 1));
            // Team 1's unit at (8,1) now shows its surroundings to team 0.
            Equal(true, simulation.IsCellVisibleToTeam(0, farCell));
            // The Allies packet as a command: team 0 withdraws in the command phase,
            // after this update's relation refresh, so the next update applies it.
            simulation.Step([new ScheduledWorldCommand(0, 0, new AllianceIntent(0, 1, false))]);
            Equal((false, false), (simulation.TeamRelations.IsHostile(0, 1), simulation.OffersAlliance(0, 1)));
            simulation.Step([]);
            Equal((true, false), (simulation.TeamRelations.IsHostile(0, 1), simulation.SharesVision(0, 1)));
        });

        Check("native sight: tree occlusion, shaded cells, day/night blend and the 16-update refresh", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var trees = NativeVisionTrees.Load(install.ExecutablePath);
            const int size = 24;
            var viewer = new CellCoordinate(8, 12);
            var nodes = trees.Nodes(4);
            int NodeAt(int dx, int dz) => nodes.Select((node, index) => (node, index)).Single(entry => entry.node.DeltaX == dx && entry.node.DeltaZ == dz).index;
            bool Under(int index, int ancestor)
            {
                for (var parent = nodes[index].Parent; parent >= 0; parent = nodes[parent].Parent)
                    if (parent == ancestor) return true;
                return false;
            }
            // World cells; the MAP row of world z is size - 1 - z.
            var wall = new CellCoordinate(viewer.X + 2, viewer.Z);
            var nearShade = new CellCoordinate(viewer.X, viewer.Z - 1);
            var farShadeNode = nodes.Select((node, index) => (node, index)).First(entry => entry.node.Depth >= 2 && entry.node.DeltaX == 0 && entry.node.DeltaZ > 0);
            var farShade = new CellCoordinate(viewer.X, viewer.Z + farShadeNode.node.DeltaZ);
            var map = new byte[8 + size * size * 6];
            BinaryPrimitives.WriteUInt32LittleEndian(map, size);
            BinaryPrimitives.WriteUInt32LittleEndian(map.AsSpan(4), size);
            for (var z = 0; z < size; z++)
            for (var x = 0; x < size; x++)
            {
                var attribute = 8 + size * size * 4 + ((size - 1 - z) * size + x) * 2;
                var cell = new CellCoordinate(x, z);
                // Bit 7 lets sight through; bit 8 (shaded) cells are opaque in every shipped map.
                map[attribute] = (byte)(cell == wall || cell == nearShade || cell == farShade ? 0 : 0x80);
                map[attribute + 1] = (byte)(cell == nearShade || cell == farShade ? 1 : 0);
            }
            var catalog = EntityCatalog.Parse("1\nSCOUT 0 255 25 4 8 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n" +
                $"{viewer.X} {viewer.Z} 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + size * size];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            // Day at its last tick: lighting 0, so the radius is the day sight (4).
            var clock = DayNightCycle.FromNativeScenario(new ScenarioDayNight(0, 100, 100, 1));
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, size, size),
                dayNight: clock, terrain: TerrainMap.Parse(map), visionTrees: trees);
            var scout = simulation.Actors.Single();
            Equal(4, simulation.ObservationRange(scout));

            Equal(true, simulation.IsCellVisibleToTeam(0, wall));
            var wallIndex = NodeAt(2, 0);
            var hidden = nodes.Where((node, index) => Under(index, wallIndex)).ToList();
            Equal(true, hidden.Count > 0);
            foreach (var node in hidden)
                Equal(false, simulation.IsCellVisibleToTeam(0, new CellCoordinate(viewer.X + node.DeltaX, viewer.Z + node.DeltaZ)));
            // A shaded cell is seen at depth 1, not at depth 2 or more.
            Equal(1, nodes[NodeAt(0, -1)].Depth);
            Equal(true, simulation.IsCellVisibleToTeam(0, nearShade));
            Equal(false, simulation.IsCellVisibleToTeam(0, farShade));
            // Explored memory (bit 31) takes every reached cell, shaded or not.
            Equal(true, simulation.IsCellExploredByTeam(0, farShade));
            Equal(false, simulation.IsCellExploredByTeam(0, new CellCoordinate(viewer.X + hidden[0].DeltaX, viewer.Z + hidden[0].DeltaZ)));

            // Night begins next update and is fully dark one update later (night
            // sight 8), but the stamps only refresh on update 16.
            var far = new CellCoordinate(viewer.X - 6, viewer.Z);
            Equal(false, simulation.IsCellVisibleToTeam(0, far));
            for (var update = 1; update < 16; update++) simulation.Step([]);
            Equal(8, simulation.ObservationRange(scout));
            Equal(false, simulation.IsCellVisibleToTeam(0, far));
            simulation.Step([]);
            Equal(true, simulation.IsCellVisibleToTeam(0, far));
        }, CheckTags.Data);
    }
}
