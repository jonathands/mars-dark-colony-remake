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

/// <summary>Mission scripts and triggers, campaign events, transports, artifacts, and campaign outcomes.</summary>
internal static class MissionsChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Missions", name, tags, action);

        Check("trigger conditions parse structurally without assigning variable semantics", () =>
        {
            var timer = ScenarioTriggerConditionParser.Parse("(c>1200)");
            var timerComparison = (ScenarioConditionComparisonNode)timer;
            Equal(ScenarioConditionComparison.Greater, timerComparison.Operator);
            Equal("c", ((ScenarioConditionVariable)timerComparison.Left).Name);
            Equal(new ScenarioConditionNumber(1200), timerComparison.Right);

            var state = ScenarioTriggerConditionParser.Parse("(s(0,0,73)==1)");
            var stateComparison = (ScenarioConditionComparisonNode)state;
            Equal("s", ((ScenarioConditionVariable)stateComparison.Left).Name);
            Equal(3, ((ScenarioConditionVariable)stateComparison.Left).Arguments.Count);

            var combined = ScenarioTriggerConditionParser.Parse("((c>s(0,2,0))&&(S==0))");
            Equal(ScenarioConditionLogical.And, ((ScenarioConditionLogicalNode)combined).Operator);
            var bareValue = ScenarioTriggerConditionParser.Parse("(1)");
            Equal(new ScenarioConditionNumber(1), ((ScenarioConditionValueNode)bareValue).Value);
            Equal(false, ScenarioTriggerConditionParser.TryParse("(c&&==0)", out _));
        });

        Check("trigger expressions compile and evaluate with the executable's grammar", () =>
        {
            var context = new FakeTriggerContext();
            int Run(string text) => TriggerExpression.Evaluate(TriggerExpression.Compile(text), context);
            Equal(new byte[] { TriggerExpression.Clock, TriggerExpression.Literal, 0xb0, 0x04, TriggerExpression.Greater, TriggerExpression.End },
                TriggerExpression.Compile("(c>1200)"));
            // && and || share one precedence and associate to the right (0x43C638).
            Equal(0, Run("(0&&0||1)"));
            Equal(1, Run("(1||0&&0)"));
            // Addition binds tighter than multiplication (0x43C87C over 0x43C988).
            Equal(8, Run("(2*3+1)"));
            Equal(1, Run("((c+45)>40)"));
            Equal(10, Run("s(2,3)"));
            Equal(15, Run("s(1,2,5)"));
            Equal(1, Run("(b(1,0)==0)"));
            Equal(1, Run("(S==2)"));
            // human09 "(b(1,3)&&==0)": the stray && takes its left operand from the
            // enclosing chain, so the chain's last && reads the -1 sentinel below the
            // stack (0x43CF4C) and keeps the rest.
            Equal(0, Run("((1==1)&&(b(1,3)&&==0))"));
            Equal(1, Run("((1==1)&&(b(1,2)&&==0))"));
        });

        Check("commander packet modes deliver reinforcements and abduct hostile units", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var weapons = WeaponCatalog.Load(install.DataFile("gamestat", "weapstat.txt"));
            var effects = AreaEffectCatalog.Load(install.DataFile("gamestat", "boomstat.txt"));
            var pathBytes = new byte[PathRegionMap.RouteTableSize + 12 * 8];
            pathBytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var path = PathRegionMap.Parse(pathBytes, 12, 8);

            const string humanScenario = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n1\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 2 71 0 -1 0\n";
            var human = ScenarioSimulation.Create(ScenarioDefinition.Parse(humanScenario), catalog, path,
                weaponCatalog: weapons, areaEffects: effects);
            Equal("DROP SHIP", UnitCommandProfiles.Describe(catalog[71], true).SecondaryCommand!.Label);
            human.Step([new ScheduledWorldCommand(1, 0, new GroundSpecialAttackIntent(1, new CellCoordinate(5, 2)))]);
            for (var tick = 0; tick < 100 && !human.LastBattlefieldTransports.Any(item => item.Kind == BattlefieldTransportEventKind.Started); tick++) human.Step([]);
            var humanStarted = human.LastBattlefieldTransports.Single(item => item.Kind == BattlefieldTransportEventKind.Started);
            Equal(92, humanStarted.TransportEntityId);
            Equal(1, human.Actors.Count);
            var dropship = human.BattlefieldTransports.Single();
            Equal(BattlefieldTransportPhase.Descending, dropship.Phase);
            Equal(ScenarioSimulation.NativeDropShipBaseHeightRaw + 3 * ScenarioSimulation.NativeTransportFlightTicks * ScenarioSimulation.NativeTransportFlightTicks, dropship.HeightRaw);
            Equal(FixedPointPosition.One, Math.Abs(dropship.Position.XRaw - FixedPointPosition.AtCellCenter(dropship.Target).XRaw));
            Equal(FixedPointPosition.One, Math.Abs(dropship.Position.ZRaw - FixedPointPosition.AtCellCenter(dropship.Target).ZRaw));
            var humanTransportEvents = new List<BattlefieldTransportEvent>();
            for (var tick = 0; tick < 160 && human.BattlefieldTransports.Count != 0; tick++)
            {
                human.Step([]);
                humanTransportEvents.AddRange(human.LastBattlefieldTransports);
            }
            var deliveries = humanTransportEvents.Where(item => item.Kind == BattlefieldTransportEventKind.PayloadResolved).ToArray();
            Equal(new[] { 0, 0, 2 }, deliveries.SelectMany(item => item.ReinforcementInstanceIds).Select(id => human.Actor(id)!.Seed.EntityId).ToArray());
            Equal(0, deliveries.SelectMany(item => item.AbductedInstanceIds).Count());
            Equal(true, humanTransportEvents.Any(item => item.Kind == BattlefieldTransportEventKind.Departed));
            Equal(0, human.BattlefieldTransports.Count);

            const string grayScenario = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n1\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 2 74 1 -1 0\n8 2 0 0 -1 0\n";
            var gray = ScenarioSimulation.Create(ScenarioDefinition.Parse(grayScenario), catalog, path,
                weaponCatalog: weapons, areaEffects: effects);
            Equal("SAUCER", UnitCommandProfiles.Describe(catalog[74], true).SecondaryCommand!.Label);
            gray.Step([new ScheduledWorldCommand(1, 1, new GroundSpecialAttackIntent(1, new CellCoordinate(5, 2)))]);
            for (var tick = 0; tick < 100 && !gray.LastBattlefieldTransports.Any(item => item.Kind == BattlefieldTransportEventKind.Started); tick++) gray.Step([]);
            var grayStarted = gray.LastBattlefieldTransports.Single(item => item.Kind == BattlefieldTransportEventKind.Started);
            Equal(93, grayStarted.TransportEntityId);
            var saucer = gray.BattlefieldTransports.Single();
            Equal(BattlefieldTransportPhase.Descending, saucer.Phase);
            Equal((byte)216, catalog[93].InitialFacing);
            Equal((byte)216, saucer.Facing.Current);
            Equal(ScenarioSimulation.NativeSaucerBaseHeightRaw + 3 * ScenarioSimulation.NativeTransportFlightTicks * ScenarioSimulation.NativeTransportFlightTicks, saucer.HeightRaw);
            Equal(false, gray.Actor(2)!.IsDestroyed);
            var grayTransportEvents = new List<BattlefieldTransportEvent>();
            for (var tick = 0; tick < 80 && !saucer.IsPursuing; tick++)
            {
                gray.Step([]);
                grayTransportEvents.AddRange(gray.LastBattlefieldTransports);
            }
            Equal(true, saucer.IsPursuing);
            Equal(true, saucer.IsTurning);
            Equal((byte)0, saucer.Facing.Target);
            Equal(false, gray.Actor(2)!.IsDestroyed);
            var pursuitStart = saucer.Position;
            var pursuitExecutions = saucer.HorizontalExecutionsRemaining;
            Equal(true, pursuitExecutions > 0);
            for (var turn = 0; turn < 4; turn++)
            {
                gray.Step([]);
                grayTransportEvents.AddRange(gray.LastBattlefieldTransports);
                Equal(pursuitStart, saucer.Position);
                Equal(pursuitExecutions, saucer.HorizontalExecutionsRemaining);
            }
            Equal(false, saucer.IsTurning);
            Equal((byte)0, saucer.Facing.Current);
            gray.Step([]);
            grayTransportEvents.AddRange(gray.LastBattlefieldTransports);
            Equal(true, saucer.Position != pursuitStart);
            Equal(pursuitExecutions - 1, saucer.HorizontalExecutionsRemaining);
            for (var tick = 0; tick < 160 && gray.BattlefieldTransports.Count != 0; tick++)
            {
                gray.Step([]);
                grayTransportEvents.AddRange(gray.LastBattlefieldTransports);
            }
            Equal(new[] { 2 }, grayTransportEvents.Where(item => item.Kind == BattlefieldTransportEventKind.PayloadResolved).SelectMany(item => item.AbductedInstanceIds).ToArray());
            Equal(true, gray.Actor(2)!.IsDestroyed);
            Equal(0, gray.LastDestroyedActors.Count);
            Equal(true, grayTransportEvents.Any(item => item.Kind == BattlefieldTransportEventKind.Departed));
        }, CheckTags.Data);

        Check("installed TRO mission scripts preserve trigger structure", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var files = Directory.GetFiles(install.DataFile("scenario"), "*.tro", SearchOption.AllDirectories);
            var triggerCount = 0;
            var opaqueConditions = new List<string>();
            foreach (var file in files)
            {
                var triggers = ScenarioTriggers.Load(file);
                triggerCount += triggers.Count;
                if (triggers.Any(trigger => trigger.ParsedCondition is null))
                    throw new InvalidDataException($"{file} contains a trigger without a parsed condition.");
                opaqueConditions.AddRange(triggers
                    .Where(trigger => trigger.ParsedCondition is ScenarioConditionOpaqueNode)
                    .Select(trigger => trigger.Condition));
            }
            if (files.Length == 0 || triggerCount == 0)
                throw new InvalidDataException("Installed scenario corpus has no readable mission triggers.");

            var human01 = ScenarioTriggers.Load(install.DataFile("scenario", "human", "human01.tro"));
            Equal("norm", human01[0].Mode);
            Equal("(c>1200)", human01[0].Condition);
            Equal(ScenarioConditionComparison.Greater, ((ScenarioConditionComparisonNode)human01[0].ParsedCondition).Operator);
            Equal("bail", human01[0].Commands.Single().Verb);
            Equal("newtype", human01.Single(trigger => trigger.Id == 1).Commands[0].Verb);
            var verbs = files.SelectMany(ScenarioTriggers.Load)
                .SelectMany(trigger => trigger.Commands)
                .Select(command => command.Verb.ToLowerInvariant())
                .Distinct()
                .OrderBy(verb => verb)
                .ToArray();
            Equal("abduct,ai,aimsg,ally,artifact,bail,dfiddle,exomoney,msg,newrate,newrate2,newtype,nopickup,noundeploy,reinforce,reinforce2,setarray,setlifes,setmoney,vision,waypoint",
                string.Join(',', verbs));
            Equal("((b(1,0)==0)&&(b(1,1)==0)&&(b(1,2)==0)&&(b(1,3)&&==0)&&(b(1,4)==0)&&(b(2,0)==0)&&(b(2,1)==0)&&(b(2,2)==0)&&(b(2,3)==0)&&(b(2,4)==0)&&(s(4,3)==12))",
                string.Join(',', opaqueConditions));
            Console.WriteLine($"  trigger corpus: {files.Length} scripts / {triggerCount} triggers");
        }, CheckTags.Data);

        Check("campaign mission text preserves briefings messages and outcomes", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var mission = ScenarioMissionText.LoadForScenario(install.DataFile("scenario", "human", "human01.scn"));
            Equal(true, mission.Briefing.Contains("MARS, COMMANDER", StringComparison.Ordinal));
            Equal("MISSION COMPLETE...PREPARE FOR PICKUP, COMMANDER", mission.Messages[2]);
            Equal(true, mission.Outcomes.ContainsKey("001"));
        }, CheckTags.Data);

        Check("every installed mission script compiles with its trip map", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var scripts = 0;
            var triggers = 0;
            foreach (var file in Directory.GetFiles(install.DataFile("scenario"), "*.scn", SearchOption.AllDirectories))
            {
                if (MissionScript.LoadForScenario(file) is not { } script) continue;
                scripts++;
                triggers += script.Triggers.Count;
                if (script.TripMap is { } trips && File.Exists(Path.ChangeExtension(file, ".map")))
                {
                    var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
                    Equal((map.Width, map.Height), (trips.Width, trips.Height));
                }
            }
            if (scripts < 100) throw new InvalidDataException($"Only {scripts} mission scripts compiled.");
            Console.WriteLine($"  mission scripts: {scripts} scripts / {triggers} triggers compiled");
        }, CheckTags.Data);

        Check("human01 opens with the beacon landing: message 1, reinforcements, no passive income", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var (simulation, _) = DeterminismHarness.Load(install, rules, "human/human01");
            var script = MissionScript.LoadForScenario(install.DataFile("scenario", "human", "human01") + ".scn")!;
            // Trigger 8 "(c>0)": its actions run in reverse source order.
            Equal(MissionActionType.Message, script.Triggers.Single(trigger => trigger.Slot == 8).Actions[0].Type);
            Equal(true, script.TripMap!.TriggerAt(new CellCoordinate(54, 19)) == 1);
            var before = simulation.Actors.Count(actor => actor.Seed.Team == 0);
            var messages = new List<MissionMessageEvent>();
            for (var tick = 0; tick < 17; tick++)
            {
                simulation.Step([]);
                messages.AddRange(simulation.LastMissionMessages);
            }
            // c = ticks >> 4 first exceeds 0 at the norm pass of tick 16.
            Equal(1, messages.Single().Index);
            Equal(0, simulation.MissionLives[8]);
            // reinforce (0x43E1A4 -> 0x418F4C): a drop ship (team 0 is race 0)
            // descends, then unloads one unit per update.
            var transport = simulation.BattlefieldTransports.Single();
            Equal(92, transport.TransportEntityId);
            Equal(0, transport.TeamId);
            Equal(before, simulation.Actors.Count(actor => actor.Seed.Team == 0));
            var arrivals = new List<ulong>();
            for (var tick = 0; tick < 400 && simulation.Actors.Count(actor => actor.Seed.Team == 0) < before + 5; tick++)
            {
                var count = simulation.Actors.Count(actor => actor.Seed.Team == 0);
                simulation.Step([]);
                if (simulation.Actors.Count(actor => actor.Seed.Team == 0) > count) arrivals.Add(simulation.TickCount);
            }
            Equal(before + 5, simulation.Actors.Count(actor => actor.Seed.Team == 0));
            Equal(5, arrivals.Count);
            Equal(4UL, arrivals[^1] - arrivals[0]);
            var p7 = simulation.ResourceForTeam(0);
            for (var tick = 0; tick < 48; tick++) simulation.Step([]);
            Equal(p7, simulation.ResourceForTeam(0));
        }, CheckTags.Data);

        Check("alien01 registers commanders and abducts team 1's commander after c > 10", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var (simulation, _) = DeterminismHarness.Load(install, rules, "alien/alien01");
            // Trigger 1 (c > 0) runs "reinforce2 1 66 67 69 1": the constructor puts
            // the commander (0x41B223) into team 1's first slot.
            for (var tick = 0; tick < 17; tick++) simulation.Step([]);
            var commanderId = simulation.CommanderInSlot(1, 0) ?? throw new InvalidOperationException("No team 1 commander slot.");
            var commander = simulation.Actor(commanderId)!;
            Equal(69, commander.Seed.EntityId);
            // 0x419801: the 230 charge applies only while the player's live units
            // reach the troop cap; team 1 is far below it.
            Equal(true, simulation.PlayerStatistic(1, 6) < simulation.TroopCap);
            Equal(SimulatedActor.NativeInitialAbilityCharge, commander.AbilityCharge);
            // Trigger 10 (c > 10) runs "abduct 1 1": a transport of team 1's race
            // comes for that commander.
            for (var tick = 0; tick < 200 && simulation.BattlefieldTransports.All(transport => !transport.Abducts); tick++)
                simulation.Step([]);
            var transport = simulation.BattlefieldTransports.Single(candidate => candidate.Abducts);
            Equal(1, transport.TeamId);
            for (var tick = 0; tick < 400 && !commander.IsDestroyed; tick++) simulation.Step([]);
            Equal(true, commander.IsDestroyed);
            for (var tick = 0; tick < 8; tick++) simulation.Step([]);
            Equal(false, simulation.CommanderInSlot(1, 0).HasValue);
        }, CheckTags.Data);

        Check("artifact sites bury their placements and an idle harvester digs one out every 450 updates", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            // The loader (0x41C5C0) gives each POOP to team 8 with a container at
            // its cell; the placements on that cell join the container instead.
            var (alien06, _) = DeterminismHarness.Load(install, rules, "alien/alien06");
            Equal(4, alien06.ArtifactContainers.Count);
            Equal(23, alien06.ArtifactContainers.Sum(container => container.Items.Count));
            var sites = alien06.Actors.Where(actor => actor.Seed.EntityId == ScenarioSimulation.ArtifactSiteEntity).ToArray();
            Equal(4, sites.Length);
            Equal(true, sites.All(site => site.Seed.Team == 8));
            Equal(false, alien06.Actors.Any(actor => actor.Seed.EntityId != ScenarioSimulation.ArtifactSiteEntity &&
                                                     alien06.ArtifactContainers.Any(container => container.Cell == actor.Seed.SpawnCell)));

            // A site at (6,1) holding LUNA then LENS, and an EXPL at (2,1).
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n" +
                "6 1 37 0 -1 0\n6 1 65 0 -1 0\n6 1 63 0 -1 0\n2 1 6 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16 * 4];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            // Actions run in reverse order: reinforce2 buries a HYYK, then
            // artifact buries entity 0x3F + r % 5.
            var script = MissionScript.Compile(ScenarioTriggers.Parse(
                "1 norm 1 (c>0)\nartifact 6 1\nreinforce2 0 6 1 66 1 0 0 0 0 0 0 0 0\nend\n"));
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), PathRegionMap.Parse(bytes, 16, 4), rules, script);
            var site = simulation.Actors.Single(actor => actor.Seed.EntityId == ScenarioSimulation.ArtifactSiteEntity);
            var harvester = simulation.Actors.Single(actor => actor.Seed.EntityId == 6);
            var container = simulation.ArtifactContainers.Single();
            Equal(new[] { 65, 63 }, container.Items.ToArray());
            Equal(-1, site.ArtifactExcavationTicks);
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new MoveIntent(harvester.Seed.InstanceId, new CellCoordinate(6, 1)))]);
            for (var tick = 0; tick < 17; tick++) simulation.Step([]);
            Equal(4, container.Items.Count);
            Equal(66, container.Items[2]);
            Equal(true, container.Items[3] is >= 0x3f and <= 0x43);
            Equal(false, simulation.Actors.Any(actor => actor.Seed.EntityId == 66));
            for (var tick = 0; tick < 400 && site.ArtifactExcavatorInstanceId is null; tick++) simulation.Step([]);
            Equal(harvester.Seed.InstanceId, site.ArtifactExcavatorInstanceId!.Value);
            Equal(ScenarioSimulation.NativeArtifactExcavationTicks - 1, site.ArtifactExcavationTicks);

            var expected = container.Items.ToArray();
            var previous = simulation.TickCount;
            var gap = (ulong)ScenarioSimulation.NativeArtifactExcavationTicks - 1;
            foreach (var entityId in expected)
            {
                ArtifactRecoveryEvent? recovery = null;
                while (recovery is null && simulation.TickCount < previous + 500)
                {
                    simulation.Step([]);
                    recovery = simulation.LastArtifactRecoveries.SingleOrDefault();
                }
                Equal(previous + gap, simulation.TickCount);
                var item = simulation.Actor(recovery!.ItemInstanceId!.Value)!;
                Equal(entityId, item.Seed.EntityId);
                Equal(0, item.Seed.Team);
                previous = simulation.TickCount;
                gap = ScenarioSimulation.NativeArtifactExcavationTicks;
            }
            // The next countdown finds the container empty and removes the site.
            for (var tick = 0; tick < ScenarioSimulation.NativeArtifactExcavationTicks; tick++) simulation.Step([]);
            Equal(true, simulation.LastArtifactRecoveries.Single().SiteDepleted);
            Equal(true, site.IsDestroyed);
            Equal(false, harvester.IsDestroyed);
        }, CheckTags.Data);

        Check("a commander's body waits for its pickup transport unless nopickup or atlantis.bts", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var bytes = new byte[PathRegionMap.RouteTableSize + 16 * 4];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            ScenarioSimulation Start(string tileset, MissionScript? script)
            {
                var source = tileset + "\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n" +
                    "5 1 69 0 -1 0\n2 1 0 0 -1 0\n";
                var started = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), PathRegionMap.Parse(bytes, 16, 4), rules, script);
                // The nopickup trigger runs in the norm pass of update 8.
                for (var tick = 0; tick < 8; tick++) started.Step([]);
                return started;
            }

            var simulation = Start("desert.bts", null);
            var commander = simulation.Actors.Single(actor => actor.Seed.EntityId == 69);
            simulation.Destroy(commander, new List<DestroyedActorEvent>());
            // 0x416308: a transport of the commander's team comes for the body.
            var transport = simulation.BattlefieldTransports.Single();
            Equal(commander.Seed.InstanceId, transport.CorpseInstanceId!.Value);
            Equal(0, transport.TeamId);
            Equal(92, transport.TransportEntityId);
            simulation.Step([]);
            Equal(1, commander.DeathTicks!.Value);
            for (var tick = 0; tick < 200 && commander.IsDying; tick++)
            {
                Equal(1, commander.DeathTicks!.Value);
                Equal(2, simulation.PlayerStatistic(0, 6));
                simulation.Step([]);
            }
            // 0x418CAA sets the counter to 150 and takes off; the transport runs
            // before the body in the update order, so the body leaves at once.
            Equal(false, commander.IsDying);
            Equal(BattlefieldTransportPhase.Ascending, transport.Phase);
            Equal(commander.Seed.InstanceId, simulation.LastBattlefieldTransports
                .Single(update => update.Kind == BattlefieldTransportEventKind.PayloadResolved).AbductedInstanceIds.Single());
            simulation.Step([]);
            Equal(1, simulation.PlayerStatistic(0, 6));

            var noPickup = MissionScript.Compile(ScenarioTriggers.Parse("1 norm 1 (1)\nnopickup 0\nend\n"));
            foreach (var (tileset, script) in new[] { ("atlantis.bts", (MissionScript?)null), ("desert.bts", noPickup) })
            {
                var stay = Start(tileset, script);
                var body = stay.Actors.Single(actor => actor.Seed.EntityId == 69);
                stay.Destroy(body, new List<DestroyedActorEvent>());
                Equal(0, stay.BattlefieldTransports.Count);
                for (var tick = 0; tick < 300; tick++) stay.Step([]);
                // The counter stays at 1, so the body keeps counting as a unit.
                Equal(1, body.DeathTicks!.Value);
                Equal(2, stay.PlayerStatistic(0, 6));
            }
        }, CheckTags.Data);

        Check("waypoint makes the actor patrol its points in a loop until it gets an order", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            const string source = "desert.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n" +
                "2 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16 * 4];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var script = MissionScript.Compile(ScenarioTriggers.Parse("1 norm 1 (1)\nwaypoint 2 1 2 6 1 2 1\nend\n"));
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), PathRegionMap.Parse(bytes, 16, 4), rules, script);
            var unit = simulation.Actors.Single();
            // The norm pass of update 8 runs the trigger (0x43E08D -> 0x43D764).
            for (var tick = 0; tick < 8; tick++) simulation.Step([]);
            Equal(2, unit.PatrolPoints!.Count);
            Equal(1, unit.PatrolIndex);
            var visits = new List<CellCoordinate>();
            for (var tick = 0; tick < 600 && visits.Count < 4; tick++)
            {
                var before = unit.PatrolIndex;
                simulation.Step([]);
                if (unit.PatrolIndex != before) visits.Add(unit.Movement.OccupiedCell);
            }
            // Command 9 (0x416198) wraps to the first point after the last.
            Equal(new[] { new CellCoordinate(6, 1), new CellCoordinate(2, 1), new CellCoordinate(6, 1), new CellCoordinate(2, 1) },
                visits.ToArray());
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new StopIntent(unit.Seed.InstanceId))]);
            Equal(false, unit.PatrolPoints is not null);
        }, CheckTags.Data);

        Check("newtype changes the first ground-class actor on the cell and keeps its health", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            // A Scout VTOL (flier, entity 5) listed first, then a marine (entity 0), both on (3,1).
            const string source = "desert.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n" +
                "3 1 5 0 -1 0\n3 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16 * 4];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var script = MissionScript.Compile(ScenarioTriggers.Parse("1 norm 1 (1)\nnewtype 3 1 84\nend\n"));
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), PathRegionMap.Parse(bytes, 16, 4), rules, script);
            var flier = simulation.Actors.Single(actor => actor.Seed.EntityId == 5);
            var marine = simulation.Actors.Single(actor => actor.Seed.EntityId == 0);
            var health = marine.Health;
            for (var tick = 0; tick < 8; tick++) simulation.Step([]);
            // 0x43E117 skips the flier (movement class 1).
            Equal(5, simulation.EffectiveDefinition(flier).Id);
            Equal(84, simulation.EffectiveDefinition(marine).Id);
            Equal(health, marine.Health);
            Equal(rules.Entities[84].Health, marine.MaximumHealth);
        }, CheckTags.Data);

        Check("SCN values set health, and flagged placements wait to be rescued or picked up", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            // 0x41B339: a value other than -1 is the initial health; 0x41B321: the
            // flag column is byte +0xCB.
            var (alien06, _) = DeterminismHarness.Load(install, rules, "alien/alien06");
            var scenario = ScenarioDefinition.Load(install.DataFile("scenario", "alien", "alien06") + ".scn");
            var valued = scenario.OrdinaryPlacements.Count(placement => placement.Value > -1 && placement.EntityId != 37 && placement.Team < 8);
            Equal(true, valued > 0);
            Equal(true, alien06.Actors.Where(actor => actor.Seed.ScenarioValue > -1 && actor.Seed.InstanceId <= scenario.OrdinaryPlacements.Count)
                .All(actor => actor.Health == actor.Seed.ScenarioValue));
            Equal(3, alien06.Actors.Count(actor => actor.ContactRole == ContactRole.Rescue));

            // A captive marine of team 2 (flag 1) at (5,1) and a fuel crate of
            // team 3 worth 20 (flag 2) at (9,1); an Exploiter of team 0 at (1,1).
            const string source = "desert.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n" +
                "5 1 0 2 -1 1\n9 1 85 3 20 2\n1 1 6 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16 * 4];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), PathRegionMap.Parse(bytes, 16, 4), rules);
            var captive = simulation.Actors.Single(actor => actor.Seed.EntityId == 0);
            var crate = simulation.Actors.Single(actor => actor.Seed.EntityId == 85);
            var harvester = simulation.Actors.Single(actor => actor.Seed.EntityId == 6);
            Equal(20, crate.Health);
            Equal(ContactRole.Pickup, crate.ContactRole);
            for (var tick = 0; tick < 16; tick++) simulation.Step([]);
            Equal(2, captive.Seed.Team);
            // 0x4140DC: within two cells of a player-0 unit the captive joins player 0.
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new MoveIntent(harvester.Seed.InstanceId, new CellCoordinate(3, 1)))]);
            for (var tick = 0; tick < 200 && captive.Seed.Team != 0; tick++) simulation.Step([]);
            Equal(0, captive.Seed.Team);
            Equal(ContactRole.None, captive.ContactRole);
            Equal(1, simulation.ScriptWords[0]);
            Equal(ContactRole.Rescue, simulation.LastContactResolutions.Single().Role);
            // The crate pays its health to the first player whose unit comes close, then dies.
            var p7 = simulation.ResourceForTeam(0);
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new MoveIntent(harvester.Seed.InstanceId, new CellCoordinate(7, 1)))]);
            for (var tick = 0; tick < 200 && !crate.IsDestroyed; tick++) simulation.Step([]);
            Equal(true, crate.IsDying);
            Equal(p7 + 20, simulation.ResourceForTeam(0));
            var pickup = simulation.LastContactResolutions.Single();
            Equal((ContactRole.Pickup, 0, 20), (pickup.Role, pickup.Team, pickup.Amount));
        }, CheckTags.Data);

        Check("human01 reaches its own victory: trip 7 arms trigger 4, three player-4 losses bail 0 1", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            // Strike force: 12 marines next to player 4's Grays at (72-75,53), and
            // one runner beside the beacon's trip area (25-28,57-60).
            var simulation = LoadMissionWithExtraTriggers(install, rules, "human/human01",
                "120 norm 1 (c>0)\nreinforce2 0 70 49 0 12 0 0 0 0 0 0 0 0\nreinforce2 0 30 61 0 1 0 0 0 0 0 0 0 0\nend\n");
            for (var tick = 0; tick < 17; tick++) simulation.Step([]);
            var runner = simulation.Actors.Last(actor => actor.Seed.Team == 0 && actor.Seed.EntityId == 0 &&
                                                         Math.Abs(actor.Movement.OccupiedCell.Z - 61) <= 2);
            Equal(0, simulation.MissionLives[4]);
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new MoveIntent(runner.Seed.InstanceId, new CellCoordinate(28, 59)))]);
            for (var tick = 0; tick < 300 && simulation.MissionLives[4] == 0; tick++) simulation.Step([]);
            // Trigger 7 (trip): setlifes 4 1.
            Equal(1, simulation.MissionLives[4]);
            Strike(simulation, 0, actor => actor.Seed.Team == 4, () => simulation.Outcome is not null, 6000);
            Equal(true, simulation.PlayerStatistic(4, 3) > 2);
            var outcome = simulation.Outcome ?? throw new InvalidOperationException("human01 did not end.");
            Equal((true, 1), (outcome.Victory, outcome.OutcomeText));
        }, CheckTags.Data);

        Check("alien01 reaches its own victory: eleven player-1 Salad shooters lost bail 0 1", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var scenario = ScenarioDefinition.Load(install.DataFile("scenario", "alien", "alien01") + ".scn");
            var shooters = scenario.OrdinaryPlacements.Where(placement => placement.Team == 1 && placement.EntityId == 82).ToArray();
            Equal(true, shooters.Length > 10);
            // Strike force: 4 Grays on the free cells around each of player 1's
            // entity 82.
            var extra = string.Concat(shooters.Select(placement =>
                $"reinforce2 0 {placement.X} {placement.Z} 8 4 0 0 0 0 0 0 0 0\n"));
            var simulation = LoadMissionWithExtraTriggers(install, rules, "alien/alien01", $"120 norm 1 (c>0)\n{extra}end\n");
            Strike(simulation, 0, actor => actor.Seed.Team == 1, () => simulation.Outcome is not null, 12000);
            Equal(true, simulation.TypeStatistic(1, 0, 82) > 10);
            var outcome = simulation.Outcome ?? throw new InvalidOperationException("alien01 did not end.");
            Equal((true, 1), (outcome.Victory, outcome.OutcomeText));
        }, CheckTags.Data);

        Check("losing the mining colony ends human01 in defeat with outcome text 4", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var (simulation, _) = DeterminismHarness.Load(install, rules, "human/human01");
            // Trigger 5: every team 1 city slot gone -> bail 1 4.
            foreach (var slot in new[] { 0, 1 })
            {
                var building = simulation.CityBuilding(1, slot)!;
                building.Health = 0;
                simulation.Destroy(building, new List<DestroyedActorEvent>());
            }
            for (var tick = 0; tick < 16 && simulation.Outcome is null; tick++) simulation.Step([]);
            var outcome = simulation.Outcome!;
            Equal(false, outcome.Victory);
            Equal(4, outcome.OutcomeText);
            Equal(1, simulation.PlayerStatistic(0, 0));
            Equal(4, simulation.PlayerStatistic(7, 0));
        }, CheckTags.Data);

        Check("every campaign and training mission reaches its script's outcome in the campaign smoke", () =>
        {
            // docs/CAMPAIGN_SMOKE.md: team 0 under the Krusty planner, hostiles of the
            // victory triggers swept every 64 updates, idle units sent to open trips.
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var lines = DeterminismCli.CampaignSmoke(install, rules, 40000, filter: null, sweep: true);
            Equal(44, lines.Count);
            var unfinished = lines.Where(line => line.Contains(" none ", StringComparison.Ordinal) || line.Contains("FAULT", StringComparison.Ordinal)).ToArray();
            if (unfinished.Length != 0) throw new InvalidOperationException(string.Join("; ", unfinished));
            var victories = lines.Count(line => line.Contains(" victory ", StringComparison.Ordinal));
            Console.WriteLine($"  campaign smoke: {lines.Count} missions ended, {victories} in victory");
        }, CheckTags.Data | CheckTags.Slow);
    }
}
