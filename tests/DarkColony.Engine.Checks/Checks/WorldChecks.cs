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

/// <summary>Fixed-point positions, the update clock and intent order, day and night, critter groups, and team relations.</summary>
internal static class WorldChecks
{
    public static void Register(CheckSuite suite)
    {
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("World", name, tags, action);

        Check("day-night cycle exposes completed days for the native HUD counter", () =>
        {
            var cycle = new DayNightCycle(2);
            Equal(0UL, cycle.CompletedDays);
            cycle.Step();
            cycle.Step();
            Equal(DayNightPhase.Night, cycle.Phase);
            Equal(0UL, cycle.CompletedDays);
            cycle.Step();
            cycle.Step();
            Equal(DayNightPhase.Day, cycle.Phase);
            Equal(1UL, cycle.CompletedDays);
        });

        Check("SCN day-night header retains the executable's strict cycle and lighting ramp", () =>
        {
            var scenario = ScenarioDefinition.Parse("tiles.bts\ninternal\ndisplay\n0\n0\n2\n1\n1\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n");
            Equal(new ScenarioDayNight(0, 2, 1, 1), scenario.DayNight);
            var cycle = DayNightCycle.FromNativeScenario(scenario.DayNight);
            Equal(DayNightPhase.Day, cycle.Phase);
            Equal(0, cycle.LightingLevel);
            Equal(false, cycle.Step());
            Equal(2, cycle.PhaseTicks);
            Equal(true, cycle.Step());
            Equal(DayNightPhase.Night, cycle.Phase);
            Equal(0, cycle.PhaseTicks);
            Equal(0, cycle.LightingLevel);
            cycle.Step();
            Equal(256, cycle.LightingLevel);
        });

        Check("night greys the ground, the HUD clock turns, and lights brighten the ground", () =>
        {
            var install = GameInstallation.Open(suite.DataPath);
            var image = PeImage.Load(install.ExecutablePath);
            string Bytes(uint address, int length) => string.Join(' ', image.AtVirtualAddress(address, length).ToArray().Select(value => value.ToString("x2")));

            // 0x40AC6F: the terrain pass's colour is (lighting * 7) >> 8.
            Equal("8b 92 40 05 00 00", Bytes(0x40AC6F, 6));
            Equal("8d 04 d5 00 00 00 00 29 d0", Bytes(0x40AC75, 9));
            Equal("c1 f8 08", Bytes(0x40AC88, 3));
            Equal((0, 3, 7), (DayNightPresentation.TerrainColour(0), DayNightPresentation.TerrainColour(128), DayNightPresentation.TerrainColour(256)));

            // The jungle remap at brightness 16 keeps the ground's colours at
            // colour 0 and greys them at colour 7, without darkening them.
            var scenario = ScenarioDefinition.Load(install.DataFile("scenario", "alien", "alien07.scn"));
            var tileset = BtsTileset.Load(install.DataFile("scenario", scenario.Tileset));
            var name = Path.GetFileNameWithoutExtension(scenario.Tileset);
            var gif = GifPalette.Load(install.DataFile($"{name}.gif"));
            // The terrain reads the remap with the tileset's own colours, which
            // are the .gif's but for the 6- to 8-bit expansion. Index 0 is
            // transparent, and 138-143 (the interface font ramp) and 255 differ.
            Equal(true, Enumerable.Range(1, 254).Where(index => index is < 138 or > 143).All(index =>
                Math.Abs(tileset.Palette[index].Red - gif[index].Red) <= 3 &&
                Math.Abs(tileset.Palette[index].Green - gif[index].Green) <= 3 &&
                Math.Abs(tileset.Palette[index].Blue - gif[index].Blue) <= 3));
            var tables = NativeBlendTables.Load(install.DataFile($"{name}.rmp"), gif);
            (double Saturation, double Light) Remapped(int colour)
            {
                var saturation = new List<double>();
                var light = new List<double>();
                for (var index = 1; index < 256; index++)
                {
                    var (before, after) = (gif[index], gif[tables.Remap(FogShading.InSight, colour, (byte)index)]);
                    int Spread(VgaColor c) => Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                    if (Spread(before) > 30) saturation.Add(Spread(after) / (double)Spread(before));
                    if (before.Red + before.Green + before.Blue > 60) light.Add((after.Red + after.Green + after.Blue) / (double)(before.Red + before.Green + before.Blue));
                }
                return (saturation.Average(), light.Average());
            }
            var (day, night) = (Remapped(0), Remapped(7));
            Equal(true, day.Saturation > 0.95 && night.Saturation < 0.4);
            Equal(true, Math.Abs(day.Light - 1) < 0.05 && Math.Abs(night.Light - 1) < 0.1);

            // 0x43A9F8: the clock reads the night flag (+0x53C), the phase
            // ticks (+0x530) and the cycle limit (+0x534), and truncates (0x42B63A).
            Equal("83 ba 3c 05 00 00 00", Bytes(0x43AA05, 7));
            Equal("db 82 30 05 00 00", Bytes(0x43AA0E, 6));
            Equal("db 86 34 05 00 00", Bytes(0x43A9D3, 6));
            Equal("c6 44 24 01 1f", Bytes(0x42B643, 5));
            Equal("sprites/cloc", System.Text.Encoding.ASCII.GetString(image.AtVirtualAddress(0x47637C, 12)));
            Equal(36, Sprite.Load(install.DataFile("sprites", "cloc.spr")).Frames.Count);
            int Clock(int phase, int tick) => DayNightPresentation.ClockFrame(DayNightCycle.FromNativeScenario(new ScenarioDayNight(phase, 6750, tick, 75)), 36);
            // 6750 / 18 = 375 ticks a frame; the last tick of a phase is held back.
            Equal((0, 0, 1, 17), (Clock(0, 0), Clock(0, 374), Clock(0, 375), Clock(0, 6750)));
            Equal((18, 25, 35), (Clock(1, 0), Clock(1, 2700), Clock(1, 6750)));

            // Lights add pixel / 8 to the brightness, up to 31.
            Equal((31, 11, 16), (FogShading.AddLight(16, 160), FogShading.AddLight(10, 8), FogShading.AddLight(16, 0)));
            // NUKE lays a spot under its fire: its light layer alone holds
            // multiples of 8 up to 160, and the world composite leaves it out.
            Sprite Load(string sprite) => Sprite.Load(File.Exists(install.DataFile("sprites", sprite + ".spr"))
                ? install.DataFile("sprites", sprite + ".spr") : install.DataFile("intrface", sprite + ".spr"));
            var nuke = AnimationDefinition.Load(install.DataFile("animate", "nuke.fin"));
            var range = nuke.Animations.First(animation => animation.Name == "NUKE");
            var lit = Enumerable.Range(range.FirstFrame, range.LastFrame - range.FirstFrame + 1).First(frame => nuke.LogicalFrames[frame].Layers.Any(layer => layer.IsLight));
            var indices = Enumerable.Range(0, 256).Select(index => new VgaColor((byte)index, (byte)index, (byte)index)).ToArray();
            var light = nuke.Compose(lit, Load, bottomAnchored: true, palette: indices, lights: true);
            var pixels = Enumerable.Range(0, light.Width * light.Height).Where(pixel => light.Rgba[pixel * 4 + 3] != 0).Select(pixel => light.Rgba[pixel * 4]).ToArray();
            Equal(true, pixels.Length > 100 && pixels.All(value => value % 8 == 0 && value <= 160));
            var spot = Load("spot").Frames[0];
            Equal(true, light.Width >= spot.Width && light.Height >= spot.Height);
        }, CheckTags.Data);

        Check("8.8 positions preserve cell centers", () =>
        {
            var position = FixedPointPosition.AtCellCenter(new CellCoordinate(12, -3));
            Equal(12 * 256 + 128, position.XRaw);
            Equal(-3 * 256 + 128, position.ZRaw);
            Equal(new CellCoordinate(12, -3), position.Cell);
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

        Check("players and critter team 9 start mutually cooperative", () =>
        {
            // SCN loader 0x41B920: the matrix is zeroed, the diagonal set through
            // 0x41E7D8, then (0x41C00E) [player][9] = [9][player] = 1 for players 0-7.
            var relations = TeamRelationMatrix.CreateDefault();
            Equal(false, relations.IsHostile(0, 9));
            Equal(false, relations.IsHostile(9, 7));
            Equal(false, relations.IsHostile(3, 3));
            Equal(true, relations.IsHostile(0, 1));
            Equal(true, relations.IsHostile(8, 0));
        });

        Check("a Human shooter deals three quarters at night and a Gray one by day", () =>
        {
            // 0x4427AA: race 0 at night (+0x53C = 1) or race 1 by day sets the 3/4 flag of 0x441930.
            int Hit(int shooterRace, int initialPhase)
            {
                var catalog = EntityCatalog.Parse($"2\nATTACKER {shooterRace} 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
                var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 20 100 90 5 0 0 0 0 0\n");
                var matrix = DamageMatrix.Parse("10\n9\n" + string.Concat(Enumerable.Repeat("100 100 100 100 100 100 100 100 100 100\n", 9)));
                var source = $"t\ni\nd\n0\n{initialPhase}\n1000\n0\n1\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 1 0 0 -1 0\n2 1 1 1 -1 0\n";
                var bytes = new byte[PathRegionMap.RouteTableSize + 15]; bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
                var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 3), weaponCatalog: weapons, damageMatrix: matrix);
                simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
                for (var tick = 0; tick < 30 && simulation.LastProjectileImpacts.Count == 0; tick++) simulation.Step([]);
                return 100 - simulation.Actor(2)!.Health;
            }
            Equal((100, 75), (Hit(0, 0), Hit(0, 1)));
            Equal((75, 100), (Hit(1, 0), Hit(1, 1)));
            // Armor levels 1 and 2 use 25600 / gamestat values 9 and 10 (0x43BD46).
            var factors = DamageMatrix.Parse("10\n9\n" + string.Concat(Enumerable.Repeat("12 100 100 100 100 100 100 100 100 100\n", 9)));
            Equal(30, factors.NativeFactor(0, 0));
            Equal(((30 * 100 >> 8) * 0x100 >> 8) * (25600 / 125) >> 8, factors.CalculateNativeDamage(100, 0, 0, armorMultiplier: 25600 / 125));
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

        Check("autonomous groups seed populations without stacking", () =>
        {
            var entityText = "2\nGROUND 1 1 1 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nBAT 1 1 1 1 1 -1 -1 -1 1 1 2 50 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n";
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

        Check("autonomous groups issue recovered eight-tick wander orders", () =>
        {
            var catalog = EntityCatalog.Parse("1\nSALY 1 8 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n16 16 0 -1 1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 32 * 32];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 32, 32));
            var sawWander = false;
            for (var tick = 0; tick < 256; tick++)
            {
                simulation.Step([]);
                sawWander |= simulation.LastAutonomousWanders.Count != 0;
            }
            Equal(true, sawWander);
            var critter = simulation.Actors.Single(actor => actor.Seed.Team == AutonomousSpawnSeeder.InternalNeutralTeam);
            Equal(true, critter.MoveOrder is not null || critter.Movement.OccupiedCell != new CellCoordinate(16, 16));
        });

        Check("cell occupancy knows each owner's cells through claims, moves, overwrites and releases", () =>
        {
            var occupancy = new CellOccupancy();
            var random = new Random(3);
            CellCoordinate Cell() => new(random.Next(0, 6), random.Next(0, 6));
            void Agrees()
            {
                foreach (var owner in Enumerable.Range(1, 5))
                {
                    var claimed = occupancy.Claims.Where(claim => claim.Value == owner).Select(claim => claim.Key).ToHashSet();
                    if (!claimed.SetEquals(occupancy.CellsOf(owner)))
                        throw new InvalidOperationException($"owner {owner}: CellsOf disagrees with the claims");
                }
            }
            for (var operation = 0; operation < 3000; operation++)
            {
                var owner = random.Next(1, 6);
                switch (random.Next(5))
                {
                    case 0: occupancy.TryClaim(owner, [Cell(), Cell()]); break;
                    case 1: occupancy.TryMove(owner, Cell(), Cell()); break;
                    // Construction overwrites whoever held the cell.
                    case 2: occupancy.ReplaceClaims(owner, [Cell(), Cell(), Cell()]); break;
                    case 3: occupancy.ReleaseCell(Cell()); break;
                    default:
                        occupancy.Release(owner);
                        Equal(0, occupancy.CellsOf(owner).Count);
                        break;
                }
                Agrees();
            }
        });
    }
}
