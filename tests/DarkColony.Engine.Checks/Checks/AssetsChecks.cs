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

/// <summary>Original data formats and catalogs: SPR, FIN, MAP, BTS, PTH, SCN, entity and sound tables.</summary>
internal static class AssetsChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Assets", name, tags, action);

        Check("the native animation clock skips frame 0 once, then loops it at its own ticks", () =>
        {
            // 0x425674: (d + 3) * 15 / 100 ticks, d 0 meaning 15; a zero byte lasts 256.
            Equal((2, 3, 256), (NativeAnimationTiming.FrameTicks(0), NativeAnimationTiming.FrameTicks(17), NativeAnimationTiming.FrameTicks(1704)));
            // 0x4264C8 with frames of 2, 1 and 3 ticks: step 1 shows frame 1.
            int[] ticks = [2, 1, 3];
            var loop = Enumerable.Range(0, 12).Select(step => NativeAnimationTiming.FrameAt(ticks, (ulong)step)).ToArray();
            Equal(new[] { 0, 1, 2, 2, 2, 0, 0, 1, 2, 2, 2, 0 }, loop);
            Equal(new[] { 1, 2, 2, 2, -1 }, Enumerable.Range(1, 5).Select(step => NativeAnimationTiming.FrameAt(ticks, (ulong)step, NativeAnimationMode.Once)).ToArray());
            Equal(2, NativeAnimationTiming.FrameAt(ticks, 50, NativeAnimationMode.Hold));
        });

        Check("troop build animations convert FIN frame delays to ticks like 0x425674", () =>
        {
            // The first tick leaves frame 0; each later frame lasts (d + 3) * 15 / 100
            // ticks with d = 0 read as 15; a zero result wraps the byte to 256.
            Equal(1, TroopBuildTimings.PlayOnceTicks([6]));
            Equal(3, TroopBuildTimings.PlayOnceTicks([0, 6, 6]));
            Equal(5, TroopBuildTimings.PlayOnceTicks([6, 13, 0]));
            Equal(1 + 256, TroopBuildTimings.PlayOnceTicks([6, 1]));
            Equal(1 + 1, TroopBuildTimings.PlayOnceTicks([6, 4]));
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

        Check("original entity counterparts are faction links rather than deployment targets", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            Equal("PSYC", entities[entities.Entities.Single(entity => entity.Code == "SARG").FactionCounterpartEntityId].Code);
            Equal("ZISP", entities[entities.Entities.Single(entity => entity.Code == "BEON").FactionCounterpartEntityId].Code);
            Equal("PSYCSTL", entities[entities.Entities.Single(entity => entity.Code == "SARGSTL").FactionCounterpartEntityId].Code);
            Equal(1, entities[49].AbilityChargeRecovery);
            Equal(1, entities[50].AbilityChargeRecovery);
        }, CheckTags.Data);

        Check("world sprites leave out light layers (FIN draw type 3), which the original only writes to its light map", () =>
        {
            var install = GameInstallation.Open(dataPath);
            Sprite Load(string name) => Sprite.Load(install.DataFile("sprites", $"{name}.spr"));
            // NUKE's frames: the nuke, spon and smae smoke (type 5) over the spot ellipse (type 3).
            var nuke = AnimationDefinition.Load(install.DataFile("animate", "nuke.fin"));
            var frame = nuke.LogicalFrames[5];
            Equal(new[] { ("nuke", 5), ("spon", 5), ("spot", 3), ("smae", 5) }, frame.Layers.Select(layer => (layer.SpriteName, layer.DrawType)).ToArray());
            // Alone, the spot composes as interface art but leaves a world frame empty.
            var spotArt = nuke.Compose(5, Load, includeLayer: layer => layer.IsLight);
            var spotWorld = nuke.Compose(5, Load, bottomAnchored: true, includeLayer: layer => layer.IsLight);
            Equal(true, spotArt.Width > 100 && spotArt.Rgba.Any(value => value != 0));
            Equal((1, 1, false), (spotWorld.Width, spotWorld.Height, spotWorld.Rgba.Any(value => value != 0)));
            Equal(true, nuke.Compose(5, Load, bottomAnchored: true).Width > 1);
        }, CheckTags.Data);

        Check("mirrored FIN layers draw flipped at their X - 1, opposite their source direction", () =>
        {
            var install = GameInstallation.Open(dataPath);
            Sprite Load(string name) => Sprite.Load(install.DataFile("sprites", $"{name}.spr"));
            // EXPLSTAND12 is expl frame 4 at FIN x -159 (frame x 133); EXPLSTAND4 is the
            // same frame with the mirror word set, at FIN x -29.
            var exploiter = AnimationDefinition.Load(install.DataFile("animate", "expl.fin"));
            CompositeFrame Stand(AnimationDefinition definition, string name) =>
                definition.Compose(definition.Animations.Single(animation => animation.Name == name).FirstFrame, Load, bottomAnchored: true);
            var left = Stand(exploiter, "EXPLSTAND12");
            var right = Stand(exploiter, "EXPLSTAND4");
            Equal((-26, 30), (left.X, left.X + left.Width));
            Equal((-30, 26), (right.X, right.X + right.Width));
            Equal((left.Y, left.Width, left.Height), (right.Y, right.Width, right.Height));
            for (var y = 0; y < left.Height; y++)
            for (var x = 0; x < left.Width; x++)
                Equal(left.Rgba[(y * left.Width + x) * 4 + 3], right.Rgba[(y * left.Width + left.Width - 1 - x) * 4 + 3]);

            // SCGMSTAND2 mixes mirrored and ordinary engine-glow layers; with the
            // rule, the hull and both glows keep their places in all four frames.
            var vtol = AnimationDefinition.Load(install.DataFile("animate", "scgm.fin"));
            var stand = vtol.Animations.Single(animation => animation.Name == "SCGMSTAND2");
            var extents = Enumerable.Range(stand.FirstFrame, stand.LastFrame - stand.FirstFrame + 1)
                .Select(frame => vtol.Compose(frame, Load, bottomAnchored: true))
                .Select(frame => (frame.X, frame.X + frame.Width)).ToArray();
            foreach (var (from, to) in extents)
                if (from < -22 || to > 38) throw new InvalidOperationException($"SCGMSTAND2 spans {from}..{to}");
        }, CheckTags.Data);

        Check("every curs.fin cursor centres on the pointer when composed like a world sprite", () =>
        {
            var install = GameInstallation.Open(dataPath);
            Sprite Load(string name) => Sprite.Load(install.DataFile("sprites", $"{name}.spr"));
            // The pointer is the FIN origin. Bottom-anchored, every frame's
            // centre lands on it: DEFAULT 31x31 at (-15,-15), MOVE 39x33 at
            // (-19,-16), DIG 43x42 at (-21,-21).
            var cursors = AnimationDefinition.Load(install.DataFile("animate", "curs.fin"));
            Equal((18, 38), (cursors.Animations.Count, cursors.LogicalFrames.Count));
            for (var frame = 0; frame < cursors.LogicalFrames.Count; frame++)
            {
                var cursor = cursors.Compose(frame, Load, bottomAnchored: true);
                if ((cursor.X, cursor.Y) != (-(cursor.Width / 2), -(cursor.Height / 2)))
                    throw new InvalidOperationException($"frame {frame}: ({cursor.X},{cursor.Y}) {cursor.Width}x{cursor.Height}");
            }
            // The interface layout hangs DEFAULT 22 pixels below the pointer.
            Equal((-15, 22), (cursors.Compose(0, Load).X, cursors.Compose(0, Load).Y));
        }, CheckTags.Data);

        Check("every troop's build animation is the one its production time comes from", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var animations = EntityAnimationCatalog.Build(entities, install.DataFile("animate"), EntityAnimationCatalog.LoadOrder(install.DataFile("anim.dat")));
            var timings = TroopBuildTimings.Load(entities, install);
            // The marine walks out of the barracks in hubu.fin, the Gray warrior out of its hive in albu.fin.
            Equal(("hubu.fin", "TRSCBUILD0"), (Path.GetFileName(animations.PreferredBuild(0)!.FinPath), animations.PreferredBuild(0)!.AnimationName));
            Equal(("albu.fin", "GRAYBUILDSTAND0"), (Path.GetFileName(animations.PreferredBuild(8)!.FinPath), animations.PreferredBuild(8)!.AnimationName));
            foreach (var entity in entities.Entities)
            {
                var build = animations.PreferredBuild(entity.Id);
                Equal(timings.BuildTicks(entity.Id) is not null, build is not null);
                if (build is null) continue;
                var definition = AnimationDefinition.Load(build.FinPath);
                var delays = Enumerable.Range(build.FirstFrame, build.LastFrame - build.FirstFrame + 1).Select(frame => definition.LogicalFrames[frame].Delay).ToArray();
                Equal(timings.BuildTicks(entity.Id) ?? -1, TroopBuildTimings.PlayOnceTicks(delays));
            }
        }, CheckTags.Data);

        Check("world sprites hang from their layer's bottom row and city art from the city origin", () =>
        {
            var install = GameInstallation.Open(dataPath);
            // TRSCSTAND0: one layer of trsc frame 0 (28 x 45, canvas x 147) at FIN
            // offset (-159, 4). The world blit (0x454751) puts the frame's bottom
            // row on the layer's Y, so the feet sit 4 pixels below the position.
            var definition = AnimationDefinition.Load(install.DataFile("animate", "trsc.fin"));
            Sprite Load(string name) => Sprite.Load(install.DataFile("sprites", $"{name}.spr"));
            var world = definition.Compose(0, Load, bottomAnchored: true);
            Equal((-12, -41, 28, 45), (world.X, world.Y, world.Width, world.Height));
            // The interface layout adds the frame's own Y instead.
            Equal(4 + 99, definition.Compose(0, Load).Y);

            // 0x4398AB: a city building's art hangs from the city origin corner,
            // not from its slot position (origin + 0x47AB70 offset).
            var rules = SimulationRules.Load(install);
            var (simulation, _) = DeterminismHarness.Load(install, rules, "mplayer/j4play01");
            var scenario = ScenarioDefinition.Load(install.DataFile("scenario", "mplayer", "j4play01") + ".scn");
            var team = scenario.Teams.First(candidate => candidate.HasCity && simulation.CityBuilding(candidate.TeamId, 0) is not null);
            var headquarters = simulation.CityBuilding(team.TeamId, 0)!;
            var origin = team.CityOrigin!.Value;
            Equal(new FixedPointPosition(origin.X * FixedPointPosition.One, origin.Z * FixedPointPosition.One),
                simulation.CityArtAnchor(headquarters.Seed.InstanceId)!.Value);
            Equal(true, headquarters.Movement.VisualPosition != simulation.CityArtAnchor(headquarters.Seed.InstanceId)!.Value);
            Equal(false, simulation.CityArtAnchor(simulation.Actors.First(actor => actor.Definition.MovementSpeed > 0).Seed.InstanceId).HasValue);
        }, CheckTags.Data);

        Check("the SCN loader swaps a placement of the other race for its counterpart", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var file = install.DataFile("scenario", "mplayer", "j4play01") + ".scn";
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            // Team 1 plays the alien race (1). Marines (entity 0, human) go to
            // team 1, to team 0 (human) and to the race-0 neutral team 4.
            var lines = File.ReadAllLines(file).ToList();
            lines[lines.IndexOf("TEAM 1 1") + 1] = "1";
            lines.AddRange(["2 2 0 1 -1 0", "3 2 0 0 -1 0", "4 2 0 4 -1 0"]);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(string.Join("\n", lines) + "\n"), path, rules, terrain: map);
            int EntityAt(int x, int team) =>
                simulation.Actors.Single(actor => actor.Seed.Team == team && actor.Seed.SpawnCell == new CellCoordinate(x, 2)).Seed.EntityId;

            var counterpart = rules.Entities[0].FactionCounterpartEntityId;
            Equal(true, counterpart >= 0 && rules.Entities[counterpart].Faction == 1);
            Equal(counterpart, EntityAt(2, 1));
            Equal(0, EntityAt(3, 0));
            Equal(0, EntityAt(4, 4));
        }, CheckTags.Data);

        Check("native random table reads the executable's shared stream", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var table = NativeRandomTable.Load(install.ExecutablePath);
            Equal(0x41c6u, table[0]);
            Equal(0x167eu, table[1]);
            Equal(0x2781u, table[2]);
            Equal(0x5d5eu, table[NativeRandomTable.Length - 1]);
        }, CheckTags.Data);

        Check("Cyborg and Psy-raider firing assets expose native common-fire variants", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var sarg = AnimationDefinition.Load(install.DataFile("animate", "sarg.fin"));
            var psyc = AnimationDefinition.Load(install.DataFile("animate", "psyc.fin"));
            Equal(true, sarg.Animations.Any(animation => animation.Name.StartsWith("SARGFIREA", StringComparison.Ordinal)));
            Equal(true, sarg.Animations.Any(animation => animation.Name.StartsWith("SARGFIREB", StringComparison.Ordinal)));
            Equal(true, psyc.Animations.Any(animation => animation.Name.StartsWith("PSYCFIRE", StringComparison.Ordinal)));
            Equal(false, psyc.Animations.Any(animation => animation.Name.StartsWith("PSYCFIREA", StringComparison.Ordinal) || animation.Name.StartsWith("PSYCFIREB", StringComparison.Ordinal)));
        }, CheckTags.Data);

        Check("original sound catalog loads", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var sounds = SoundCatalog.Load(install.DataFile("sound"));
            Equal(true, sounds.Sounds.ContainsKey(0));
            Equal("SOUND/UNIT.WAV", sounds.Sounds[0].RelativePath);
            Equal(4, sounds.For(0, "DEA").Count);
            Equal(28, sounds.For(0, "DEA")[0]);
            // The resource-harvester deployment event uses the original dedicated
            // DPY category, rather than the ordinary command acknowledgement.
            Equal(new[] { 104 }, sounds.For(6, "DPY").ToArray());
            Equal(new[] { 98 }, sounds.For(14, "DPY").ToArray());
            // State 18 uses weapon owners 50/51; their GUN lists select the
            // dedicated second-fire samples.
            Equal(new[] { 173 }, sounds.For(50, "GUN").ToArray());
            Equal(new[] { 174 }, sounds.For(51, "GUN").ToArray());
            Equal("SOUND/CY2NDFI.WAV", sounds.Sounds[173].RelativePath);
            Equal("SOUND/PSY2NDFI.WAV", sounds.Sounds[174].RelativePath);
            // Both shipped healer records route their contextual action through
            // the same dedicated HEAL.WAV, rather than an ordinary weapon sound.
            Equal(new[] { 138 }, sounds.For(49, "DPY").ToArray());
            Equal(new[] { 138 }, sounds.For(50, "DPY").ToArray());
        }, CheckTags.Data);

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
        }, CheckTags.Data);

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

            if (files.Length == 0) throw new InvalidOperationException("Installed PTH corpus is empty.");
            if (zeroCells == 0 || nonzeroCells == 0) throw new InvalidOperationException("PTH corpus lacks expected zero/nonzero regions.");
            Console.WriteLine($"  path corpus: {files.Length} files / {zeroCells} region-zero cells / {nonzeroCells} nonzero cells");
        }, CheckTags.Data);

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
        }, CheckTags.Data);

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
        }, CheckTags.Data);

        Check("SCN team blocks preserve postfix-labelled faction fields", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var scenario = ScenarioDefinition.Load(install.DataFile("scenario", "mplayer", "d2play01.scn"));
            Equal(0, scenario.Teams[0].Race ?? -1);
            Equal(1_500, scenario.Teams[0].StartingResource ?? -1);
            Equal(0, scenario.Teams[0].AiProfile ?? -1);
            Equal(0, scenario.Teams[0].TeamColor ?? -1);
            Equal(8, scenario.Teams[0].StartingDependencyFlags.Count);
            Equal(15, scenario.Teams[0].AllianceFlags.Count);
            Equal(1, scenario.Teams[1].Race ?? -1);
            Equal(0, scenario.EnabledTeamForRace(0)?.TeamId ?? -1);
            Equal(1, scenario.EnabledTeamForRace(1)?.TeamId ?? -1);
        }, CheckTags.Data);

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
        }, CheckTags.Data);

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

            if (files.Length == 0 || frameCount == 0)
                throw new InvalidOperationException("Installed SPR corpus is empty.");
        }, CheckTags.Data);

        Check("entity identities resolve through exact stand animations", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            Equal(106, entities.Entities.Count);
            Equal("TRSC", entities[0].Code);
            Equal("Thunderbolt (Mortar)", entities[3].DisplayName);
            Equal("EXPL", entities[6].Code);
            Equal("Exploiter", entities[6].DisplayName);

            var animations = EntityAnimationCatalog.Build(entities, install.DataFile("animate"),
                EntityAnimationCatalog.LoadOrder(install.DataFile("anim.dat")));
            // fill.fin is not in anim.dat, so the game takes FILL from fuel.fin.
            Equal("fuel.fin", Path.GetFileName(animations.Preferred(90)!.FinPath).ToLowerInvariant());
            var unresolved = entities.Entities.Where(entity => animations.Preferred(entity.Id) is null).ToArray();
            Equal(0, unresolved.Length);
            Equal("EXPLSTAND0", animations.Preferred(6)!.AnimationName);
            Equal("expl.fin", Path.GetFileName(animations.Preferred(6)!.FinPath).ToLowerInvariant());
            Equal("BEONFIREA0", animations.PreferredFire(49, 0)!.Candidate.AnimationName);
            Equal("ZISPFIREA0", animations.PreferredFire(50, 0)!.Candidate.AnimationName);
            var cyborg = entities.Entities.Single(entity => entity.Code == "SARG");
            Equal("SARGFIREA12", animations.PreferredFire(cyborg.Id, 0)!.Candidate.AnimationName);
            Equal("SARGFIREB12", animations.PreferredFire(cyborg.Id, 0, 1)!.Candidate.AnimationName);
            Equal("SARGFIREA12", animations.PreferredFire(cyborg.Id, 0, 2)!.Candidate.AnimationName);
            var psyRaider = entities.Entities.Single(entity => entity.Code == "PSYC");
            Equal("PSYCFIRE12", animations.PreferredFire(psyRaider.Id, 0, 99)!.Candidate.AnimationName);
            Equal("BARRSTAND0", animations.Preferred(3)!.AnimationName);
            var trooperFire = animations.PreferredFire(0, 0) ?? throw new InvalidOperationException("TRSC FIRE family missing.");
            Equal("TRSCFIREA12", trooperFire.Candidate.AnimationName);
            var grayHit = animations.PreferredHit(8, 0) ?? throw new InvalidOperationException("GRAY HIT family missing.");
            Equal("GRAYHITB12", grayHit.Candidate.AnimationName);
            var trooperMove = animations.PreferredMove(0, 1) ?? throw new InvalidOperationException("TRSC MOVE family missing.");
            Equal(true, trooperMove.ExactSector);
            Equal(10, trooperMove.AnimationSector);
            var lunaMove = animations.PreferredMove(65, 1) ?? throw new InvalidOperationException("LUNA MOVE family missing.");
            Equal(true, lunaMove.ExactSector);
            Equal("LUNAMOVE10", lunaMove.Candidate.AnimationName);
            var turretDeploy = animations.PreferredDeploy(1, 0) ?? throw new InvalidOperationException("TURR DEPLOY family missing.");
            Equal("TURRDEPLOY0", turretDeploy.Candidate.AnimationName);
            var xenoDeploy = animations.PreferredDeploy(9, 0) ?? throw new InvalidOperationException("XENO DEPLOY family missing.");
            Equal("XENODEPLOY0", xenoDeploy.Candidate.AnimationName);
            var exploiterRetract = animations.PreferredRetract(6, 0) ?? throw new InvalidOperationException("EXPL RETRACT family missing.");
            Equal(true, exploiterRetract.Candidate.AnimationName.StartsWith("EXPLRETRACT", StringComparison.OrdinalIgnoreCase));
            var slugRetract = animations.PreferredRetract(14, 0) ?? throw new InvalidOperationException("SLUG RETRACT family missing.");
            Equal(true, slugRetract.Candidate.AnimationName.StartsWith("SLUGRETRACT", StringComparison.OrdinalIgnoreCase));
        }, CheckTags.Data);
        Check("translucent FIN layers blend through the tileset's .rmp tables", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var tables = NativeBlendTables.Load(install.DataFile("jungle.rmp"), GifPalette.Load(install.DataFile("jungle.gif")));
            // Index 0 is transparent: it leaves the screen as it is.
            Equal(NativeBlend.Identity, tables.For(5, 0));
            // Type 5 reads the second table: row 32 leaves the screen, 76-79 make it white,
            // and 48-75 glow red to yellow, brighter further up the ramp.
            var neutral = tables.For(5, 32);
            Equal(true, neutral.Multiplier >= 250 && neutral.Red + neutral.Green + neutral.Blue <= 3);
            Equal(new NativeBlend(255, 255, 255, 0), tables.For(5, 76));
            var (low, high) = (tables.For(5, 52), tables.For(5, 68));
            Equal(true, high.Red > high.Green && high.Green > high.Blue && high.Red > low.Red && high.Multiplier < low.Multiplier);
            // Type 4 reads the first (the colour remap): sprite 64 is brightness 8 of
            // colour 0, about half the ground, untinted.
            var shade = tables.For(4, 64);
            Equal(true, shade.Multiplier is >= 110 and <= 135 && Math.Max(shade.Red, Math.Max(shade.Green, shade.Blue)) < 12);
            Equal(false, NativeBlendTables.IsTranslucent(3));

            // The explosions use them: NUKE and GASY draw their fire with draw type 5.
            foreach (var (file, name) in (ReadOnlySpan<(string, string)>)[("nuke.fin", "NUKE"), ("gasy.fin", "GASY")])
            {
                var fin = AnimationDefinition.Load(install.DataFile("animate", file));
                var range = fin.Animations.First(animation => animation.Name == name);
                Equal(true, Enumerable.Range(range.FirstFrame, range.LastFrame - range.FirstFrame + 1)
                    .Any(frame => fin.LogicalFrames[frame].Layers.Any(layer => layer.DrawType == 5)));
            }
        }, CheckTags.Data);
    }
}
