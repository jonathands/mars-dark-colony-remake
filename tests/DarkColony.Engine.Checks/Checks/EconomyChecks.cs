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

/// <summary>P7 income and vents, purchases, city buildings, troop production, research, and the catalog sweep.</summary>
internal static class EconomyChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Economy", name, tags, action);

        Check("default P7 harvester cadence follows the native low-four-bit gate", () =>
        {
            Equal(PetraFlowRules.NativeHarvesterPulseTicks, PetraFlowRules.ProvisionalDefault.TicksPerPulse);
            Equal(16, PetraFlowRules.NativeHarvesterPulseTicks);
            Equal(0, PetraFlowRules.ProvisionalDefault.PassiveP7PerPulse);
        });

        Check("P7 source credit keeps the native strict reservoir boundary", () =>
        {
            var rules = PetraFlowRules.ProvisionalDefault;
            Equal(true, rules.CanCreditReservoir(5, 4));
            Equal(false, rules.CanCreditReservoir(4, 4));
            Equal(false, rules.CanCreditReservoir(3, 4));
        });

        Check("P7 source credit applies the recovered signed 8.8 owner multiplier", () =>
        {
            Equal(4, new PetraFlowRules(16, 0, 4).EffectiveAttachedP7);
            Equal(4, new PetraFlowRules(16, 0, 4, 0x180).EffectiveAttachedP7);
            Equal(6, new PetraFlowRules(16, 0, 4, 0x180, true).EffectiveAttachedP7);
            Equal(2, new PetraFlowRules(16, 0, 4, 0x80, true).EffectiveAttachedP7);
            Equal(-2, new PetraFlowRules(16, 0, 3, -0x80, true).EffectiveAttachedP7);
            Equal(false, new PetraFlowRules(16, 0, 3, -0x80, true).CanCreditReservoir(100, -2));
        });

        Check("scenario simulation executes P7 purchase intents deterministically", () =>
        {
            var catalog = EntityCatalog.Parse("1\nACTOR 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var dependencies = DependencyCatalog.Parse("2\n1 100 80 0 1 0 0 -1\n9 350 89 1 0 1 -1\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n600\n%Money\n%City\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 1];
            bytes[^1] = 1;
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 1, 1), dependencyCatalog: dependencies);
            simulation.Step([new ScheduledWorldCommand(1, 0, new PurchaseIntent(0, 9))]);
            Equal(PurchaseEligibility.MissingPrerequisite, simulation.LastPurchaseReservations.Single().Eligibility);
            Equal(600, simulation.ResourceForTeam(0));
            simulation.Step([new ScheduledWorldCommand(2, 0, new PurchaseIntent(0, 1))]);
            Equal(PurchaseEligibility.Available, simulation.LastPurchaseReservations.Single().Eligibility);
            Equal(true, simulation.MarkDependencyBuildingCompleted(0, 1));
            simulation.Step([new ScheduledWorldCommand(3, 0, new PurchaseIntent(0, 9))]);
            Equal(PurchaseEligibility.Available, simulation.LastPurchaseReservations.Single().Eligibility);
            Equal(150, simulation.ResourceForTeam(0));
            Equal(new[] { 9 }, simulation.EconomyForTeam(0)!.ReservedItems.ToArray());
        });

        Check("Exploiter vent deployment accelerates P7 and sight blends day and night", () =>
        {
            var catalog = EntityCatalog.Parse("2\nEXPL 0 255 25 2 9 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 8 5 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n1 1 40 0 100\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 4),
                petraFlowRules: new PetraFlowRules(100, 1, 4), dayNight: new DayNightCycle(2));
            var exploiter = simulation.Actors.Single();
            int Blend() => (simulation.DayNight.LightingLevel * 9 + (256 - simulation.DayNight.LightingLevel) * 2) >> 8;
            Equal(Blend(), simulation.ObservationRange(exploiter));
            simulation.Step([new ScheduledWorldCommand(1, 0, new HarvestVentIntent(exploiter.Seed.InstanceId, 0))]);
            Equal(HarvesterDeploymentOutcome.Preparing, simulation.LastHarvesterDeployments.Single().Outcome);
            Equal(ScenarioSimulation.NativeHarvesterAttachTicks, simulation.PetraVents[0].AttachTicksRemaining);
            Equal(0, simulation.ResourceForTeam(0));
            simulation.Step([]);
            Equal(DayNightPhase.Night, simulation.DayNight.Phase);
            Equal(Blend(), simulation.ObservationRange(exploiter));
            for (var tick = 1; tick < ScenarioSimulation.NativeHarvesterAttachTicks; tick++) simulation.Step([]);
            Equal(true, simulation.PetraVents[0].HarvesterInstanceId == exploiter.Seed.InstanceId);
            for (var tick = ScenarioSimulation.NativeHarvesterAttachTicks + 1;
                 tick < 100; tick++) simulation.Step([]);
            Equal(5, simulation.ResourceForTeam(0));
            Equal(new[] { 1, 4 }, simulation.LastP7Income.Select(income => income.Amount).ToArray());
            Equal(96, simulation.PetraVents[0].RemainingReservoir);
        });

        Check("P7 source stops before consuming its final exact-rate remainder", () =>
        {
            var catalog = EntityCatalog.Parse("2\nEXPL 0 255 25 2 2 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 2 2 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n1 1 40 0 4\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 4),
                petraFlowRules: new PetraFlowRules(1, 0, 4));
            var exploiter = simulation.Actors.Single();
            simulation.Step([new ScheduledWorldCommand(1, 0, new HarvestVentIntent(exploiter.Seed.InstanceId, 0))]);
            for (var tick = 0; tick < ScenarioSimulation.NativeHarvesterAttachTicks; tick++) simulation.Step([]);
            Equal(exploiter.Seed.InstanceId, simulation.PetraVents[0].HarvesterInstanceId!.Value);
            simulation.Step([]);
            Equal(4, simulation.PetraVents[0].RemainingReservoir);
            Equal(0, simulation.LastP7Income.Count);
        });

        Check("harvester deployment walks to a vent then attaches deterministically", () =>
        {
            var catalog = EntityCatalog.Parse("2\nEXPL 0 255 25 2 9 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 8 5 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 0 0 0 -1 0\n3 3 40 0 100\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 36];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 6, 6));
            var exploiter = simulation.Actors.Single();
            simulation.Step([new ScheduledWorldCommand(1, 0, new HarvestVentIntent(exploiter.Seed.InstanceId, 0))]);
            Equal(HarvesterDeploymentOutcome.EnRoute, simulation.LastHarvesterDeployments.Single().Outcome);
            for (var tick = 0; tick < 250 && simulation.PetraVents[0].HarvesterInstanceId is null; tick++) simulation.Step([]);
            Equal(exploiter.Seed.InstanceId, simulation.PetraVents[0].HarvesterInstanceId!.Value);
            Equal(new CellCoordinate(3, 3), exploiter.Movement.OccupiedCell);
            Equal(true, exploiter.MoveOrder is null && exploiter.Playback is null);
            Equal(1, exploiter.DeployedEntityId!.Value);
            Equal("EDPLY", simulation.EffectiveDefinition(exploiter).Code);
            Equal(0, exploiter.HarvestVentId!.Value);
            simulation.Step([new ScheduledWorldCommand(2, 0, new RetractHarvesterIntent(exploiter.Seed.InstanceId))]);
            Equal(HarvesterDeploymentOutcome.Retracted, simulation.LastHarvesterDeployments.Single().Outcome);
            Equal(false, simulation.PetraVents[0].HarvesterInstanceId.HasValue);
            Equal(false, exploiter.DeployedEntityId.HasValue);
            Equal("EXPL", simulation.EffectiveDefinition(exploiter).Code);
            Equal(new CellCoordinate(3, 3), exploiter.Movement.OccupiedCell);
            simulation.Step([new ScheduledWorldCommand(3, 0, new MoveIntent(exploiter.Seed.InstanceId, new CellCoordinate(2, 3)))]);
            Equal(true, simulation.LastMoveOutcomes.Single().StepCount > 0);
        });

        Check("ordinary movement onto a free vent automatically begins harvester deployment", () =>
        {
            var catalog = EntityCatalog.Parse("2\nEXPL 0 255 25 2 9 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 8 5 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 0 0 0 -1 0\n3 3 40 0 100\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 36];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 6, 6));
            var exploiter = simulation.Actors.Single();

            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(exploiter.Seed.InstanceId, new CellCoordinate(3, 3)))]);
            for (var tick = 0; tick < 250 && simulation.LastHarvesterDeployments.Count == 0; tick++) simulation.Step([]);

            Equal(HarvesterDeploymentOutcome.Preparing, simulation.LastHarvesterDeployments.Single().Outcome);
            Equal(0, exploiter.HarvestVentId!.Value);
            Equal(true, exploiter.MoveOrder is null && exploiter.Playback is null);
            Equal(ScenarioSimulation.NativeHarvesterAttachTicks, simulation.PetraVents[0].AttachTicksRemaining);
        });

        Check("noundeploy keeps deployed harvesters on their vents", () =>
        {
            var catalog = EntityCatalog.Parse("2\nEXPL 0 255 25 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n" +
                "1 1 40 20 5000\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16 * 4];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            HarvesterDeploymentOutcome Retract(string? trigger)
            {
                var script = trigger is null ? null : MissionScript.Compile(ScenarioTriggers.Parse(trigger));
                var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 16, 4),
                    missionScript: script);
                simulation.Step([new ScheduledWorldCommand(1, 0, new HarvestVentIntent(1, 0))]);
                for (var tick = 0; tick < ScenarioSimulation.NativeHarvesterAttachTicks + 8; tick++) simulation.Step([]);
                Equal("EDPLY", simulation.EffectiveDefinition(simulation.Actor(1)!).Code);
                simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new RetractHarvesterIntent(1))]);
                return simulation.LastHarvesterDeployments.Single().Outcome;
            }
            Equal(HarvesterDeploymentOutcome.Retracted, Retract(null));
            // world +0x948: 0x4137CF ignores the request and 0x4167EF refuses state 13.
            Equal(HarvesterDeploymentOutcome.UndeployLocked, Retract("1 norm 1 (1)\nnoundeploy\nend\n"));
        });

        Check("vents pay their own SCN rate per pulse, and a zero-rate vent pays nothing", () =>
        {
            // The SCN vent record "x z 40 rate reservoir": the loader stores the
            // team column as the vent's +0x32 rate (x the session option, 256 = x1).
            var catalog = EntityCatalog.Parse("2\nEXPL 0 255 25 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            int Earned(int rate)
            {
                var source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n2 1 0 0 -1 0\n" +
                    $"1 1 40 {rate} 100\n";
                var bytes = new byte[PathRegionMap.RouteTableSize + 16 * 4];
                bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
                var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 16, 4));
                simulation.Step([new ScheduledWorldCommand(1, 0, new HarvestVentIntent(1, 0))]);
                var earned = 0;
                for (var tick = 0; tick < 64; tick++)
                {
                    simulation.Step([]);
                    earned += simulation.LastP7Income.Where(income => income.VentId == 0).Sum(income => income.Amount);
                }
                Equal(100 - earned, simulation.PetraVents[0].RemainingReservoir);
                return earned;
            }
            var paid = Earned(20);
            Equal(0, paid % 20);
            Equal(true, paid >= 20);
            Equal(0, Earned(0));
        });

        Check("authored scenario buildings seed their dependency prerequisites", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var dependencies = DependencyCatalog.Load(install.DataFile("gamestat", "depend.txt"));
            var footprints = BuildingFootprintCatalog.Load(install.ExecutablePath);
            var candidate = dependencies.Items.Values
                .Where(item => item.IsBuilding && footprints.TryResolveBuildingEntity(item.BuildingFaction!.Value,
                    item.BuildingVariant!.Value, item.BuildingSlot!.Value, out _))
                .OrderBy(item => item.Id)
                .FirstOrDefault() ?? throw new InvalidDataException("No dependency building resolves through the executable build table.");
            Equal(true, footprints.TryResolveBuildingEntity(candidate.BuildingFaction!.Value, candidate.BuildingVariant!.Value,
                candidate.BuildingSlot!.Value, out var entityId));
            const string header = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n";
            var scenario = ScenarioDefinition.Parse($"{header}TEAM 0 1\n{candidate.BuildingFaction}\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 0 {entityId} 0 -1 0\n");
            var bytes = new byte[PathRegionMap.RouteTableSize + 1];
            bytes[^1] = 1;
            var path = PathRegionMap.Parse(bytes, 1, 1);
            var simulation = ScenarioSimulation.Create(scenario, catalog, path, footprints, dependencyCatalog: dependencies);
            Equal(true, simulation.EconomyForTeam(0)!.CompletedItems.Contains(candidate.Id));
        }, CheckTags.Data);

        Check("SCN cities seed slot buildings and gate passive P7 on the headquarters", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var file = install.DataFile("scenario", "mplayer", "j4play01") + ".scn";
            var team0 = ScenarioDefinition.Load(file).Teams.Single(team => team.TeamId == 0);
            Equal(new CellCoordinate(10, 55), team0.CityOrigin!.Value);
            Equal(new CellCoordinate(15, 57), team0.StartPoint!.Value);
            Equal(5, team0.CitySlots.Count);
            Equal(new ScenarioCitySlot(1, -1), team0.CitySlots[0]);
            Equal(new ScenarioCitySlot(0, -1), team0.CitySlots[1]);

            // Team 0 loses its headquarters; team 1 keeps one with explicit health.
            var cityLines = 0;
            var text = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), @"%City(\r?\n)1 -1", match =>
                ++cityLines switch
                {
                    1 => $"%City{match.Groups[1].Value}0 -1",
                    2 => $"%City{match.Groups[1].Value}1 2000",
                    _ => match.Value,
                });
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(text), path, rules, terrain: map);
            Equal(true, simulation.CityBuilding(0, 0) is null);
            var headquarters = simulation.CityBuilding(1, 0)!;
            Equal(2000, headquarters.Health);
            var origin = new CellCoordinate(63, 80);
            Equal(rules.Footprints!.CitySlotPosition(origin, 0), headquarters.Movement.VisualPosition);
            foreach (var cell in rules.Footprints.CitySlotCells(origin, 0))
            {
                Equal(true, simulation.GroundOccupancy.TryGetOwner(cell, out var owner));
                Equal(headquarters.Seed.InstanceId, owner);
            }
            var defaultHeadquarters = simulation.CityBuilding(2, 0)!;
            Equal(defaultHeadquarters.MaximumHealth, defaultHeadquarters.Health);

            var team0Start = simulation.ResourceForTeam(0);
            var team1Start = simulation.ResourceForTeam(1);
            var income = new List<P7IncomeEvent>();
            for (var tick = 0; tick < 32; tick++)
            {
                simulation.Step([]);
                income.AddRange(simulation.LastP7Income);
            }
            Equal(team0Start, simulation.ResourceForTeam(0));
            Equal(team1Start + 2 * ScenarioSimulation.NativePassiveP7Rate, simulation.ResourceForTeam(1));
            Equal(0, income.Count(item => item.TeamId == 0));
        }, CheckTags.Data);

        Check("every built SCN city slot lies on MAP pedestal cells", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var footprints = BuildingFootprintCatalog.Load(install.ExecutablePath);
            var checkedCells = 0;
            foreach (var file in Directory.GetFiles(install.DataFile("scenario"), "*.scn", SearchOption.AllDirectories))
            {
                var mapFile = Path.ChangeExtension(file, ".map");
                if (!File.Exists(mapFile)) continue;
                var map = TerrainMap.Load(mapFile);
                foreach (var team in ScenarioDefinition.Load(file).Teams.Where(team => team.HasCity))
                for (var slot = 0; slot < team.CitySlots.Count; slot++)
                {
                    if (team.CitySlots[slot].Level <= 0) continue;
                    foreach (var cell in footprints.CitySlotCells(team.CityOrigin!.Value, slot))
                    {
                        // 0x444F14 asserts load[ysize - 1 - z][x] bit 31, which the
                        // MAP loader 0x453320 fills from attribute bit 9.
                        if ((map[cell.X, map.Height - 1 - cell.Z].Ambient & 0x02) == 0)
                            throw new InvalidDataException($"{Path.GetFileName(file)} team {team.TeamId} slot {slot} cell {cell} is off the pedestal.");
                        checkedCells++;
                    }
                }
            }
            if (checkedCells == 0) throw new InvalidDataException("No built city slots found.");
            Console.WriteLine($"  city pedestals: {checkedCells} built slot cells on attribute bit 9");
        }, CheckTags.Data);

        Check("installed troop build timings come from the anim.dat FIN order", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var timings = TroopBuildTimings.Load(entities, install);
            int Ticks(string code) => timings.BuildTicks(entities.Entities.First(entity => entity.Code == code).Id)!.Value;
            Equal(22, Ticks("TRSC"));
            Equal(9, Ticks("EXPL"));
            Equal(37, Ticks("GRAY"));
        }, CheckTags.Data);

        Check("city buildings produce queued troops at their native exit after the build animation", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var (simulation, _) = DeterminismHarness.Load(install, rules, "mplayer/j4play01");
            var headquarters = simulation.CityBuilding(0, 0)!;
            // depend.txt item 7: the Human exploiter (entity 6), queue 2 (the
            // headquarters slot), exit variant 0 = origin (10,55) + (-4,0).
            const int exploiterItem = 7;
            simulation.Step([
                new ScheduledWorldCommand(simulation.TickCount, 0, new PurchaseIntent(0, exploiterItem)),
                new ScheduledWorldCommand(simulation.TickCount, 1, new ProduceUnitIntent(0, exploiterItem, headquarters.Seed.InstanceId)),
            ]);
            Equal(UnitProductionOutcome.Queued, simulation.LastUnitProductions.Single().Outcome);
            var queue = simulation.ProductionQueues.Single(item => item.TeamId == 0 && item.Queue == 2);
            var exit = new CellCoordinate(6, 55);
            Equal(false, queue.Ready);
            Equal(exit, queue.ReservedExit!.Value);
            Equal(true, simulation.GroundOccupancy.TryGetOwner(exit, out var holder) && holder == ScenarioSimulation.ProductionReservationOwner);
            var ticks = 0;
            UnitProducedEvent? produced = null;
            while (produced is null && ticks < 20)
            {
                simulation.Step([]);
                ticks++;
                produced = simulation.LastUnitProductions.SingleOrDefault(item => item.Outcome == UnitProductionOutcome.Produced);
            }
            Equal(9, ticks);
            var troop = simulation.Actor(produced!.EntityInstanceId)!;
            Equal(6, troop.Seed.EntityId);
            Equal(exit, troop.Movement.OccupiedCell);
            Equal(true, queue.Ready);
            Equal(0, queue.QueuedEntityIds.Count);
        }, CheckTags.Data);

        Check("purchased buildings rise in their city slot and run that slot's queue", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var (simulation, _) = DeterminismHarness.Load(install, rules, "mplayer/j4play01");
            // depend.txt item 1 is the Human barracks (slot 1, variant 0); item 9
            // the marine (entity 0, queue 0, exit (0,-3) from origin (10,55)).
            const int barracksItem = 1, marineItem = 9;
            var origin = new CellCoordinate(10, 55);
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new PurchaseIntent(0, barracksItem))]);
            var placed = simulation.LastBuildingPlacements.Single();
            Equal(BuildingDropOutcome.Placed, placed.Outcome);
            var barracks = simulation.CityBuilding(0, 1)!;
            Equal(placed.EntityInstanceId, barracks.Seed.InstanceId);
            Equal(rules.Footprints.CitySlotPosition(origin, 1), barracks.Movement.VisualPosition);
            Equal(true, rules.Footprints.TryResolveBuildingEntity(0, 0, 1, out var barracksEntity) && barracksEntity == barracks.Seed.EntityId);
            Equal(true, simulation.EconomyForTeam(0)!.CompletedItems.Contains(barracksItem));

            simulation.Step([
                new ScheduledWorldCommand(simulation.TickCount, 0, new PurchaseIntent(0, marineItem)),
                new ScheduledWorldCommand(simulation.TickCount, 1, new ProduceUnitIntent(0, marineItem, barracks.Seed.InstanceId)),
            ]);
            Equal(UnitProductionOutcome.Queued, simulation.LastUnitProductions.Single().Outcome);
            UnitProducedEvent? produced = null;
            var ticks = 0;
            while (produced is null && ticks < 40)
            {
                simulation.Step([]);
                ticks++;
                produced = simulation.LastUnitProductions.SingleOrDefault(item => item.Outcome == UnitProductionOutcome.Produced);
            }
            Equal(22, ticks);
            Equal(barracks.Seed.InstanceId, produced!.SourceBuildingInstanceId);
            Equal(new CellCoordinate(10, 52), simulation.Actor(produced.EntityInstanceId)!.Movement.OccupiedCell);
        }, CheckTags.Data);

        Check("building prerequisites follow the live city slot and its variant", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var file = install.DataFile("scenario", "mplayer", "j4play01") + ".scn";
            // Team 0 starts with a level-2 robot factory (slot 2, variant 1).
            var text = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), @"%City(\r?\n)1 -1 0 -1 0 -1",
                match => $"%City{match.Groups[1].Value}1 -1 0 -1 2 -1", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(text), path, rules, terrain: map);
            var economy = simulation.EconomyForTeam(0)!;
            // depend.txt: item 3 is robot factory 1 (slot 2, variant 0), item 5 robot
            // factory 2 (variant 1); 0x438220 accepts any live variant at least as high.
            Equal(true, economy.CompletedItems.Contains(3));
            Equal(true, economy.CompletedItems.Contains(5));

            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new PurchaseIntent(0, 1))]);
            Equal(true, economy.CompletedItems.Contains(1));
            Equal(PurchaseEligibility.Available, economy.Evaluate(rules.Dependencies, 9));
            // ApplyDamage drops health to zero before Destroy runs.
            var barracks = simulation.CityBuilding(0, 1)!;
            barracks.Health = 0;
            simulation.Destroy(barracks, new List<DestroyedActorEvent>());
            Equal(false, economy.CompletedItems.Contains(1));
            Equal(PurchaseEligibility.MissingPrerequisite, economy.Evaluate(rules.Dependencies, 9));
        }, CheckTags.Data);

        Check("a team without a city builds and trains nothing in a scenario with cities", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var file = install.DataFile("scenario", "human", "human01") + ".scn";
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Load(file), path, rules, terrain: map);
            Equal(false, simulation.HasCity(0));
            Equal(false, simulation.UsesPortConstructionAdapters());
            var start = simulation.ResourceForTeam(0);
            // Item 0 (Human exo center) has no prerequisite, so only the missing city refuses it.
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new PurchaseIntent(0, 0))]);
            Equal(PurchaseEligibility.NoCity, simulation.LastPurchaseReservations.Single().Eligibility);
            Equal(start, simulation.ResourceForTeam(0));
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new PlaceBuildingIntent(0, 0, new CellCoordinate(20, 20)))]);
            Equal(BuildingDropOutcome.NoCity, simulation.LastBuildingPlacements.Single().Outcome);
        }, CheckTags.Data);

        Check("the troop cap shares the free actor slots and refunds orders beyond it", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var file = install.DataFile("scenario", "mplayer", "j4play01") + ".scn";
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            var scenario = ScenarioDefinition.Load(file);
            var simulation = ScenarioSimulation.Create(scenario, path, rules, terrain: map);
            simulation.Step([]);
            // 0x41E6AC: (648 - critter groups - actors of city-less teams 0-8
            // (vents are team 8) - 100) / players with a city, at most 150.
            var cityless = simulation.Actors.Count(actor => !actor.IsDestroyed && actor.Seed.Team is >= 4 and <= 8);
            var expected = Math.Min(150, (648 - scenario.AutonomousSpawnGroups.Sum(group => group.DesiredPopulation) - cityless -
                                          simulation.PetraVents.Count - 100) / 4);
            Equal(expected, simulation.TroopCap);

            // 500 marines of the city-less team 4 push the cap below team 0's count.
            var crowd = string.Concat(Enumerable.Range(0, 500).Select(index => $"{2 + index % 100} {2 + index / 100} 0 4 -1 0\n"));
            var crowded = ScenarioSimulation.Create(ScenarioDefinition.Parse(File.ReadAllText(file) + crowd), path, rules, terrain: map);
            var headquarters = crowded.CityBuilding(0, 0)!;
            var start = crowded.ResourceForTeam(0);
            crowded.Step([
                new ScheduledWorldCommand(crowded.TickCount, 0, new PurchaseIntent(0, 7)),
                new ScheduledWorldCommand(crowded.TickCount, 1, new ProduceUnitIntent(0, 7, headquarters.Seed.InstanceId)),
            ]);
            Equal(true, crowded.TroopCap <= crowded.PlayerStatistic(0, 6));
            // The order queues and, in the same update, meets the cap.
            Equal(new[] { UnitProductionOutcome.Queued, UnitProductionOutcome.CapReached },
                crowded.LastUnitProductions.Select(production => production.Outcome).ToArray());
            Equal(start, crowded.ResourceForTeam(0));
            Equal(0, crowded.ProductionQueues.Single(queue => queue.TeamId == 0 && queue.Queue == 2).QueuedEntityIds.Count);
        }, CheckTags.Data);

        Check("original dependency catalog preserves P7 costs and prerequisite build items", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var dependencies = DependencyCatalog.Load(install.DataFile("gamestat", "depend.txt"));
            Equal(true, dependencies.Items.Count >= 80);
            var humanTroops = dependencies.Items[9];
            Equal(350, humanTroops.Cost);
            Equal(true, humanTroops.IsTroop);
            Equal(0, humanTroops.TroopEntityId!.Value);
            Equal(new[] { 1 }, humanTroops.PrerequisiteItemIds.ToArray());
            var grayTroops = dependencies.Items[23];
            Equal(350, grayTroops.Cost);
            Equal(8, grayTroops.TroopEntityId!.Value);
            Equal(new[] { 15 }, grayTroops.PrerequisiteItemIds.ToArray());
            Equal(2000, dependencies.Items[0].Cost);
            Equal(true, dependencies.Items[0].IsBuilding);
            Equal(false, dependencies.Items[0].TroopEntityId.HasValue);
            Equal(0, dependencies.Items[0].BuildingSlot!.Value);
            Equal(0, dependencies.Items[0].BuildingVariant!.Value);
            Equal(0, dependencies.Items[0].BuildingFaction!.Value);
            var napalm = dependencies.Items[80];
            Equal(ResearchEffectKind.Ability, napalm.ResearchEffect!.Value);
            Equal(false, napalm.IsStatUpgrade);
            var virusSac = dependencies.Items[54];
            Equal(ResearchEffectKind.Ability, virusSac.ResearchEffect!.Value);
            Equal(false, virusSac.IsStatUpgrade);
        }, CheckTags.Data);

        Check("P7 reservations require completed structures and do not complete them early", () =>
        {
            var dependencies = DependencyCatalog.Parse("3\n0 200 206 0 0 0 0 -1\n1 100 80 0 1 0 0 0 -1\n9 350 89 1 0 1 -1\n");
        var economy = new TeamEconomy(1_000);
        Equal(PurchaseEligibility.MissingPrerequisite, economy.Evaluate(dependencies, 9));
        Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 0));
        Equal(true, economy.MarkCompleted(dependencies, 0));
        Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 1));
        Equal(true, economy.MarkCompleted(dependencies, 1));
        Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 9));
        Equal(350, economy.P7);
            Equal(new[] { 9 }, economy.ReservedItems.ToArray());
            Equal(false, economy.CompletedItems.Contains(9));
        Equal(PurchaseEligibility.Available, economy.Evaluate(dependencies, 9));
        }, CheckTags.Data);

        Check("buildings and upgrades cannot charge P7 after completion or while reserved", () =>
        {
        var dependencies = DependencyCatalog.Parse("3\n0 200 206 0 0 0 0 -1\n1 150 80 2 0 0 1 -1\n9 50 89 1 0 -1\n");
        var economy = new TeamEconomy(1_000);
        Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 0));
        Equal(PurchaseEligibility.AlreadyReserved, economy.Evaluate(dependencies, 0));
        Equal(true, economy.MarkCompleted(dependencies, 0));
        Equal(PurchaseEligibility.AlreadyCompleted, economy.Evaluate(dependencies, 0));
        Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 1));
        Equal(true, economy.CompleteResearch(dependencies, 1));
        Equal(PurchaseEligibility.AlreadyCompleted, economy.Evaluate(dependencies, 1));
        Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 9));
        Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 9));
        Equal(550, economy.P7);
        }, CheckTags.Data);

        Check("Napalm ability research does not masquerade as a Cyborg weapon level", () =>
        {
        var dependencies = DependencyCatalog.Parse("2\n80 2000 131 2 4 0 2 -1\n54 2000 78 2 12 0 2 -1\n");
        var economy = new TeamEconomy(4_000);
        Equal(ResearchEffectKind.Ability, dependencies.Items[80].ResearchEffect!.Value);
        Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 80));
        Equal(true, economy.CompleteResearch(dependencies, 80));
        Equal(0, economy.CompletedUpgradeLevel(dependencies, targetEntityId: 4, category: 0));
        Equal(ResearchEffectKind.Ability, dependencies.Items[54].ResearchEffect!.Value);
        Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 54));
        Equal(true, economy.CompleteResearch(dependencies, 54));
        Equal(0, economy.CompletedUpgradeLevel(dependencies, targetEntityId: 12, category: 0));
        }, CheckTags.Data);

        Check("original tech tree gates executable-footprint building drops", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var dependencies = DependencyCatalog.Load(install.DataFile("gamestat", "depend.txt"));
            var weapons = WeaponCatalog.Load(install.DataFile("gamestat", "weapstat.txt"));
            var footprints = BuildingFootprintCatalog.Load(install.ExecutablePath);
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n7000\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 64];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 8, 8), footprints,
                weaponCatalog: weapons, dependencyCatalog: dependencies);
            simulation.Step([new ScheduledWorldCommand(1, 0, new PurchaseIntent(0, 0))]);
            Equal(PurchaseEligibility.Available, simulation.LastPurchaseReservations.Single().Eligibility);
            simulation.Step([new ScheduledWorldCommand(2, 0, new PlaceBuildingIntent(0, 0, new CellCoordinate(-1, -1)))]);
            Equal(BuildingDropOutcome.OutOfBounds, simulation.LastBuildingPlacements.Single().Outcome);
            Equal(true, simulation.EconomyForTeam(0)!.ReservedItems.Contains(0));
            simulation.Step([new ScheduledWorldCommand(3, 0, new PlaceBuildingIntent(0, 0, new CellCoordinate(3, 3)))]);
            var exoCenter = simulation.LastBuildingPlacements.Single();
            Equal(BuildingDropOutcome.Placed, exoCenter.Outcome);
            Equal(16, exoCenter.EntityId);
            Equal(true, simulation.EconomyForTeam(0)!.CompletedItems.Contains(0));
            simulation.Step([new ScheduledWorldCommand(4, 0, new PurchaseIntent(0, 1))]);
            Equal(PurchaseEligibility.Available, simulation.LastPurchaseReservations.Single().Eligibility);
            simulation.Step([new ScheduledWorldCommand(5, 0, new PlaceBuildingIntent(0, 1, new CellCoordinate(6, 3)))]);
            var barracks = simulation.LastBuildingPlacements.Single();
            Equal(BuildingDropOutcome.Placed, barracks.Outcome);
            Equal(17, barracks.EntityId);
            Equal(true, simulation.EconomyForTeam(0)!.CompletedItems.Contains(1));
            Equal(true, simulation.GroundOccupancy.IsOccupied(new CellCoordinate(5, 2)));
            simulation.Step([new ScheduledWorldCommand(6, 0, new PurchaseIntent(0, 2))]);
            Equal(PurchaseEligibility.Available, simulation.LastPurchaseReservations.Single().Eligibility);
            simulation.Step([new ScheduledWorldCommand(7, 0, new PlaceBuildingIntent(0, 2, new CellCoordinate(3, 6)))]);
            Equal(BuildingDropOutcome.Placed, simulation.LastBuildingPlacements.Single().Outcome);
            simulation.Step([new ScheduledWorldCommand(8, 0, new PurchaseIntent(0, 59))]);
            Equal(PurchaseEligibility.Available, simulation.LastPurchaseReservations.Single().Eligibility);
            simulation.Step([new ScheduledWorldCommand(9, 0, new ResearchIntent(0, 59, barracks.EntityInstanceId))]);
            Equal(ResearchOutcome.Completed, simulation.LastResearchCompletions.Single().Outcome);
            Equal(true, simulation.EconomyForTeam(0)!.CompletedItems.Contains(59));
            simulation.Step([new ScheduledWorldCommand(10, 0, new PurchaseIntent(0, 9))]);
            Equal(PurchaseEligibility.Available, simulation.LastPurchaseReservations.Single().Eligibility);
            simulation.Step([new ScheduledWorldCommand(11, 0, new ProduceUnitIntent(0, 9, barracks.EntityInstanceId))]);
            var trooper = simulation.LastUnitProductions.Single();
            Equal(UnitProductionOutcome.Produced, trooper.Outcome);
            Equal(0, trooper.EntityId);
            var producedActor = simulation.Actor(trooper.EntityInstanceId)!;
            Equal(1, simulation.WeaponUpgradeLevel(producedActor));
            Equal(2, simulation.EffectiveWeaponFor(producedActor)!.Id);
            Equal(125, simulation.EffectiveWeaponFor(producedActor)!.Damage);
            Equal(4, simulation.Actors.Count);
            Equal(true, simulation.GroundOccupancy.IsOccupied(producedActor.Movement.OccupiedCell));
            // A production structure's SCN anchor is part of its decoded mask,
            // not a legal unit exit. Production must search outside that mask.
            Equal(false, footprints.OccupiedCells(barracks.EntityId, barracks.Origin).Contains(producedActor.Movement.OccupiedCell));
        }, CheckTags.Data);

        Check("executable building footprints decode", () =>
        {
            var footprints = BuildingFootprintCatalog.Load(Path.Combine(dataPath, "dc.exe"));
            Equal(true, footprints.TryGetOffsets(16, out var humanBuilding));
            Equal(4, humanBuilding.Count);
            Equal(new CellCoordinate(-3, 0), humanBuilding[0]);
            Equal(new CellCoordinate(-2, 1), humanBuilding[^1]);
            Equal(false, footprints.TryGetOffsets(25, out _));
            Equal(true, footprints.TryResolveBuildingEntity(0, 0, 0, out var humanExoCenter));
            Equal(16, humanExoCenter);
            Equal(true, footprints.TryResolveBuildingEntity(0, 1, 2, out var humanFactoryTwo));
            Equal(19, humanFactoryTwo);
            Equal(true, footprints.TryResolveBuildingEntity(1, 0, 2, out var grayBreederOne));
            Equal(30, grayBreederOne);
            Equal(
                new[] { new CellCoordinate(7, 20), new CellCoordinate(8, 20), new CellCoordinate(7, 21), new CellCoordinate(8, 21) },
                footprints.OccupiedCells(16, new CellCoordinate(10, 20)).ToArray());
        }, CheckTags.Data);

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

            if (entityCount == 0) throw new InvalidOperationException("Installed scenarios seeded no entities.");
            // Shipped six-field SCN placements contain no unambiguous build-table
            // entities. Pedestals/vents are terrain or special records; buildings
            // enter occupancy through the runtime delivery system.
            Equal(0, occupiedCount);
            Console.WriteLine($"  seeded scenario worlds: {entityCount} entities / {occupiedCount} static occupied cells");
        }, CheckTags.Data);

        Check("every building, troop, research and unit of both races builds, trains, moves, attacks, uses its special and dies", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            foreach (var sweep in new[] { 0, 1 }.AsParallel().AsOrdered().Select(race => CatalogSweep.Run(install, rules, race)).ToArray())
            {
                if (sweep.Failures.Count != 0)
                    throw new InvalidOperationException($"{sweep.Failures.Count} failure(s), run --catalog-sweep for the report: " + string.Join("; ", sweep.Failures));
                // depend.txt has 7 buildings, 9 troops and 24 research items per race.
                Equal((7, 9, 24), (sweep.Buildings, sweep.Troops, sweep.Research));
            }
        }, CheckTags.Data | CheckTags.Slow);
    }
}
