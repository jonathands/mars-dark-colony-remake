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

/// <summary>Unit specials: towers, mines, steal stances, healing, Inspire, and the ground specials.</summary>
internal static class SpecialsChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Specials", name, tags, action);

        Check("unit special-command identities preserve recovered HUD mappings", () =>
        {
            Equal(true, UnitSpecialCommandCatalog.TryGet("EXPL", out var exploiter));
            Equal(UnitSpecialCommand.HarvestPetra, exploiter.Command);
            Equal(74, exploiter.InterfaceFrame);
            Equal(true, UnitSpecialCommandCatalog.TryGet("ENGI", out var engineer));
            Equal(UnitSpecialCommand.DeployMine, engineer.Command);
            Equal(69, engineer.InterfaceFrame);
            Equal(true, UnitSpecialCommandCatalog.TryGet("BEON", out var beon));
            Equal(UnitSpecialCommand.HealUnits, beon.Command);
            Equal(122, beon.InterfaceFrame);
            Equal(true, UnitSpecialCommandCatalog.TryGet("SARG", out var cyborg));
            Equal(UnitSpecialCommand.StealMoney, cyborg.Command);
            Equal(false, UnitSpecialCommandCatalog.TryGet("SARGSTL", out _));
            Equal(true, UnitSecondaryCommandCatalog.TryGet("SARG", out var cyborgSecondary));
            Equal("NAPALM ATTACK", cyborgSecondary.Label);
            Equal(72, cyborgSecondary.InterfaceFrame);
            Equal(80, cyborgSecondary.RequiredResearchItemId!.Value);
            Equal(50, cyborgSecondary.CandidateEffectWeaponId!.Value);
            Equal(UnitCommandActivation.MapTarget, cyborgSecondary.Activation);
            Equal(true, UnitSecondaryCommandCatalog.TryGet("PSYC", out var psySecondary));
            Equal("DISEASE ATTACK", psySecondary.Label);
            Equal(73, psySecondary.InterfaceFrame);
            Equal(54, psySecondary.RequiredResearchItemId!.Value);
            Equal(51, psySecondary.CandidateEffectWeaponId!.Value);
            Equal(false, UnitSecondaryCommandCatalog.TryGet("DROP", out _));
            var humanCaptainValues = new int[32];
            humanCaptainValues[28] = 132;
            humanCaptainValues[29] = 57;
            Equal(true, UnitSecondaryCommandCatalog.TryGet(
                new EntityDefinition(70, "TRSC", "Human captain", 0, humanCaptainValues), out var dropshipSecondary));
            Equal("DROP SHIP", dropshipSecondary.Label);
            Equal(125, dropshipSecondary.InterfaceFrame);
            Equal(57, dropshipSecondary.CandidateEffectWeaponId!.Value);
            Equal(UnitCommandActivation.MapTarget, dropshipSecondary.Activation);
            var grayCaptainValues = new int[32];
            grayCaptainValues[28] = 134;
            grayCaptainValues[29] = 60;
            Equal(true, UnitSecondaryCommandCatalog.TryGet(
                new EntityDefinition(74, "GRAY", "Gray captain", 1, grayCaptainValues), out var saucerSecondary));
            Equal("SAUCER", saucerSecondary.Label);
            Equal(126, saucerSecondary.InterfaceFrame);
            Equal(60, saucerSecondary.CandidateEffectWeaponId!.Value);
            Equal(false, UnitSecondaryCommandCatalog.TryGet("SARGSTL", out _));
            var commander = new EntityDefinition(69, "TRSC", "Human lieutenant", 0, new int[32]);
            Equal(true, UnitSpecialCommandCatalog.TryGet(commander, out var inspire));
            Equal(UnitSpecialCommand.InspireTroops, inspire.Command);
            Equal(UnitCommandActivation.Immediate, inspire.Activation);
            Equal(121, inspire.InterfaceFrame);
            Equal(true, UnitSpecialCommandCatalog.TryGet("XENO", out var xenowort));
            Equal(UnitSpecialCommand.DeployTurret, xenowort.Command);
            Equal(false, UnitSpecialCommandCatalog.TryGet("TRSC", out _));
        });

        Check("unit command profiles keep common, contextual, and pending slots distinct", () =>
        {
            var exploiter = new EntityDefinition(6, "EXPL", "Exploiter", 0, ValuesWith(movementSpeed: 25));
            var profile = UnitCommandProfiles.Describe(exploiter, hasResolvedWeapon: false);
            Equal(true, profile.CanMove);
            Equal(true, profile.CanStop);
            Equal(true, profile.CanUseWaypoints);
            Equal(false, profile.CanAttack);
            Equal(UnitSpecialCommand.HarvestPetra, profile.ContextualCommand!.Command);
            Equal(UnitCommandActivation.MapTarget, profile.ContextualCommand.Activation);
            Equal(false, profile.HasSecondaryCommand);

            var cyborg = new EntityDefinition(4, "SARG", "Cyborg", 0, ValuesWith(movementSpeed: 20));
            var cyborgProfile = UnitCommandProfiles.Describe(cyborg, hasResolvedWeapon: true);
            Equal(true, cyborgProfile.CanAttack);
            Equal(UnitSpecialCommand.StealMoney, cyborgProfile.ContextualCommand!.Command);
            Equal("NAPALM ATTACK", cyborgProfile.SecondaryCommand!.Label);
            var deployedCyborgProfile = UnitCommandProfiles.Describe(cyborg, effectiveMovementSpeed: 0, hasResolvedWeapon: true);
            Equal(false, deployedCyborgProfile.CanMove);
            Equal(UnitSpecialCommand.StealMoney, deployedCyborgProfile.ContextualCommand!.Command);

            var staticTower = new EntityDefinition(0, "TURR", "Tower", 0, ValuesWith(movementSpeed: 0));
            var towerProfile = UnitCommandProfiles.Describe(staticTower, hasResolvedWeapon: true);
            Equal(false, towerProfile.CanMove);
            Equal(false, towerProfile.CanUseWaypoints);
            Equal(true, towerProfile.CanAttack);
            Equal(true, towerProfile.CanStop);

            var secondExploiter = UnitCommandProfiles.Describe(exploiter, hasResolvedWeapon: false);
            Equal(UnitSpecialCommand.HarvestPetra,
                UnitCommandProfiles.CommonContextualCommand([profile, secondExploiter])!.Command);
            Equal(true, UnitCommandProfiles.CommonContextualCommand([profile, UnitCommandProfiles.Describe(
                new EntityDefinition(17, "ENGI", "Engineer", 0, ValuesWith(movementSpeed: 20)), hasResolvedWeapon: false)]) is null);
            Equal(true, UnitCommandProfiles.CommonContextualCommand([]) is null);
            Equal("NAPALM ATTACK", UnitCommandProfiles.CommonSecondaryCommand([cyborgProfile, cyborgProfile])!.Label);
            Equal(true, UnitCommandProfiles.CommonSecondaryCommand([cyborgProfile, profile]) is null);
            var mixedSelection = UnitCommandProfiles.DescribeSelection([cyborgProfile, profile]);
            Equal(2, mixedSelection.Count);
            Equal(true, mixedSelection.AnyCanStop);
            Equal(true, mixedSelection.AnyCanMove);
            Equal(true, mixedSelection.AnyCanUseWaypoints);
            Equal(true, mixedSelection.AnyCanAttack);
            Equal(true, mixedSelection.CommonContextualCommand is null);
            Equal(true, mixedSelection.CommonSecondaryCommand is null);
            var emptySelection = UnitCommandProfiles.DescribeSelection([]);
            Equal(0, emptySelection.Count);
            Equal(false, emptySelection.AnyCanStop);
            Equal(false, emptySelection.AnyCanMove);
            Equal(false, emptySelection.AnyCanUseWaypoints);
            Equal(false, emptySelection.AnyCanAttack);
            Equal(800, UnitSelectionCommandProfile.MaximumActors);
            Equal(new[] { 2, 4 }, UnitCommandProfiles.ApplyActorSelection([1, 2], [4, 2], toggle: false).ToArray());
            Equal(new[] { 1, 4 }, UnitCommandProfiles.ApplyActorSelection([1, 2], [4, 2], toggle: true).ToArray());
            Equal(UnitSelectionCommandProfile.MaximumActors,
                UnitCommandProfiles.ApplyActorSelection([], Enumerable.Range(0, UnitSelectionCommandProfile.MaximumActors + 1), toggle: false).Count);

            var ground = new EntityDefinition(0, "GROUND", "Ground", 0, ValuesWith(movementSpeed: 20));
            var airValues = ValuesWith(movementSpeed: 20);
            airValues[10] = 7; // armor class alone does not make an actor fly
            airValues[12] = 1; // gamestat value 13, runtime +0x60
            var air = new EntityDefinition(1, "AIR", "Air", 0, airValues);
            var mineValues = ValuesWith(movementSpeed: 0);
            mineValues[10] = 7;
            mineValues[14] = 1;
            var mine = new EntityDefinition(45, "HMINE", "Mine", 0, mineValues);
            Equal(NativeActorSelectionLayer.Ground, UnitCommandProfiles.NativeSelectionLayer(ground));
            Equal(NativeActorSelectionLayer.Air, UnitCommandProfiles.NativeSelectionLayer(air));
            Equal(NativeActorSelectionLayer.Mine, UnitCommandProfiles.NativeSelectionLayer(mine));
            var altSelection = UnitSelectionLayerFilter.FromModifiers(control: false, alt: true);
            Equal(false, altSelection.Includes(ground));
            Equal(true, altSelection.Includes(air));
            Equal(true, altSelection.Includes(mine));
            var controlSelection = UnitSelectionLayerFilter.FromModifiers(control: true, alt: false);
            Equal(true, controlSelection.Includes(ground));
            Equal(false, controlSelection.Includes(air));
            Equal(true, controlSelection.Includes(mine));
            var mineOnlySelection = UnitSelectionLayerFilter.FromModifiers(control: true, alt: true);
            Equal(false, mineOnlySelection.Includes(ground));
            Equal(false, mineOnlySelection.Includes(air));
            Equal(true, mineOnlySelection.Includes(mine));
            var owners = new Dictionary<int, int> { [1] = 0, [2] = 1, [3] = 0, [4] = 2 };
            Equal(new[] { 1, 3 }, UnitCommandProfiles.PreferLocalOwnerSelection([4, 3, 2, 1], owners, localOwner: 0).ToArray());
            Equal(new[] { 2, 4 }, UnitCommandProfiles.PreferLocalOwnerSelection([4, 2], owners, localOwner: 0).ToArray());
            Equal(new[] { 69, 70, 71, 72, 73, 74, 75, 76 }, NativeUnitSelectionHotkeys.EntityIds(1).ToArray());
            Equal(new[] { 0, 8 }, NativeUnitSelectionHotkeys.EntityIds(2).ToArray());
            Equal(new[] { 1, 9 }, NativeUnitSelectionHotkeys.EntityIds(10).ToArray());
            Equal(0, NativeUnitSelectionHotkeys.EntityIds(11).Count);
        });

        Check("tower builders deploy into their paired armed static forms", () =>
        {
            var catalog = EntityCatalog.Parse("5\nTURR 0 10 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nT 0 5 0 1 1 1 -1 -1 1 1 6 60 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nXDEPLOY 1 5 0 1 1 1 -1 -1 1 1 6 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nLAB 0 0 0 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 100 4 0 0 0 0 0\n");
            var dependencies = DependencyCatalog.Parse("1\n71 0 0 2 1 0 1 -1\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n100\n%Money\nTEAM 1 1\n1\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 -1 0\n2 0 3 0 -1 0\n3 0 4 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 4), weaponCatalog: weapons, dependencyCatalog: dependencies);
            var economy = simulation.EconomyForTeam(0)!;
            Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 71));
            Equal(true, economy.CompleteResearch(dependencies, 71));
            simulation.Step([new ScheduledWorldCommand(1, 0, new DeployTowerIntent(1))]);
            var tower = simulation.Actor(1)!;
            Equal(TowerDeploymentOutcome.Deployed, simulation.LastTowerDeployments.Single().Outcome);
            Equal(1, tower.DeployedEntityId!.Value);
            Equal("T", simulation.EffectiveDefinition(tower).Code);
            Equal(60, tower.MaximumHealth);
            Equal(60, tower.Health);
            Equal(0, simulation.EffectiveDefinition(tower).MovementSpeed);
            Equal(1, simulation.EffectiveWeaponFor(tower)!.Id);
            Equal(1, simulation.WeaponUpgradeLevel(tower));
            simulation.Step([new ScheduledWorldCommand(2, 0, new AttackIntent(1, 3))]);
            for (var tick = 0; tick < 100 && simulation.Actor(3)!.Health == 100; tick++) simulation.Step([]);
            Equal(true, simulation.Actor(3)!.Health < 100);
            simulation.Step([new ScheduledWorldCommand(3, 0, new StopIntent(1))]);
            Equal(1, tower.DeployedEntityId!.Value);
            simulation.Step([new ScheduledWorldCommand(4, 0, new MoveIntent(1, new CellCoordinate(2, 1)))]);
            Equal(0, simulation.LastMoveOutcomes.Single().StepCount);
        });

        Check("a stealing stance forms after state 13 and retracts when it finds no victim", () =>
        {
            var catalog = EntityCatalog.Parse("2\nSARG 0 10 45 10 10 13 14 14 125 150 4 800 0 31 0 1 0 0 0 0 3 96 0 1 4 0 0 5 129 50 12 0\nSARGSTL 0 10 0 10 10 -1 -1 -1 125 150 4 800 0 1 0 1 0 0 0 0 0 0 0 0 0 0 0 5 0 0 78 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 4));
            simulation.Step([new ScheduledWorldCommand(1, 0, new DeployStealIntent(1))]);
            var thief = simulation.Actor(1)!;
            Equal(StealDeploymentOutcome.Preparing, simulation.LastStealDeployments.Single().Outcome);
            Equal(ScenarioSimulation.NativeImmediateSpecialTicks, thief.StealTransitionTicksRemaining);
            // The timer counts down from the next update; the type swaps on its 50th.
            for (var tick = 1; tick < ScenarioSimulation.NativeImmediateSpecialTicks; tick++) simulation.Step([]);
            Equal("SARG", simulation.EffectiveDefinition(thief).Code);
            simulation.Step([]);
            Equal(StealDeploymentOutcome.NoVictim, simulation.LastStealDeployments.Single().Outcome);
            Equal("SARGSTL", simulation.EffectiveDefinition(thief).Code);
            Equal(0, simulation.EffectiveDefinition(thief).MovementSpeed);
            Equal(800, thief.Health);
            // Without a victim the stance starts retracting at once (0x416784).
            Equal(ScenarioSimulation.NativeImmediateSpecialTicks, thief.StealTransitionTicksRemaining);
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new MoveIntent(1, new CellCoordinate(2, 1)))]);
            Equal(0, simulation.LastMoveOutcomes.Single().StepCount);
            for (var tick = 2; tick < ScenarioSimulation.NativeImmediateSpecialTicks; tick++) simulation.Step([]);
            Equal("SARGSTL", simulation.EffectiveDefinition(thief).Code);
            simulation.Step([]);
            Equal(StealDeploymentOutcome.Retracted, simulation.LastStealDeployments.Single().Outcome);
            Equal(false, thief.DeployedEntityId.HasValue);
            Equal("SARG", simulation.EffectiveDefinition(thief).Code);
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new MoveIntent(1, new CellCoordinate(2, 1)))]);
            Equal(true, simulation.LastMoveOutcomes.Single().StepCount > 0);
        });

        Check("a steal stance links the first visible deployed harvester and halves its pulses", () =>
        {
            // Both forms see 10 cells (the largest sight tree), so both stances see
            // the harvester at (1,1) from 10 and 9 cells away.
            var catalog = EntityCatalog.Parse("4\nEXPL 0 255 25 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nSARG 0 10 45 10 10 -1 -1 -1 1 1 4 800 0 31 0 1 0 0 0 0 3 96 0 1 4 0 0 5 129 50 3 0\nSARGSTL 0 10 0 10 10 -1 -1 -1 1 1 4 800 0 1 0 1 0 0 0 0 0 0 0 0 0 0 0 5 0 0 2 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n" +
                "11 1 2 0 -1 0\n10 1 2 0 -1 0\n1 1 0 1 -1 0\n1 1 40 0 5000\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16 * 4];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 16, 4),
                petraFlowRules: new PetraFlowRules(1, 0, 11));
            var harvester = simulation.Actor(3)!;
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new HarvestVentIntent(3, 0))]);
            for (var tick = 0; tick < ScenarioSimulation.NativeHarvesterAttachTicks; tick++) simulation.Step([]);
            Equal("EDPLY", simulation.EffectiveDefinition(harvester).Code);

            simulation.Step([
                new ScheduledWorldCommand(simulation.TickCount, 0, new DeployStealIntent(1)),
                new ScheduledWorldCommand(simulation.TickCount, 1, new DeployStealIntent(2)),
            ]);
            for (var tick = 0; tick < ScenarioSimulation.NativeImmediateSpecialTicks; tick++) simulation.Step([]);
            // 0x417E91: the first stance keeps the harvester; the second retracts.
            Equal(new[] { StealDeploymentOutcome.Deployed, StealDeploymentOutcome.VictimTaken },
                simulation.LastStealDeployments.Select(deployment => deployment.Outcome).ToArray());
            Equal(3, simulation.Actor(1)!.StealVictimInstanceId!.Value);
            Equal(1, harvester.ThiefInstanceId!.Value);

            // An 11-unit pulse pays 5 to each side; the odd unit is lost, and the
            // vent still loses all 11.
            var reservoir = simulation.PetraVents[0].RemainingReservoir;
            simulation.Step([]);
            var theft = simulation.LastP7Thefts.Single();
            Equal(1, theft.ThiefInstanceId);
            Equal(3, theft.VictimHarvesterInstanceId);
            Equal(5, theft.Amount);
            Equal(new[] { 5, 5 }, simulation.LastP7Income.OrderBy(entry => entry.TeamId).Select(entry => entry.Amount).ToArray());
            Equal(reservoir - 11, simulation.PetraVents[0].RemainingReservoir);

            // A retracted harvester is no victim: the stance notices and retracts.
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new RetractHarvesterIntent(3))]);
            simulation.Step([]);
            Equal(StealDeploymentOutcome.VictimLost,
                simulation.LastStealDeployments.Single(deployment => deployment.EntityInstanceId == 1).Outcome);
            Equal(false, harvester.ThiefInstanceId.HasValue);
        });

        Check("engineer mine deployment resolves the faction-matched HMINE form", () =>
        {
            var catalog = EntityCatalog.Parse("3\nENGI 0 15 30 6 4 -1 -1 -1 1 1 5 800 0 0 0 1 0 0 0 0 0 0 0 0 0 0 0 3 0 0 0 0\nUNUSED 0 0 0 0 0 -1 -1 -1 1 1 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nHMINE 0 40 0 6 4 38 38 38 1 1 7 800 0 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 4));
            simulation.Step([new ScheduledWorldCommand(1, 0, new DeployMineIntent(1))]);
            var deployment = simulation.LastMineDeployments.Single();
            Equal(MineDeploymentOutcome.Preparing, deployment.Outcome);
            Equal(2, deployment.EntityId);
            Equal(1, deployment.EntityInstanceId);
            Equal(new CellCoordinate(1, 1), deployment.Target);
            Equal(ScenarioSimulation.NativeImmediateSpecialTicks, simulation.Actor(1)!.MineDeployTicksRemaining);
            Equal(false, simulation.MineOccupancy.IsOccupied(new CellCoordinate(1, 1)));
            Equal(true, simulation.GroundOccupancy.TryGetOwner(new CellCoordinate(1, 1), out _));
            for (var tick = 0; tick < ScenarioSimulation.NativeImmediateSpecialTicks; tick++) simulation.Step([]);
            Equal(MineDeploymentOutcome.Deployed, simulation.LastMineDeployments.Single().Outcome);
            Equal(true, simulation.MineOccupancy.TryGetOwner(new CellCoordinate(1, 1), out var mineId));
            Equal(deployment.EntityInstanceId, mineId);
            Equal(false, simulation.GroundOccupancy.IsOccupied(new CellCoordinate(1, 1)));
            Equal("HMINE", simulation.EffectiveDefinition(simulation.Actor(mineId)!).Code);
            Equal(0, simulation.EffectiveDefinition(simulation.Actor(mineId)!).MovementSpeed);
        });

        Check("a deployed mine follows native three-trigger integrity and weapon cooldown", () =>
        {
            var catalog = EntityCatalog.Parse("4\nENGI 0 15 30 6 4 -1 -1 -1 1 1 5 800 0 0 0 1 0 0 0 0 0 0 0 0 0 0 0 3 0 0 0 0\nUNUSED 0 0 0 0 0 -1 -1 -1 1 1 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nHMINE 0 40 0 6 4 38 38 38 1 1 7 800 0 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 10000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n38 weapons 6 38 2 100 90 1 2 -1 -1 0 0\n");
            var areas = AreaEffectCatalog.Parse("1\n2 3\nNONE\n0 0 0\n0 100 0\n0 0 0\n0 0 0\n0 100 0\n0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n1 1 3 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 25];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 5), weaponCatalog: weapons, areaEffects: areas);
            simulation.Step([new ScheduledWorldCommand(1, 0, new DeployMineIntent(1))]);
            for (var tick = 0; tick < ScenarioSimulation.NativeImmediateSpecialTicks; tick++) simulation.Step([]);
            Equal(38, simulation.LastWeaponFires.Single().WeaponId);
            Equal(475, simulation.Actor(1)!.Health);
            Equal(2, simulation.MineTriggersRemainingFor(simulation.Actor(1)!));
            Equal(2, simulation.Actor(1)!.CooldownTicks);
            simulation.Step([]);
            Equal(0, simulation.LastWeaponFires.Count);
            simulation.Step([]);
            Equal(150, simulation.Actor(1)!.Health);
            Equal(1, simulation.MineTriggersRemainingFor(simulation.Actor(1)!));
            simulation.Step([]);
            Equal(0, simulation.LastWeaponFires.Count);
            simulation.Step([]);
            Equal(true, simulation.Actor(1)!.IsDestroyed);
            Equal(0, simulation.MineTriggersRemainingFor(simulation.Actor(1)!));
            Equal(false, simulation.Actor(2)!.IsDestroyed);
        });

        Check("with the installed tables, a mine fires at a hostile that stops beside it, jitter and all", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            // Team 0: a Human mine guy (43) at (2,2). Team 1: a Gray warrior (8) at (8,2).
            const string scenarioText = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n1\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n2 2 43 0 -1 0\n8 2 8 1 -1 0\n";
            var pathBytes = new byte[PathRegionMap.RouteTableSize + 12 * 6];
            pathBytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(scenarioText), PathRegionMap.Parse(pathBytes, 12, 6), rules);
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new DeployMineIntent(1))]);
            for (var tick = 0; tick < ScenarioSimulation.NativeImmediateSpecialTicks + 2; tick++) simulation.Step([]);
            Equal("HMINE", simulation.EffectiveDefinition(simulation.Actor(1)!).Code);
            // The warrior stops on the neighbouring cell, where the movement
            // jitter leaves it off the centre and beyond an 8.8 range of one.
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new MoveIntent(2, new CellCoordinate(3, 2)))]);
            var fired = false;
            for (var tick = 0; tick < 200 && !fired; tick++)
            {
                simulation.Step([]);
                fired = simulation.LastWeaponFires.Any(fire => fire.SourceActorInstanceId == 1);
            }
            // It fires once the warrior's step claims the neighbouring cell in the ground grid.
            Equal(true, fired);
        }, CheckTags.Data);

        Check("native ground-special fields drive researched Napalm area fire", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var weapons = WeaponCatalog.Load(install.DataFile("gamestat", "weapstat.txt"));
            var effects = AreaEffectCatalog.Load(install.DataFile("gamestat", "boomstat.txt"));
            var dependencies = DependencyCatalog.Load(install.DataFile("gamestat", "depend.txt"));
            Equal(true, catalog[4].HasGroundSpecialAttack);
            Equal(50, catalog[4].GroundSpecialWeaponId);
            Equal(true, catalog[12].HasGroundSpecialAttack);
            Equal(51, catalog[12].GroundSpecialWeaponId);

            const string scenarioText = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n1\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 2 4 0 -1 0\n3 2 0 1 -1 0\n4 2 1 1 -1 0\n";
            var pathBytes = new byte[PathRegionMap.RouteTableSize + 8 * 6];
            pathBytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(
                ScenarioDefinition.Parse(scenarioText), catalog, PathRegionMap.Parse(pathBytes, 8, 6),
                weaponCatalog: weapons, dependencyCatalog: dependencies, areaEffects: effects);

            simulation.Step([new ScheduledWorldCommand(1, 0, new GroundSpecialAttackIntent(1, new CellCoordinate(3, 2)))]);
            Equal(GroundSpecialAttackOutcome.ResearchRequired, simulation.LastGroundSpecialAttacks.Single().Outcome);

            var economy = simulation.EconomyForTeam(0) ?? throw new InvalidOperationException("Team 0 economy missing.");
            economy.AddP7(10_000);
            Equal(true, economy.SeedCompletedBuilding(dependencies, 2));
            Equal(true, economy.SeedCompletedBuilding(dependencies, 4));
            Equal(true, economy.SeedCompletedBuilding(dependencies, 6));
            Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 79));
            Equal(true, economy.CompleteResearch(dependencies, 79));
            Equal(PurchaseEligibility.Available, economy.TryReserve(dependencies, 80));
            Equal(true, economy.CompleteResearch(dependencies, 80));

            simulation.Step([new ScheduledWorldCommand(2, 0, new GroundSpecialAttackIntent(1, new CellCoordinate(3, 2)))]);
            Equal(GroundSpecialAttackOutcome.Accepted, simulation.LastGroundSpecialAttacks.Single().Outcome);
            var fired = simulation.LastWeaponFires.Any(fire => fire.WeaponId == 50);
            var peakHeight = simulation.Projectiles.Select(projectile => projectile.HeightRaw).DefaultIfEmpty().Max();
            for (var tick = 0; tick < 100 && simulation.LastProjectileImpacts.Count == 0; tick++)
            {
                simulation.Step([]);
                fired |= simulation.LastWeaponFires.Any(fire => fire.WeaponId == 50);
                peakHeight = Math.Max(peakHeight, simulation.Projectiles.Select(projectile => projectile.HeightRaw).DefaultIfEmpty().Max());
            }
            Equal(true, fired);
            Equal(true, peakHeight > 0);
            var impact = simulation.LastProjectileImpacts.Single();
            Equal(-1, impact.TargetActorInstanceId);
            Equal(50, impact.WeaponId);
            Equal(FixedPointPosition.AtCellCenter(new CellCoordinate(3, 2)), impact.Position);
            Equal(true, simulation.Actor(2)!.Health < simulation.Actor(2)!.MaximumHealth);
        }, CheckTags.Data);

        Check("healing uses the original class-7 resistance formula", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var matrix = DamageMatrix.Load(install.DataFile("gamestat", "mbullet.txt"));
            var catalog = EntityCatalog.Parse("3\nBEON 0 255 25 5 3 -1 -1 -1 1 1 2 400 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 0 255 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nATTACKER 1 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            ((int[])catalog[0].Values)[24] = 1;
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 100 10 90 4 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 0 0 0 -1 0\n1 0 1 0 -1 0\n2 0 2 1 -1 0\n";
            var pathBytes = new byte[PathRegionMap.RouteTableSize + 4 * 2];
            pathBytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(
                ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(pathBytes, 4, 2),
                weaponCatalog: weapons, damageMatrix: matrix);

            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(3, 2))]);
            for (var tick = 0; tick < 100 && simulation.Actor(2)!.Health == simulation.Actor(2)!.MaximumHealth; tick++) simulation.Step([]);
            var target = simulation.Actor(2) ?? throw new InvalidOperationException("Friendly target was not seeded.");
            if (target.Health == target.MaximumHealth)
            {
                var attacker = simulation.Actor(3) ?? throw new InvalidOperationException("Hostile attacker was not seeded.");
                throw new InvalidOperationException($"Hostile test actor did not damage the healing target (order={simulation.LastAttackOrders.FirstOrDefault()?.Outcome}, weaponFires={simulation.LastWeaponFires.Count}, attacker={attacker.Definition.Code}, slots={string.Join(',', attacker.Definition.WeaponSlots)}).");
            }
            var missingBeforeHeal = target.MaximumHealth - target.Health;

            simulation.Step([new ScheduledWorldCommand(2, 0, new StopIntent(3))]);
            simulation.Step([new ScheduledWorldCommand(2, 0, new HealAreaIntent(1))]);
            var heal = simulation.LastHeals.Single();
            Equal(HealOutcome.Healed, heal.Outcome);
            // 0x413E21 uses the 8.8 table factor: (36 * factor) >> 8.
            Equal(Math.Min(missingBeforeHeal, (36 * matrix.NativeFactor(7, target.Definition.ArmorClass)) >> 8), heal.Amount);
            Equal(target.MaximumHealth - missingBeforeHeal + heal.Amount, target.Health);
            Equal(0, simulation.Actor(1)!.AbilityCharge);
            // Stat 9 adds the restored health to the healed actor's player.
            Equal(heal.Amount, simulation.PlayerStatistic(target.Seed.Team, 9));

            simulation.Step([new ScheduledWorldCommand(3, 0, new HealAreaIntent(1))]);
            Equal(HealOutcome.InsufficientCharge, simulation.LastHeals.Single().Outcome);
            var rechargeTicks = 0;
            while (simulation.Actor(1)!.AbilityCharge < ScenarioSimulation.NativeHealMinimumCharge && rechargeTicks < 200)
            {
                simulation.Step([]);
                rechargeTicks++;
            }
            Equal(true, rechargeTicks is >= 97 and <= 128);
            Equal(ScenarioSimulation.NativeHealMinimumCharge, simulation.Actor(1)!.AbilityCharge);
            simulation.Step([new ScheduledWorldCommand(4, 0, new HealAreaIntent(1))]);
            Equal(HealOutcome.NoEligibleTargets, simulation.LastHeals.Single().Outcome);
            Equal(ScenarioSimulation.NativeHealMinimumCharge, simulation.Actor(1)!.AbilityCharge);
        }, CheckTags.Data);

        Check("native immediate-special gate maps to gamestat value 27", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            Equal(2, entities[1].ImmediateSpecialCode);   // TURR
            Equal(3, entities[43].ImmediateSpecialCode);  // ENGI
            Equal(5, entities[4].ImmediateSpecialCode);   // SARG
            Equal(0, entities[6].ImmediateSpecialCode);   // EXPL uses another deploy path
            Equal(0, entities[49].ImmediateSpecialCode);  // BEON uses another heal path
            Equal(196, entities[69].ImmediateSpecialCode); // Human lieutenant
            Equal(true, entities[69].HasImmediateAreaEffect);
            Equal(6, entities[69].ImmediateAreaTargetLimit);
            Equal(12, entities[72].ImmediateAreaTargetLimit);
            Equal(0, entities[41].ImmediateSpecialCode);  // deployed tower is one-way
            Equal(0, entities[45].ImmediateSpecialCode);  // deployed mine is one-way
            Equal(7, entities[47].ImmediateSpecialCode);  // EDPLY retracts
            Equal(5, entities[77].ImmediateSpecialCode);  // SARGSTL retracts
        }, CheckTags.Data);

        Check("commander Inspire waits 50 ticks then applies the recovered aim countdown", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var weapons = WeaponCatalog.Load(install.DataFile("gamestat", "weapstat.txt"));
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n10 10 69 0 -1 0\n10 5 0 0 -1 0\n11 5 0 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 32 * 32];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var scenario = ScenarioDefinition.Parse(source);
            Equal(69, scenario.Placements[0].EntityId);
            var simulation = ScenarioSimulation.Create(scenario, entities,
                PathRegionMap.Parse(bytes, 32, 32), weaponCatalog: weapons);

            Equal(69, simulation.Actor(1)!.Definition.Id);
            Equal(196, simulation.Actor(1)!.Definition.ImmediateSpecialCode);
            Equal(true, simulation.Actor(1)!.Definition.HasImmediateAreaEffect);
            simulation.Step([new ScheduledWorldCommand(1, 0, new InspireTroopsIntent(1))]);
            Equal(InspireOutcome.Preparing, simulation.LastInspires.Single().Outcome);
            Equal(ScenarioSimulation.NativeImmediateSpecialTicks, simulation.Actor(1)!.InspireCastTicksRemaining);
            Equal(0, simulation.Actor(2)!.InspirationTicksRemaining);
            for (var tick = 1; tick < ScenarioSimulation.NativeImmediateSpecialTicks; tick++) simulation.Step([]);
            Equal(1, simulation.Actor(1)!.InspireCastTicksRemaining);
            Equal(0, simulation.Actor(2)!.InspirationTicksRemaining);

            simulation.Step([]);
            var applied = simulation.LastInspires.Where(effect => effect.Outcome == InspireOutcome.Applied).ToArray();
            Equal(true, applied.Any(effect => effect.TargetActorInstanceId == 2));
            Equal(false, applied.Any(effect => effect.TargetActorInstanceId == 3));
            Equal(0, simulation.Actor(1)!.InspireCastTicksRemaining);
            Equal(true, simulation.Actor(2)!.InspirationTicksRemaining is >= 20 and <= 35);
            Equal(1, simulation.Actor(2)!.InspirationSourceActorInstanceId!.Value);
            Equal(0, simulation.Actor(3)!.InspirationTicksRemaining);

            var beforeCadence = simulation.Actor(2)!.InspirationTicksRemaining;
            for (var tick = 0; tick < ScenarioSimulation.NativeInspireCountdownCadence; tick++) simulation.Step([]);
            Equal(beforeCadence - 1, simulation.Actor(2)!.InspirationTicksRemaining);
        }, CheckTags.Data);

        Check("original mine selection layer is limited to the paired HMINE definitions", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            Equal(new[] { 45, 46 }, entities.Entities
                .Where(entity => entity.UsesNativeMineLayer)
                .Select(entity => entity.Id)
                .ToArray());
            Equal(true, entities.Entities.Where(entity => entity.Id is 45 or 46)
                .All(entity => UnitCommandProfiles.NativeSelectionLayer(entity) == NativeActorSelectionLayer.Mine));
        }, CheckTags.Data);

        Check("installed button dictionary corroborates Cyborg and Psy-raider special frames", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var buttons = File.ReadAllText(install.DataFile("intrface", "bdf.txt"));
            var hud = File.ReadAllText(install.DataFile("intrface", "maine"));
            var interfaceDefinition = InterfaceDefinition.Parse(hud);
            Equal(640, interfaceDefinition.Width);
            Equal(480, interfaceDefinition.Height);
            Equal(new InterfaceRectangle(518, 92, 40, 20), interfaceDefinition.Controls[0].Bounds);
            Equal(77, interfaceDefinition.Controls[3].Frame!.Value);
            Equal(new InterfaceRectangle(518, 275, 59, 41), interfaceDefinition.Controls[196].Bounds);
            Equal(131, interfaceDefinition.Controls[196].Frame!.Value);
            Equal(new InterfaceRectangle(518, 112, 59, 41), interfaceDefinition.Controls[87].Bounds);
            Equal(new InterfaceRectangle(518, 112, 59, 41), interfaceDefinition.Controls[110].Bounds);
            Equal(new InterfaceRectangle(4, 460, 20, 19), interfaceDefinition.Controls[147].Bounds);
            Equal(57, interfaceDefinition.Controls[149].Frame!.Value);
            Equal("Move Only", interfaceDefinition.LabelFor(33)!);
            var moveOnly = interfaceDefinition.Controls[33];
            Equal(InterfaceControlKind.CheckButton, moveOnly.Kind);
            Equal(new InterfaceRectangle(518, 153, 59, 41), moveOnly.Bounds);
            Equal(63, moveOnly.Frame!.Value);
            Equal(122, interfaceDefinition.Controls[138].Frame!.Value);
            Equal(new InterfaceRectangle(520, 404, 15, 1), interfaceDefinition.Controls[79].Bounds);
            Equal(new InterfaceRectangle(10, 425, 72, 1), interfaceDefinition.Controls[204].Bounds);
            Equal(new InterfaceRectangle(10, 440, 72, 1), interfaceDefinition.Controls[203].Bounds);
            Equal(new InterfaceRectangle(50, 462, 61, 1), interfaceDefinition.Controls[148].Bounds);
            Equal(new InterfaceRectangle(524, 456, 72, 17), interfaceDefinition.Controls[75].Bounds);
            Equal(104, interfaceDefinition.Controls[75].Frame!.Value);
            Equal(new InterfaceRectangle(480, 463, 3, 1), interfaceDefinition.Controls[200].Bounds);
            var mainButtons = Sprite.Load(install.DataFile("intrface", "mainbut.spr"));
            Equal(true, Enumerable.Range(104, 10).All(index => mainButtons.Frames[index].Width == 12 && mainButtons.Frames[index].Height == 17));
            Equal("Set waypoints.", interfaceDefinition.LabelFor(180)!);
            Equal("Select target.", interfaceDefinition.LabelFor(181)!);
            Equal("Too many units", interfaceDefinition.LabelFor(182)!);
            Equal("Issuing refund", interfaceDefinition.LabelFor(183)!);
            Equal(true, interfaceDefinition.Groups[40].Values.Contains(33));
            Equal(true, interfaceDefinition.Groups[40].Values.Contains(150));
            // Group 40's common controls are the source authority for the App
            // layout and shortcut adapter. Keep its overlapping-group geometry
            // distinct from the options/build/research button grids.
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"pushb\s+150\s+150\s+518\s+112\s+59\s+41\s+62\b"));
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"checkb\s+33\s+33\s+518\s+153\s+59\s+41\s+63\b"));
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"checkb\s+35\s+35\s+518\s+194\s+59\s+41\s+65\b"));
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"pushb\s+36\s+36\s+518\s+235\s+59\s+41\s+66\b"));
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"pushb\s+37\s+37\s+518\s+276\s+59\s+41\s+74\b"));
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"in_text\s+79\s+0\s+520\s+404\b"));
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"in_text\s+204\s+0\s+10\s+425\b"));
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"in_text\s+203\s+0\s+10\s+440\b"));
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"in_text\s+148\s+0\s+50\s+462\b"));
            Equal(true, buttons.Contains("62 stop (S)", StringComparison.OrdinalIgnoreCase));
            Equal(true, buttons.Contains("63 move (M)", StringComparison.OrdinalIgnoreCase));
            Equal(true, buttons.Contains("66 waypoints (W)", StringComparison.OrdinalIgnoreCase));
            Equal(true, buttons.Contains("72 cyborg call cruise missile", StringComparison.OrdinalIgnoreCase));
            Equal(true, buttons.Contains("73 psych raider deploy virus", StringComparison.OrdinalIgnoreCase));
            Equal(72, UnitSecondaryCommandCatalog.TryGet("SARG", out var cyborg) ? cyborg.InterfaceFrame : -1);
            Equal(73, UnitSecondaryCommandCatalog.TryGet("PSYC", out var psy) ? psy.InterfaceFrame : -1);
            // The same physical research buttons advance from stat technology to
            // the two recovered special-capability records.
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"count\s+130\s+130\s+518\s+317\s+59\s+41\s+83\b"));
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"count\s+131\s+131\s+518\s+317\s+59\s+41\s+35\b"));
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"count\s+60\s+60\s+518\s+317\s+59\s+41\s+89\b"));
            Equal(true, System.Text.RegularExpressions.Regex.IsMatch(hud, @"count\s+78\s+78\s+518\s+316\s+59\s+41\s+45\b"));
        }, CheckTags.Data);
    }
}
