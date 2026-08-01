using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Movement;
using DarkColony.Engine.World;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Commands;
using System.Buffers.Binary;

var failures = new List<string>();

Check("clock uses strict comparison", () =>
{
    var clock = new FixedStepClock(1_000);
    var ticks = 0;
    Equal(0, clock.Advance(1_066, () => ticks++));
    Equal(0, ticks);
    Equal(1, clock.Advance(1_067, () => ticks++));
    Equal(1, ticks);
});

Check("clock catches up deterministically", () =>
{
    var clock = new FixedStepClock(0);
    var ticks = 0;
    Equal(3, clock.Advance(199, () => ticks++));
    Equal(3, ticks);
    Equal(198L, clock.AccumulatedTimestamp);
});

Check("8.8 positions preserve cell centers", () =>
{
    var position = FixedPointPosition.AtCellCenter(new CellCoordinate(12, -3));
    Equal(12 * 256 + 128, position.XRaw);
    Equal(-3 * 256 + 128, position.ZRaw);
    Equal(new CellCoordinate(12, -3), position.Cell);
});

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

Check("native cell interpolation preserves integer residue", () =>
{
    var movement = new MovementState(new CellCoordinate(4, 7));
    var transition = NativeCellTransition.Begin(movement, PathDirection.East, 25);
    Equal(25, transition.VelocityXRaw);
    Equal(0, transition.VelocityZRaw);
    Equal(10, transition.RemainingExecutions);
    for (var index = 0; index < 10; index++) Equal(true, transition.Step());
    Equal(4 * 256 + 128 + 250, movement.VisualPosition.XRaw);
    Equal(new CellCoordinate(4, 7), movement.OccupiedCell);
    Equal(false, transition.Step());
    Equal(new CellCoordinate(5, 7), movement.OccupiedCell);
    Equal(4 * 256 + 128 + 250, movement.VisualPosition.XRaw);

    var diagonal = NativeCellTransition.Begin(new MovementState(new CellCoordinate(2, 2)), PathDirection.NorthWest, 25);
    Equal(-17, diagonal.VelocityXRaw);
    Equal(-17, diagonal.VelocityZRaw);
    Equal(14, diagonal.RemainingExecutions);
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

Check("scenario simulation consumes move intents and owns motion", () =>
{
    var entityText = "1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n";
    var catalog = EntityCatalog.Parse(entityText);
    const string scenarioText = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n";
    var scenario = ScenarioDefinition.Parse(scenarioText);
    var pathBytes = new byte[PathRegionMap.RouteTableSize + 16];
    pathBytes[1 * 256 + 1] = 1;
    pathBytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(scenario, catalog, PathRegionMap.Parse(pathBytes, 4, 4));
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

Check("scenario simulation chains movement beyond one packed segment", () =>
{
    var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n";
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

Check("scenario simulation waits then replans after a dynamic block", () =>
{
    var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n";
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

Check("world intents execute by tick then insertion order", () =>
{
    var simulation = new WorldSimulation();
    simulation.Commands.Enqueue(simulation.TickCount, 2, new MoveIntent(7, new CellCoordinate(5, 6)));
    simulation.Commands.Enqueue(simulation.TickCount, 1, new MoveIntent(8, new CellCoordinate(2, 3)));
    simulation.Commands.Enqueue(simulation.TickCount, 2, new MoveIntent(9, new CellCoordinate(7, 8)));
    simulation.Step();
    Equal(8, ((MoveIntent)simulation.LastCommands.Single().Command).EntityInstanceId);
    simulation.Step();
    Equal(new[] { 7, 9 }, simulation.LastCommands.Select(item => ((MoveIntent)item.Command).EntityInstanceId).ToArray());
    Equal(0, simulation.Commands.Count);
});

Check("world intents reject past and current ticks", () =>
{
    var queue = new WorldCommandQueue();
    var rejected = false;
    try { queue.Enqueue(3, 3, new MoveIntent(1, new CellCoordinate(0, 0))); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Equal(true, rejected);
});

Check("PTH converts bottom-up regions and follows coarse next nodes", () =>
{
    var data = new byte[PathRegionMap.RouteTableSize + 4];
    data[1 * 256 + 3] = 2;
    data[2 * 256 + 3] = 3;
    new byte[] { 3, 4, 1, 2 }.CopyTo(data, PathRegionMap.RouteTableSize);
    var path = PathRegionMap.Parse(data, 2, 2);
    Equal((byte)1, path.RegionAt(new CellCoordinate(0, 0)));
    Equal((byte)2, path.RegionAt(new CellCoordinate(1, 0)));
    Equal((byte)3, path.RegionAt(new CellCoordinate(0, 1)));
    var route = path.BuildCoarseRoute(1, 3);
    Equal(CoarseRouteTermination.ReachedTarget, route.Termination);
    Equal(new byte[] { 1, 2, 3 }, route.Regions.ToArray());
    Equal(CoarseRouteTermination.ZeroSentinel, path.BuildCoarseRoute(4, 3).Termination);
});

Check("spawn validity separates ground and alternate movement grids", () =>
{
    var data = new byte[PathRegionMap.RouteTableSize + 9];
    // Map-order rows become: 0 1 1 / 1 1 1 / 1 1 1.
    new byte[] { 1, 1, 1, 1, 1, 1, 0, 1, 1 }.CopyTo(data, PathRegionMap.RouteTableSize);
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

Check("autonomous groups seed populations without stacking", () =>
{
    var entityText = "2\nGROUND 1 1 1 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nBAT 1 1 1 1 1 -1 -1 -1 1 1 2 50 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n";
    var catalog = EntityCatalog.Parse(entityText);
    var pathBytes = new byte[PathRegionMap.RouteTableSize + 9];
    pathBytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var path = PathRegionMap.Parse(pathBytes, 3, 3);
    var ground = new CellOccupancy();
    var alternate = new CellOccupancy();
    var result = AutonomousSpawnSeeder.Seed(
        [new AutonomousSpawnGroup(0, new CellCoordinate(1, 1), 0, 2, 0),
         new AutonomousSpawnGroup(1, new CellCoordinate(1, 1), 1, 2, 0)],
        catalog, path, ground, alternate);
    Equal(4, result.Entities.Count);
    Equal(2, ground.Count);
    Equal(2, alternate.Count);
    Equal(new CellCoordinate(1, 1), result.Entities[0].SpawnCell);
    Equal(new CellCoordinate(0, 0), result.Entities[1].SpawnCell);
    Equal(AutonomousSpawnSeeder.InternalNeutralTeam, result.Entities[0].Team);
    Equal(0, result.MissingPopulationByGroup.Count);
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

Check("compressed SPR decodes literals and transparency", () =>
{
    var sprite = Sprite.Parse(SpriteFixture([0xfe, 0x01, 1, 1], 4, 1, compressed: true));
    Equal(Sprite.CompressedSignature, sprite.Signature);
    Equal((byte)255, sprite.Palette[1].Red);
    Equal(new byte[] { 0, 0, 1, 1 }, sprite.Frames[0].DecodeIndices());
});

Check("raw SPR decodes direct indices", () =>
{
    var sprite = Sprite.Parse(SpriteFixture([0, 1, 1, 0], 2, 2, compressed: false));
    Equal(Sprite.RawSignature, sprite.Signature);
    Equal(new byte[] { 0, 1, 1, 0 }, sprite.Frames[0].DecodeIndices());
});

var dataArgument = args.Length >= 2 && args[0] == "--data" ? args[1] : Path.Combine("..", "Dark Colony");
var dataPath = Path.GetFullPath(dataArgument);
if (File.Exists(Path.Combine(dataPath, "dc.exe")))
{
    Check("original damage matrix loads", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var matrix = DamageMatrix.Load(install.DataFile("gamestat", "mbullet.txt"));
        Equal(25, matrix[0, 0]);
        Equal(25, matrix.CalculateBaseDamage(100, 0, 0));
    });

    Check("installed MAP and BTS corpus parses", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var mapFiles = Directory.GetFiles(install.DataFile("scenario"), "*.map", SearchOption.AllDirectories);
        var btsFiles = Directory.GetFiles(install.DataFile("scenario"), "*.bts", SearchOption.AllDirectories);
        var cellCount = 0L;
        foreach (var file in mapFiles)
        {
            var map = TerrainMap.Load(file);
            cellCount += map.Cells.Count;
        }

        var tileCount = 0;
        foreach (var file in btsFiles) tileCount += BtsTileset.Load(file).Tiles.Count;
        if (mapFiles.Length == 0 || btsFiles.Length == 0 || cellCount == 0 || tileCount == 0)
        {
            throw new InvalidOperationException("Installed terrain corpus is empty.");
        }

        Console.WriteLine($"  terrain corpus: {mapFiles.Length} maps / {cellCount} cells / {btsFiles.Length} tilesets / {tileCount} tiles");
    });

    Check("installed PTH corpus matches MAP dimensions", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var files = Directory.GetFiles(install.DataFile("scenario"), "*.pth", SearchOption.AllDirectories);
        var zeroCells = 0L;
        var nonzeroCells = 0L;
        foreach (var file in files)
        {
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(file, map.Width, map.Height);
            for (var z = 0; z < path.Height; z++)
            for (var x = 0; x < path.Width; x++)
            {
                if (path.RegionAt(new CellCoordinate(x, z)) == 0) zeroCells++;
                else nonzeroCells++;
            }
        }

        Equal(101, files.Length);
        if (zeroCells == 0 || nonzeroCells == 0) throw new InvalidOperationException("PTH corpus lacks expected zero/nonzero regions.");
        Console.WriteLine($"  path corpus: {files.Length} files / {zeroCells} region-zero cells / {nonzeroCells} nonzero cells");
    });

    Check("training terrain viewport composes", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var map = TerrainMap.Load(install.DataFile("scenario", "test", "htrain1.map"));
        var tileset = BtsTileset.Load(install.DataFile("scenario", "htrain.bts"));
        var image = TerrainRasterizer.RenderViewport(map, tileset, 30 * 32, 22 * 32, 516, 458);
        Equal(516, image.Width);
        Equal(458, image.Height);
        Equal(516 * 458 * 4, image.Rgba.Length);
        if (!image.Rgba.Where((_, index) => index % 4 == 3).Any(alpha => alpha == 255))
        {
            throw new InvalidOperationException("Training terrain viewport is transparent.");
        }
    });

    Check("installed SCN corpus parses", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var files = Directory.GetFiles(install.DataFile("scenario"), "*.scn", SearchOption.AllDirectories);
        var placementCount = 0;
        var ventCount = 0;
        var autonomousGroupCount = 0;
        var autonomousPopulation = 0;
        var autonomousEntityIds = new HashSet<int>();
        foreach (var file in files)
        {
            var scenario = ScenarioDefinition.Load(file);
            placementCount += scenario.Placements.Count;
            ventCount += scenario.Vents.Count;
            autonomousGroupCount += scenario.AutonomousSpawnGroups.Count;
            autonomousPopulation += scenario.AutonomousSpawnGroups.Sum(group => group.DesiredPopulation);
            foreach (var group in scenario.AutonomousSpawnGroups)
            {
                autonomousEntityIds.Add(group.EntityId);
                if (group.DesiredPopulation < 0 || group.DesiredPopulation >= 10)
                    throw new InvalidOperationException($"{file} group {group.GroupId} has invalid population {group.DesiredPopulation}.");
            }
            if (scenario.AutonomousSpawnGroups.Count > 25)
                throw new InvalidOperationException($"{file} exceeds the native 25 autonomous-group limit.");
            if (string.IsNullOrWhiteSpace(scenario.Tileset)) throw new InvalidOperationException($"{file} has no tileset.");
        }

        var training = ScenarioDefinition.Load(install.DataFile("scenario", "test", "htrain1.scn"));
        Equal("htrain.bts", training.Tileset);
        Equal(2, training.Vents.Count);
        Equal(new[] { 23, 24, 25, 26, 36 }, autonomousEntityIds.Order().ToArray());
        Console.WriteLine($"  scenario corpus: {files.Length} files / {placementCount} raw placements / {ventCount} vents / {autonomousGroupCount} nature groups / {autonomousPopulation} desired actors");
    });

    Check("executable building footprints decode", () =>
    {
        var footprints = BuildingFootprintCatalog.Load(Path.Combine(dataPath, "dc.exe"));
        Equal(true, footprints.TryGetOffsets(16, out var humanBuilding));
        Equal(4, humanBuilding.Count);
        Equal(new CellCoordinate(-3, 0), humanBuilding[0]);
        Equal(new CellCoordinate(-2, 1), humanBuilding[^1]);
        Equal(false, footprints.TryGetOffsets(25, out _));
        Equal(
            new[] { new CellCoordinate(7, 20), new CellCoordinate(8, 20), new CellCoordinate(7, 21), new CellCoordinate(8, 21) },
            footprints.OccupiedCells(16, new CellCoordinate(10, 20)).ToArray());
    });

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
    });

    Check("installed scenarios seed world without footprint conflicts", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var footprints = BuildingFootprintCatalog.Load(Path.Combine(dataPath, "dc.exe"));
        var entityCount = 0;
        var occupiedCount = 0;
        foreach (var file in Directory.GetFiles(install.DataFile("scenario"), "*.scn", SearchOption.AllDirectories))
        {
            var world = ScenarioWorld.Create(ScenarioDefinition.Load(file), footprints);
            entityCount += world.Entities.Count;
            occupiedCount += world.StaticOccupancy.Count;
        }

        Equal(3_244, entityCount);
        // Shipped six-field SCN placements contain no unambiguous build-table
        // entities. Pedestals/vents are terrain or special records; buildings
        // enter occupancy through the runtime delivery system.
        Equal(0, occupiedCount);
        Console.WriteLine($"  seeded scenario worlds: {entityCount} entities / {occupiedCount} static occupied cells");
    });

    Check("installed scenarios create authoritative simulations", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
        var footprints = BuildingFootprintCatalog.Load(Path.Combine(dataPath, "dc.exe"));
        var actorCount = 0;
        foreach (var file in Directory.GetFiles(install.DataFile("scenario"), "*.scn", SearchOption.AllDirectories))
        {
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            try
            {
                actorCount += ScenarioSimulation.Create(ScenarioDefinition.Load(file), catalog, path, footprints).Actors.Count;
            }
            catch (InvalidDataException error)
            {
                throw new InvalidDataException($"{Path.GetRelativePath(install.ScenarioPath, file)}: {error.Message}", error);
            }
        }
        if (actorCount <= 3_244) throw new InvalidOperationException("Autonomous group population did not expand into actors.");
        Console.WriteLine($"  authoritative scenario simulations: {actorCount} seeded actors");
    });

    Check("installed SPR corpus decodes", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var files = Directory.GetFiles(install.RootPath, "*.spr", SearchOption.AllDirectories);
        var frameCount = 0;
        foreach (var file in files)
        {
            var sprite = Sprite.Load(file);
            foreach (var frame in sprite.Frames)
            {
                _ = frame.DecodeIndices();
                frameCount++;
            }
        }

        Equal(259, files.Length);
        Equal(8_092, frameCount);
    });

    Check("entity identities resolve through exact stand animations", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
        Equal(106, entities.Entities.Count);
        Equal("TRSC", entities[0].Code);
        Equal("Thunderbolt (Mortar)", entities[3].DisplayName);
        Equal("EXPL", entities[6].Code);
        Equal("Exploiter", entities[6].DisplayName);

        var animations = EntityAnimationCatalog.Build(entities, install.DataFile("animate"));
        var unresolved = entities.Entities.Where(entity => animations.Preferred(entity.Id) is null).ToArray();
        Equal(0, unresolved.Length);
        Equal("EXPLSTAND0", animations.Preferred(6)!.AnimationName);
        Equal("expl.fin", Path.GetFileName(animations.Preferred(6)!.FinPath).ToLowerInvariant());
        Equal("BARRSTAND0", animations.Preferred(3)!.AnimationName);
        var trooperMove = animations.PreferredMove(0, 1) ?? throw new InvalidOperationException("TRSC MOVE family missing.");
        Equal(false, trooperMove.ExactSector);
        Equal(0, trooperMove.AnimationSector);
        var lunaMove = animations.PreferredMove(65, 1) ?? throw new InvalidOperationException("LUNA MOVE family missing.");
        Equal(true, lunaMove.ExactSector);
        Equal("LUNAMOVE1", lunaMove.Candidate.AnimationName);
    });

    Check("menu bitmap font uses shipped metrics", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var font = new BitmapFont(
            Sprite.Load(install.DataFile("intrface", "mfonto5.spr")),
            frameOffset: 31,
            lineHeight: 14);
        Equal(123, font.Sprite.Frames.Count);
        Equal(34, font.FrameIndex('A'));
        Equal(7, font.Advance('A'));
        Equal(7, font.Advance(' '));
        Equal(5, font.Advance('!'));
    });

    Check("encyclopedia catalog preserves shipped identities", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var catalog = EncyclopediaCatalog.Load(install.DataFile("intrface", "encyclo.txt"));
        Equal(3, catalog.Categories.Count);
        Equal(10, catalog.Categories[0].Entries.Count);
        Equal(10, catalog.Categories[1].Entries.Count);
        Equal(5, catalog.Categories[2].Entries.Count);
        Equal("Exploiter", catalog.Categories[1].Entries[5].Name);
        Equal(15, catalog.Categories[1].Entries[5].NativeId);
    });

    Check("menu FIN animations compose", () =>
    {
        var install = GameInstallation.Open(dataPath);
        Sprite LoadSprite(string name)
        {
            foreach (var directory in new[] { "sprites", "intrface" })
            {
                var path = install.DataFile(directory, $"{name}.spr");
                if (File.Exists(path)) return Sprite.Load(path);
            }

            throw new FileNotFoundException($"Missing {name}.spr");
        }

        foreach (var (file, animationName) in new[]
        {
            ("dcss.fin", "DCSS"),
            ("knobe.fin", "LARGEBUTTON"),
            ("hcar.fin", "HLOOP"),
            ("acar.fin", "ALOOP"),
            ("acom.fin", "HCOM"),
            ("acom.fin", "ACOM"),
            ("gray.fin", "GRAYSTAND0"),
            ("atril.fin", "ATRILSTAND0"),
            ("scyth.fin", "SCYTHSTAND0"),
            ("ortu.fin", "ORTUMOVE0"),
            ("psyc.fin", "PSYCSTAND0"),
            ("slug.fin", "SLUGSTAND0"),
            ("xeno.fin", "XENOSTAND0"),
            ("slom.fin", "SLOMSTAND0"),
            ("sauc.fin", "EASY2"),
            ("zisp.fin", "ZISPSTAND0"),
            ("trooper1.fin", "TROOPER1STAND6"),
            ("barr.fin", "BARRSTAND0"),
            ("reap.fin", "REAPSTAND0"),
            ("scgm.fin", "SCGMMOVE0"),
            ("cyborg.fin", "CYBORGSTAND0"),
            ("expl.fin", "EXPLSTAND0"),
            ("turr.fin", "TURRSTAND0"),
            ("engi.fin", "ENGISTAND0"),
            ("drop.fin", "DROPSTAND0"),
            ("beon.fin", "BEONMOVE0"),
            ("tektara.fin", "TEKTARA"),
            ("mactor.fin", "MACTORSTAND0"),
            ("lens.fin", "LENSSTAND0"),
            ("luna.fin", "LUNAMOVE0"),
            ("hyyk.fin", "HYYKDEPLOY0"),
        })
        {
            var definition = AnimationDefinition.Load(install.DataFile("animate", file));
            var animation = definition.Animations.Single(item => item.Name == animationName);
            var frame = definition.Compose(animation.FirstFrame, LoadSprite);
            if (frame.Width <= 1 || frame.Height <= 1) throw new InvalidOperationException($"{animationName} composed empty.");
        }
    });
}
else
{
    Console.WriteLine($"SKIP original-data check: {dataPath}");
}

if (failures.Count != 0)
{
    Console.Error.WriteLine($"{failures.Count} check(s) failed:");
    failures.ForEach(Console.Error.WriteLine);
    return 1;
}

Console.WriteLine("All engine checks passed.");
return 0;

void Check(string name, Action action)
{
    try
    {
        action();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failures.Add($"FAIL {name}: {error.Message}");
    }
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (expected is Array expectedArray && actual is Array actualArray)
    {
        if (expectedArray.Length != actualArray.Length ||
            !expectedArray.Cast<object>().SequenceEqual(actualArray.Cast<object>()))
        {
            throw new InvalidOperationException("Arrays differ.");
        }

        return;
    }

    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static byte[] SpriteFixture(byte[] payload, ushort width, ushort height, bool compressed)
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
