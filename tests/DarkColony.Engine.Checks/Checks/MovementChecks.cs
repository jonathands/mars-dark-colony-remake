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

/// <summary>Paths, packed playback, occupancy, facing, waypoints, and blocked steps.</summary>
internal static class MovementChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Movement", name, tags, action);

        Check("movement reserves before visual arrival", () =>
        {
            var movement = new MovementState(new CellCoordinate(4, 7));
            movement.ReserveDestination(new CellCoordinate(5, 7));
            movement.AdvanceVisual(32, 0);
            Equal(new CellCoordinate(4, 7), movement.OccupiedCell);
            Equal(new CellCoordinate(5, 7), movement.ReservedDestination);
            Equal(4 * 256 + 160, movement.VisualPosition.XRaw);
            movement.CommitArrival();
            Equal(new CellCoordinate(5, 7), movement.OccupiedCell);
            Equal(4 * 256 + 160, movement.VisualPosition.XRaw);
        });

        Check("facing turns on the shorter wrapped arc and quantizes at render", () =>
        {
            var facing = new FacingState(250);
            facing.Face(PathDirection.SouthEast);
            Equal(true, facing.Step(10));
            Equal((byte)4, facing.Current);
            while (facing.Step(10)) { }
            Equal((byte)32, facing.Current);
            Equal(2, facing.RenderSector16);
            facing.Face(PathDirection.NorthWest);
            Equal(true, facing.Step(32));
            Equal((byte)64, facing.Current);

            facing.FaceTowards(FixedPointPosition.AtCellCenter(new CellCoordinate(1, 1)), FixedPointPosition.AtCellCenter(new CellCoordinate(1, 2)));
            Equal((byte)64, facing.Target);
            Equal((byte)13, NativeBearing.FromDelta(768, 256));
            Equal(new NativeDirectionVector(1944, 642), NativeBearing.Vector(13));
        });

        Check("packed playback reserves occupancy before interpolation", () =>
        {
            var occupancy = new CellOccupancy();
            occupancy.TryClaim(7, [new CellCoordinate(2, 2)]);
            var path = new PackedLocalPath();
            path.Append(PathDirection.East);
            var playback = new PackedPathPlayback(7, 25, new MovementState(new CellCoordinate(2, 2)), path, occupancy);
            Equal(PackedPathPlaybackStatus.ReservedStep, playback.Step());
            Equal(false, occupancy.IsOccupied(new CellCoordinate(2, 2)));
            Equal(true, occupancy.TryGetOwner(new CellCoordinate(3, 2), out var owner));
            Equal(7, owner);
            Equal(new CellCoordinate(2, 2), playback.Movement.OccupiedCell);
            for (var index = 0; index < 10; index++) Equal(PackedPathPlaybackStatus.Interpolating, playback.Step());
            Equal(PackedPathPlaybackStatus.Complete, playback.Step());
            Equal(new CellCoordinate(3, 2), playback.Movement.OccupiedCell);
        });

        Check("cancelled playback restores its source cell while it is free", () =>
        {
            var occupancy = new CellOccupancy();
            occupancy.TryClaim(7, [new CellCoordinate(2, 2)]);
            var path = new PackedLocalPath();
            path.Append(PathDirection.East);
            var playback = new PackedPathPlayback(7, 25, new MovementState(new CellCoordinate(2, 2)), path, occupancy);
            Equal(PackedPathPlaybackStatus.ReservedStep, playback.Step());
            playback.Cancel();
            Equal(new CellCoordinate(2, 2), playback.Movement.OccupiedCell);
            Equal(FixedPointPosition.AtCellCenter(new CellCoordinate(2, 2)), playback.Movement.VisualPosition);
            Equal(true, occupancy.TryGetOwner(new CellCoordinate(2, 2), out var owner));
            Equal(7, owner);
            Equal(false, occupancy.IsOccupied(new CellCoordinate(3, 2)));
        });

        Check("cancelled playback completes on its destination when the source was taken", () =>
        {
            // Found by the scripted determinism run (human01): an attacker's pursuit
            // was cancelled after another actor walked into the cell it had vacated.
            var occupancy = new CellOccupancy();
            occupancy.TryClaim(7, [new CellCoordinate(2, 2)]);
            var path = new PackedLocalPath();
            path.Append(PathDirection.East);
            var playback = new PackedPathPlayback(7, 25, new MovementState(new CellCoordinate(2, 2)), path, occupancy);
            Equal(PackedPathPlaybackStatus.ReservedStep, playback.Step());
            Equal(PackedPathPlaybackStatus.Interpolating, playback.Step());
            Equal(true, occupancy.TryClaim(8, [new CellCoordinate(2, 2)]));
            playback.Cancel();
            Equal(new CellCoordinate(3, 2), playback.Movement.OccupiedCell);
            Equal(new CellCoordinate(3, 2), playback.Movement.ReservedDestination);
            Equal(FixedPointPosition.AtCellCenter(new CellCoordinate(3, 2)), playback.Movement.VisualPosition);
            Equal(true, occupancy.TryGetOwner(new CellCoordinate(3, 2), out var mover));
            Equal(7, mover);
            Equal(true, occupancy.TryGetOwner(new CellCoordinate(2, 2), out var newcomer));
            Equal(8, newcomer);
        });

        Check("packed playback reports a contested destination", () =>
        {
            var occupancy = new CellOccupancy();
            occupancy.TryClaim(7, [new CellCoordinate(2, 2)]);
            occupancy.TryClaim(8, [new CellCoordinate(3, 2)]);
            var path = new PackedLocalPath();
            path.Append(PathDirection.East);
            var playback = new PackedPathPlayback(7, 25, new MovementState(new CellCoordinate(2, 2)), path, occupancy);
            Equal(PackedPathPlaybackStatus.Blocked, playback.Step());
            Equal(new CellCoordinate(3, 2), playback.BlockedCell ?? throw new InvalidOperationException("Blocked cell missing."));
            Equal(true, occupancy.TryGetOwner(new CellCoordinate(2, 2), out var owner));
            Equal(7, owner);
        });

        Check("local path uses decoded target-relative priority buckets", () =>
        {
            var bytes = new byte[PathRegionMap.RouteTableSize + 5 * 5];
            bytes[1 * 256 + 1] = 1;
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var map = PathRegionMap.Parse(bytes, 5, 5);
            var finder = new DiagnosticLocalPathfinder(map, new CellOccupancy(), new CellOccupancy());
            var route = finder.Find(new CellCoordinate(1, 1), new CellCoordinate(3, 3), 0, 7);
            Equal(PathDirection.SouthEast, route.Steps[0]);
            Equal(PathDirection.SouthEast, route.Steps[1]);
        });

        Check("scenario simulation consumes move intents and owns motion", () =>
        {
            var entityText = "1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n";
            var catalog = EntityCatalog.Parse(entityText);
            const string scenarioText = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n";
            var scenario = ScenarioDefinition.Parse(scenarioText);
            var pathBytes = new byte[PathRegionMap.RouteTableSize + 16];
            pathBytes[1 * 256 + 1] = 1;
            pathBytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(scenario, catalog, PathRegionMap.Parse(pathBytes, 4, 4));
            Equal(100, simulation.Actor(1)!.Health);
            Equal(100, simulation.Actor(1)!.MaximumHealth);
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(2, 1)))]);
            var actor = simulation.Actor(1) ?? throw new InvalidOperationException("Actor not seeded.");
            Equal(true, actor.Playback is not null);
            Equal(false, simulation.GroundOccupancy.IsOccupied(new CellCoordinate(1, 1)));
            Equal(true, simulation.GroundOccupancy.IsOccupied(new CellCoordinate(2, 1)));
            for (var index = 0; index < 10; index++) simulation.Step([]);
            simulation.Step([]);
            Equal(new CellCoordinate(2, 1), actor.Movement.OccupiedCell);
            Equal(1 * 256 + 128 + 250, actor.Movement.VisualPosition.XRaw);
        });

        Check("native bearings and direction vectors follow the quadrant tables", () =>
        {
            // 0x441504: X = cos, Z = sin, scale 2048, truncated before the sign.
            Equal(new NativeDirectionVector(2048, 0), NativeBearing.Vector(0));
            Equal(new NativeDirectionVector(1448, 1448), NativeBearing.Vector(32));
            Equal(new NativeDirectionVector(0, 2048), NativeBearing.Vector(64));
            Equal(new NativeDirectionVector(-1448, -1448), NativeBearing.Vector(160));
            Equal(new NativeDirectionVector(1892, -783), NativeBearing.Vector(240));
            // 0x4413A0: 256 facing units per turn, x toward 0, z toward 64.
            Equal(((byte)0, (byte)64, (byte)128, (byte)192, (byte)32), (NativeBearing.FromDelta(5, 0), NativeBearing.FromDelta(0, 5),
                NativeBearing.FromDelta(-5, 0), NativeBearing.FromDelta(0, -5), NativeBearing.FromDelta(3, 3)));
            Equal((ushort)604, (ushort)NativeBearing.ArctangentTable[128]);
        });

        Check("a move onto an occupied cell routes there and settles beside it", () =>
        {
            // The native search seeds the target, so an occupied destination still
            // has a route; the last step is blocked with no free cell left and the
            // target is jittered (entries 1 and 2: 0 % 3 - 1 = -1 each).
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 5 1 0 -1 0\n6 5 4 0 -1 0\n"),
                catalog, OpenPath(12, 12), randomTable: NativeRandomTable.FromValues(new uint[NativeRandomTable.Length]),
                targetRings: EuclideanRings());
            var mover = simulation.Actor(1)!;
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new MoveIntent(1, new CellCoordinate(6, 5)))]);
            Equal(true, mover.Playback is not null);
            for (var tick = 0; tick < 200 && mover.MoveOrder is not null; tick++) simulation.Step([]);
            Equal(true, mover.MoveOrder is null);
            Equal(new CellCoordinate(5, 4), mover.Movement.OccupiedCell);
        });

        Check("a ground move into region 0 heads for the first passable cell, x outer and z inner", () =>
        {
            // 0x414E32: ring 1 around (2, 2) is scanned from x = 1; (1, 1) is region 0.
            var bytes = new byte[PathRegionMap.RouteTableSize + 25];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            bytes[PathRegionMap.RouteTableSize + 2 * 5 + 2] = 0;
            bytes[PathRegionMap.RouteTableSize + 1 * 5 + 1] = 0;
            var simulation = KrustyWorld("4 4 2 0 -1 0\n", PathRegionMap.Parse(bytes, 5, 5));
            simulation.Step([new ScheduledWorldCommand(0, 0, new MoveIntent(1, new CellCoordinate(2, 2)))]);
            for (var tick = 0; tick < 200 && simulation.Actor(1)!.MoveOrder is not null; tick++) simulation.Step([]);
            Equal(new CellCoordinate(1, 2), simulation.Actor(1)!.Movement.OccupiedCell);
        });

        Check("scenario simulation chains movement beyond one packed segment", () =>
        {
            var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 45 * 3];
            bytes[1 * 256 + 1] = 1;
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 45, 3));
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(40, 1)))]);
            for (var tick = 0; tick < 600 && simulation.Actor(1)!.MoveOrder is not null; tick++) simulation.Step([]);
            var actor = simulation.Actor(1) ?? throw new InvalidOperationException("Actor missing.");
            Equal(new CellCoordinate(40, 1), actor.Movement.OccupiedCell);
            Equal(true, actor.MoveOrder is null);
        });

        Check("scenario simulation preserves queued move waypoints", () =>
        {
            var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 6 * 3];
            bytes[1 * 256 + 1] = 1;
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 6, 3));
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(2, 1)))]);
            simulation.Step([new ScheduledWorldCommand(2, 0, new MoveIntent(1, new CellCoordinate(4, 1), AppendWaypoint: true))]);
            Equal(1, simulation.Actor(1)!.MoveOrder!.PendingWaypointCount);
            for (var tick = 0; tick < 200 && simulation.Actor(1)!.MoveOrder is not null; tick++) simulation.Step([]);
            Equal(new CellCoordinate(4, 1), simulation.Actor(1)!.Movement.OccupiedCell);
            Equal(true, simulation.Actor(1)!.MoveOrder is null);
        });

        Check("queued duplicate waypoint does not discard later destinations", () =>
        {
            var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 6 * 3];
            bytes[1 * 256 + 1] = 1;
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 6, 3));
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(2, 1)))]);
            simulation.Step([
                new ScheduledWorldCommand(2, 0, new MoveIntent(1, new CellCoordinate(2, 1), AppendWaypoint: true)),
                new ScheduledWorldCommand(2, 1, new MoveIntent(1, new CellCoordinate(4, 1), AppendWaypoint: true)),
            ]);
            for (var tick = 0; tick < 250 && simulation.Actor(1)!.MoveOrder is not null; tick++) simulation.Step([]);
            Equal(new CellCoordinate(4, 1), simulation.Actor(1)!.Movement.OccupiedCell);
            Equal(true, simulation.Actor(1)!.MoveOrder is null);
        });

        Check("a replacement move finishes the in-flight step, then routes to its target", () =>
        {
            var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 5 * 5];
            bytes[1 * 256 + 1] = 1;
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 5));
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(4, 1)))]);
            var actor = simulation.Actor(1) ?? throw new InvalidOperationException("Actor missing.");
            var oldReserved = actor.Movement.ReservedDestination;
            Equal(true, oldReserved != actor.Movement.OccupiedCell);
            Equal(true, simulation.GroundOccupancy.IsOccupied(oldReserved));
            simulation.Step([new ScheduledWorldCommand(2, 0, new MoveIntent(1, new CellCoordinate(1, 3)))]);
            Equal(DiagnosticPathTermination.StepInFlight, simulation.LastMoveOutcomes.Single().PathTermination);
            Equal(true, simulation.GroundOccupancy.IsOccupied(oldReserved));
            for (var tick = 0; tick < 100 && actor.FinishingStep is not null; tick++) simulation.Step([]);
            Equal(oldReserved, actor.Movement.OccupiedCell);
            for (var tick = 0; tick < 250 && simulation.Actor(1)!.MoveOrder is not null; tick++) simulation.Step([]);
            Equal(new CellCoordinate(1, 3), simulation.Actor(1)!.Movement.OccupiedCell);
        });

        Check("active move order caps native waypoint list and ignores consecutive duplicates", () =>
        {
            var order = new ActiveMoveOrder(new CellCoordinate(1, 1));
            Equal(false, order.TryAppendWaypoint(new CellCoordinate(1, 1)));
            Equal(true, order.TryAppendWaypoint(new CellCoordinate(2, 1)));
            Equal(false, order.TryAppendWaypoint(new CellCoordinate(2, 1)));
            Equal(new[] { new CellCoordinate(2, 1) }, order.PendingWaypoints.ToArray());
            for (var index = 3; index <= 9; index++) Equal(true, order.TryAppendWaypoint(new CellCoordinate(index, 1)));
            Equal(ActiveMoveOrder.MaximumWaypoints, order.PendingWaypointCount);
            Equal(false, order.TryAppendWaypoint(new CellCoordinate(10, 1)));
        });

        Check("a stop lets the in-flight step finish on its destination cell", () =>
        {
            var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 5 * 3];
            bytes[1 * 256 + 1] = 1;
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 3));
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(3, 1)))]);
            var actor = simulation.Actor(1)!;
            var reserved = actor.Movement.ReservedDestination;
            Equal(true, reserved != actor.Movement.OccupiedCell);
            Equal(true, simulation.GroundOccupancy.IsOccupied(reserved));
            simulation.Step([new ScheduledWorldCommand(2, 0, new StopIntent(1))]);
            Equal(true, actor.Playback is null);
            Equal(true, actor.MoveOrder is null);
            Equal(true, actor.FinishingStep is not null);
            for (var tick = 0; tick < 100 && actor.FinishingStep is not null; tick++) simulation.Step([]);
            Equal(reserved, actor.Movement.OccupiedCell);
            Equal(reserved, actor.Movement.VisualPosition.Cell);
            Equal(true, simulation.GroundOccupancy.IsOccupied(reserved));
            Equal(false, simulation.GroundOccupancy.IsOccupied(new CellCoordinate(1, 1)));
        });

        Check("scenario simulation waits then replans after a dynamic block", () =>
        {
            var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 7 * 3];
            bytes[1 * 256 + 1] = 1;
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 7, 3));
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(5, 1)))]);
            // First segment reserves (2,1); block the following straight step.
            simulation.GroundOccupancy.ReplaceClaims(99, [new CellCoordinate(3, 1)]);
            for (var tick = 0; tick < 400 && simulation.Actor(1)!.MoveOrder is not null; tick++) simulation.Step([]);
            var actor = simulation.Actor(1) ?? throw new InvalidOperationException("Actor missing.");
            Equal(new CellCoordinate(5, 1), actor.Movement.OccupiedCell);
            Equal(true, actor.MoveOrder is null);
        });

        Check("scenario simulation repairs a blocked local route before waiting", () =>
        {
            var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 5 * 2];
            bytes[1 * 256 + 1] = 1;
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 2));
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(4, 0)))]);
            simulation.GroundOccupancy.ReplaceClaims(99, [new CellCoordinate(3, 0)]);
            var actor = simulation.Actor(1)!;
            for (var tick = 0; tick < 40 && actor.MoveOrder!.LastBlockedCell is null; tick++) simulation.Step([]);
            Equal(new CellCoordinate(3, 0), actor.MoveOrder!.LastBlockedCell ?? throw new InvalidOperationException("No blocked cell observed."));
            Equal(0, actor.MoveOrder.BlockedTicksRemaining);
            Equal(true, actor.Playback is not null);
        });

        Check("a blocked step whose repair fails waits four updates, then retries its kept steps", () =>
        {
            // A one-cell corridor: the step into (2,0) is blocked, the first free
            // remaining cell (3,0) has no route around the blocker, so 0x415458 waits
            // (type 3, counter 4) with the move record still holding its steps.
            var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 0 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 5];
            bytes[1 * 256 + 1] = 1;
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 1));
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(4, 0)))]);
            simulation.GroundOccupancy.ReplaceClaims(99, [new CellCoordinate(2, 0)]);
            var actor = simulation.Actor(1)!;
            for (var tick = 0; tick < 60 && actor.MoveOrder is { BlockedWaiting: false }; tick++) simulation.Step([]);
            var order = actor.MoveOrder ?? throw new InvalidOperationException("The move ended instead of waiting.");
            if (!order.BlockedWaiting)
                throw new InvalidOperationException($"No wait: at {actor.Movement.OccupiedCell}, blocked {order.LastBlockedCell}, ticks {order.BlockedTicksRemaining}, target {order.Target}, playback {actor.Playback is not null}.");
            Equal(new CellCoordinate(1, 0), actor.Movement.OccupiedCell);
            Equal(new[] { PathDirection.East, PathDirection.East, PathDirection.East }, order.KeptSteps.ToArray());
            for (var update = 0; update < 4; update++)
            {
                simulation.Step([]);
                Equal(true, order.BlockedWaiting);
            }
            // The blocker leaves; the fifth update ends the wait and the kept steps run on.
            simulation.GroundOccupancy.Release(99);
            simulation.Step([]);
            Equal(false, order.BlockedWaiting);
            Equal(true, actor.Playback is not null);
            for (var tick = 0; tick < 200 && actor.MoveOrder is not null; tick++) simulation.Step([]);
            Equal(new CellCoordinate(4, 0), actor.Movement.OccupiedCell);
        });

        Check("ordinary SCN construction replaces stacked occupancy owner", () =>
        {
            var occupancy = new CellOccupancy();
            occupancy.ReplaceClaims(4, [new CellCoordinate(3, 5)]);
            occupancy.ReplaceClaims(9, [new CellCoordinate(3, 5)]);
            Equal(1, occupancy.Count);
            Equal(true, occupancy.TryGetOwner(new CellCoordinate(3, 5), out var owner));
            Equal(9, owner);
        });

        Check("local path packs low nibble before high nibble", () =>
        {
            var path = new PackedLocalPath();
            path.Append(PathDirection.NorthWest);
            path.Append(PathDirection.SouthEast);
            path.Append(PathDirection.East);
            Equal(3, path.Count);
            Equal((byte)0x70, path.PackedBytes[0]);
            Equal((byte)0x04, path.PackedBytes[1]);
            Equal(PathDirection.NorthWest, path[0]);
            Equal(PathDirection.SouthEast, path[1]);
            Equal(new CellCoordinate(1, 0), path[2].Delta());
        });

        Check("local path enforces native 32-step limit", () =>
        {
            var path = new PackedLocalPath();
            for (var index = 0; index < PackedLocalPath.MaximumSteps; index++) path.Append(PathDirection.North);
            var rejected = false;
            try
            {
                path.Append(PathDirection.North);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Equal(true, rejected);
        });

        Check("PTH reads regions in file order and follows coarse next nodes", () =>
        {
            var data = new byte[PathRegionMap.RouteTableSize + 4];
            data[1 * 256 + 3] = 2;
            data[2 * 256 + 3] = 3;
            new byte[] { 3, 4, 1, 2 }.CopyTo(data, PathRegionMap.RouteTableSize);
            var path = PathRegionMap.Parse(data, 2, 2);
            // 0x442B7C fills navigation rows 0..height-1 in file order.
            Equal((byte)3, path.RegionAt(new CellCoordinate(0, 0)));
            Equal((byte)4, path.RegionAt(new CellCoordinate(1, 0)));
            Equal((byte)1, path.RegionAt(new CellCoordinate(0, 1)));
            Equal((byte)2, path.RegionAt(new CellCoordinate(1, 1)));
            var route = path.BuildCoarseRoute(1, 3);
            Equal(CoarseRouteTermination.ReachedTarget, route.Termination);
            Equal(new byte[] { 1, 2, 3 }, route.Regions.ToArray());
            Equal(CoarseRouteTermination.ZeroSentinel, path.BuildCoarseRoute(4, 3).Termination);
        });

        Check("spawn validity separates ground and alternate movement grids", () =>
        {
            var data = new byte[PathRegionMap.RouteTableSize + 9];
            // File-order rows: 0 1 1 / 1 1 1 / 1 1 1.
            new byte[] { 0, 1, 1, 1, 1, 1, 1, 1, 1 }.CopyTo(data, PathRegionMap.RouteTableSize);
            var path = PathRegionMap.Parse(data, 3, 3);
            var ground = new CellOccupancy();
            var alternate = new CellOccupancy();
            ground.TryClaim(10, [new CellCoordinate(1, 0)]);
            alternate.TryClaim(11, [new CellCoordinate(0, 0)]);
            var validator = new SpawnCellValidator(path, ground, alternate);
            Equal(false, validator.IsValid(new CellCoordinate(0, 0), 0));
            Equal(false, validator.IsValid(new CellCoordinate(1, 0), 0));
            Equal(true, validator.IsValid(new CellCoordinate(2, 0), 0));
            Equal(false, validator.IsValid(new CellCoordinate(0, 0), 2));
            Equal(true, validator.IsValid(new CellCoordinate(1, 0), 2));
            var nearest = validator.FindNearestValid(new CellCoordinate(0, 0), 0);
            Equal(new CellCoordinate(0, 1), nearest ?? throw new InvalidOperationException("No valid spawn cell found."));
        });

        Check("diagnostic local search preserves corner cutting and segment cap", () =>
        {
            var bytes = new byte[PathRegionMap.RouteTableSize + 40 * 40];
            bytes[1 * 256 + 1] = 1;
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var regions = PathRegionMap.Parse(bytes, 40, 40);
            var ground = new CellOccupancy();
            ground.TryClaim(2, [new CellCoordinate(1, 0), new CellCoordinate(0, 1)]);
            var finder = new DiagnosticLocalPathfinder(regions, ground, new CellOccupancy());
            var diagonal = finder.Find(new CellCoordinate(1, 1), new CellCoordinate(0, 0), 0, 1);
            Equal(DiagnosticPathTermination.ReachedTarget, diagonal.Termination);
            Equal(1, diagonal.Steps.Count);
            Equal(PathDirection.NorthWest, diagonal.Steps[0]);

            var longPath = finder.Find(new CellCoordinate(2, 2), new CellCoordinate(39, 39), 0, 1);
            Equal(DiagnosticPathTermination.SegmentLimit, longPath.Termination);
            Equal(PackedLocalPath.MaximumSteps, longPath.Steps.Count);
            Equal(PackedLocalPath.MaximumSteps + 1, longPath.Cells.Count);
        });

        Check("rebuilt bearing tables equal the executable's sine and arctangent words", () =>
        {
            var image = PeImage.Load(Path.Combine(dataPath, "dc.exe"));
            var sine = image.AtVirtualAddress(0x4796B8, 0x801 * 2).ToArray();
            var atan = image.AtVirtualAddress(0x47A6BA, 256 * 2).ToArray();
            var sineMismatches = Enumerable.Range(0, 0x801).Count(index =>
                System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(sine.AsSpan(index * 2)) != NativeBearing.QuarterSineTable[index]);
            var atanMismatches = Enumerable.Range(0, 256).Count(index =>
                System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(atan.AsSpan(index * 2)) != NativeBearing.ArctangentTable[index]);
            Equal((0, 0), (sineMismatches, atanMismatches));
        }, CheckTags.Data);

        Check("scenario world preserves anchors and proven occupancy", () =>
        {
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n10 20 16 0 300 1\n12 30 23 -1 100 0\n38 29 40 0 500\n";
            var footprints = BuildingFootprintCatalog.Load(Path.Combine(dataPath, "dc.exe"));
            var world = ScenarioWorld.Create(ScenarioDefinition.Parse(source), footprints);
            Equal(2, world.Entities.Count);
            Equal(1, world.Vents.Count);
            Equal(new FixedPointPosition(10 * 256 + 128, 20 * 256 + 128), world.Entities[0].Position);
            Equal(4, world.StaticOccupancy.Count);
            Equal(true, world.StaticOccupancy.TryGetOwner(new CellCoordinate(7, 20), out var owner));
            Equal(1, owner);
            Equal(false, world.StaticOccupancy.IsOccupied(new CellCoordinate(12, 30)));
        }, CheckTags.Data);
    }
}
