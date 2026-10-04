using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
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

Check("projectile elapsed lifetime advances with authoritative flight", () =>
{
    var projectile = new ProjectileState(1, 2, 3, 4, 5, new FixedPointPosition(100, 200), 7, -9, 3);
    Equal(3, projectile.TotalTicks);
    Equal(3, projectile.MaximumLifetimeTicks);
    Equal(0, projectile.ElapsedTicks);
    Equal(0, projectile.AnimationTicks);
    projectile.Step();
    Equal(false, projectile.ReachedAimedPosition);
    Equal(1, projectile.ElapsedTicks);
    Equal(2, projectile.RemainingTicks);
    Equal(new FixedPointPosition(107, 191), projectile.Position);
});

Check("mode-one timed projectiles follow the recovered 17-sample height arc", () =>
{
    var projectile = new ProjectileState(1, 2, -1, 50, 20, new FixedPointPosition(128, 128),
        32, 0, 8, 64, new CellCoordinate(1, 0), new CellCoordinate(1, 0), projectileMode: 1);
    projectile.Step();
    Equal(3, projectile.HeightRaw); // table[16] 25 * 8 >> 6
    projectile.Step();
    Equal(35, projectile.HeightRaw); // table[14] 280 * 8 >> 6
    Equal(1, projectile.ProjectileMode);
});

Check("weapon catalog maps stale header columns to recovered native fields", () =>
{
    var catalog = WeaponCatalog.Parse("1\n7 TEST 3 10 75 250 60 12 1 -1 -1 1 0\n");
    var weapon = catalog.Weapons[7];
    Equal(1, weapon.AreaEffectTemplateId);
    Equal(-1, weapon.BurstShotLimit);
    Equal(-1, weapon.BurstReloadTicks);
    Equal(0, weapon.PostFireReset);
    Equal(1, weapon.ProjectileMode);
    Equal(69, weapon.ProjectileLifetimeTicks);
});

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
    airValues[10] = 7;
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

Check("scenario simulation retains explicit attack targets until stopped", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 4 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n1 1 1 1 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 12];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 3), weaponCatalog: weapons);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    var attackOutcome = simulation.LastAttackOrders.Single().Outcome;
    var fired = simulation.LastWeaponFires.Count != 0;
    for (var tick = 0; tick < 20 && simulation.Actor(2)!.Health == 100; tick++) simulation.Step([]);
    Equal(2, simulation.Actor(1)!.AttackTargetInstanceId!.Value);
    Equal(AttackOrderOutcome.Acquired, attackOutcome);
    Equal(true, simulation.IsAttackTargetInRange(simulation.Actor(1)!));
    Equal((byte)64, simulation.Actor(1)!.Facing.Target);
    Equal(90, simulation.Actor(2)!.Health);
    Equal(true, fired);
    Equal(1, simulation.LastProjectileImpacts.Count);
    Equal(1, simulation.LastProjectileImpacts[0].SourceActorInstanceId);
    Equal(0, simulation.LastProjectileImpacts[0].WeaponClass);
    for (var tick = 0; tick < 9; tick++) simulation.Step([]);
    Equal(true, simulation.Actor(2)!.IsDestroyed);
    Equal(false, simulation.GroundOccupancy.IsOccupied(new CellCoordinate(1, 1)));
    Equal(true, simulation.Actor(1)!.AttackTargetInstanceId is null);
    Equal(1, simulation.LastDestroyedActors.Count);
    Equal(1, simulation.LastDestroyedActors[0].EntityId);
    simulation.Step([new ScheduledWorldCommand(2, 0, new StopIntent(1))]);
    Equal(true, simulation.Actor(1)!.AttackTargetInstanceId is null);
});

Check("weapon bursts use rate between shots then decoded reload", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    // rate=1, burst limit=2, reload=4. Projectile speed makes each hit resolve
    // within its firing tick, isolating cadence from flight time.
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 1 90 4 0 2 4 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n1 1 1 1 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 12];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 3), weaponCatalog: weapons);

    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    Equal(1, simulation.Actor(1)!.BurstShotCount);
    Equal(1, simulation.Actor(1)!.CooldownTicks);
    simulation.Step([]);
    Equal(0, simulation.Actor(1)!.BurstShotCount);
    Equal(4, simulation.Actor(1)!.CooldownTicks);
    for (var tick = 0; tick < 3; tick++)
    {
        simulation.Step([]);
        Equal(0, simulation.LastWeaponFires.Count);
    }
    simulation.Step([]);
    Equal(1, simulation.LastWeaponFires.Count);
    Equal(1, simulation.Actor(1)!.BurstShotCount);
});

Check("area-effect weapon applies its authored radial percentage", () =>
{
    var catalog = EntityCatalog.Parse("4\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 200 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 200 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nNEARBY 1 1 25 1 1 -1 -1 -1 1 1 0 200 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nFRIENDLY 0 1 25 1 1 -1 -1 -1 1 1 0 200 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BLAST 0 0 1 100 90 4 1 0 0 1 0\n");
    var areas = AreaEffectCatalog.Parse("1\n1 3\nNONE\n0 100 0\n0 100 50\n0 0 0\n0 0 0\n0 100 0\n0 0 0\n");
    const string source = "t\ni\nd\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n2 2 0 0 200 0\n3 2 1 1 200 0\n4 2 2 1 200 0\n3 1 3 0 200 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 30]; bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 6, 5), weaponCatalog: weapons, areaEffects: areas);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    for (var tick = 0; tick < 20 && simulation.LastProjectileImpacts.Count == 0; tick++) simulation.Step([]);
    Equal(100, simulation.Actor(2)!.Health);
    Equal(150, simulation.Actor(3)!.Health);
    Equal(175, simulation.Actor(4)!.Health);
});

Check("projectiles collide with an intervening hostile instead of remaining target locked", () =>
{
    var catalog = EntityCatalog.Parse("3\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nBLOCKER 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 20 25 90 5 0 0 0 0 0\n");
    const string source = "t\ni\nd\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 1 0 0 100 0\n3 1 1 1 100 0\n1 1 2 1 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 15]; bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 3), weaponCatalog: weapons);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    for (var tick = 0; tick < 20 && simulation.LastProjectileImpacts.Count == 0; tick++) simulation.Step([]);
    Equal(3, simulation.LastProjectileImpacts.Single().TargetActorInstanceId);
    Equal(75, simulation.Actor(3)!.Health);
    Equal(100, simulation.Actor(2)!.Health);
});

Check("area trajectories skip intervening actors and detonate at their launch-time cell", () =>
{
    var catalog = EntityCatalog.Parse("3\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nBLOCKER 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BLAST 0 0 100 25 15 10 1 0 0 1 0\n");
    var areas = AreaEffectCatalog.Parse("1\n1 3\nNONE\n0 0 0\n0 100 0\n0 0 0\n0 0 0\n0 100 0\n0 0 0\n");
    const string source = "t\ni\nd\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 1 0 0 100 0\n4 1 1 1 100 0\n1 1 2 1 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 21]; bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 7, 3), weaponCatalog: weapons, areaEffects: areas);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    Equal(new CellCoordinate(4, 1), simulation.Projectiles.Single().TimedImpactCell!.Value);
    for (var tick = 0; tick < 30 && simulation.LastProjectileImpacts.Count == 0; tick++) simulation.Step([]);
    Equal(new CellCoordinate(4, 1), simulation.LastProjectileImpacts.Single().Position.Cell);
    Equal(75, simulation.Actor(2)!.Health);
    Equal(100, simulation.Actor(3)!.Health);
});

Check("world ticks execute the native four projectile substeps", () =>
{
    Equal(4, ScenarioSimulation.NativeProjectileSubstepsPerTick);
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 100 10 15 10 0 0 0 0 0\n");
    const string source = "t\ni\nd\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 1 0 0 100 0\n4 1 1 1 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 18]; bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 6, 3), weaponCatalog: weapons);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    var projectile = simulation.Projectiles.Single();
    Equal(4, projectile.ElapsedTicks);
    Equal(1, projectile.AnimationTicks);
    Equal(new FixedPointPosition(FixedPointPosition.AtCellCenter(new CellCoordinate(0, 1)).XRaw + 60,
        FixedPointPosition.AtCellCenter(new CellCoordinate(0, 1)).ZRaw), projectile.Position);
});

Check("attack orders honor the recovered team relation matrix", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 4 0 0 0 0 0\n");
    const string sameTeam = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n2 0 1 0 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 12];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(sameTeam), catalog, PathRegionMap.Parse(bytes, 4, 3), weaponCatalog: weapons);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    Equal(AttackOrderOutcome.NonHostile, simulation.LastAttackOrders.Single().Outcome);
    Equal(false, simulation.Actor(1)!.AttackTargetInstanceId.HasValue);

    const string opposingTeams = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n2 1 1 1 100 0\n";
    var relations = TeamRelationMatrix.CreateDefault();
    relations.SetRelation(0, 1, 1);
    simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(opposingTeams), catalog, PathRegionMap.Parse(bytes, 4, 3), weaponCatalog: weapons, teamRelations: relations);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    Equal(AttackOrderOutcome.NonHostile, simulation.LastAttackOrders.Single().Outcome);
    relations.SetRelation(0, 1, 0);
    simulation.Step([new ScheduledWorldCommand(2, 0, new AttackIntent(1, 2))]);
    Equal(AttackOrderOutcome.Acquired, simulation.LastAttackOrders.Single().Outcome);
});

Check("attack orders pursue an out-of-range target through normal movement", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 2 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n7 1 1 1 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3), weaponCatalog: weapons);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    var fired = simulation.LastWeaponFires.Count != 0;
    for (var tick = 0; tick < 100 && !simulation.IsAttackTargetInRange(simulation.Actor(1)!); tick++)
    {
        simulation.Step([]);
        fired |= simulation.LastWeaponFires.Count != 0;
    }
    var pursuer = simulation.Actor(1)!;
    if (pursuer.Movement.OccupiedCell.X <= 1 || !simulation.IsAttackTargetInRange(pursuer) || !fired)
        throw new InvalidOperationException($"pursuit cell={pursuer.Movement.OccupiedCell}, targetCell={simulation.Actor(2)!.Movement.OccupiedCell}, range={weapons.Weapons[1].Range}, inRange={simulation.IsAttackTargetInRange(pursuer)}, fired={fired}, target={pursuer.AttackTargetInstanceId}, order={pursuer.MoveOrder?.Target}, playback={pursuer.Playback is not null}");
});

Check("attack-move acquires a visible hostile then resumes its destination", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 2 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n2 1 1 1 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3), weaponCatalog: weapons);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackMoveIntent(1, new CellCoordinate(8, 1)))]);
    Equal(AttackMoveOrderOutcome.Accepted, simulation.LastAttackMoveOrders.Single().Outcome);
    Equal(2, simulation.LastAttackMoveAcquisitions.Single().TargetActorInstanceId);
    for (var tick = 0; tick < 200 && simulation.Actor(1)!.AttackMoveDestination is not null; tick++) simulation.Step([]);
    Equal(true, simulation.Actor(2)!.IsDestroyed);
    Equal(new CellCoordinate(8, 1), simulation.Actor(1)!.Movement.OccupiedCell);
    Equal(false, simulation.Actor(1)!.AttackMoveDestination.HasValue);
});

Check("attack-move excludes a team marked cooperative in the relation matrix", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 2 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n2 1 1 1 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var relations = TeamRelationMatrix.CreateDefault();
    relations.SetRelation(0, 1, 1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3), weaponCatalog: weapons, teamRelations: relations);

    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackMoveIntent(1, new CellCoordinate(8, 1)))]);

    Equal(AttackMoveOrderOutcome.Accepted, simulation.LastAttackMoveOrders.Single().Outcome);
    Equal(0, simulation.LastAttackMoveAcquisitions.Count);
    Equal(false, simulation.Actor(1)!.AttackTargetInstanceId.HasValue);
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

Check("Exploiter vent deployment accelerates P7 and day night selects observation", () =>
{
    var catalog = EntityCatalog.Parse("2\nEXPL 0 255 25 2 9 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 8 5 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n1 1 40 0 100\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 16];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 4),
        petraFlowRules: new PetraFlowRules(100, 1, 4), dayNight: new DayNightCycle(2));
    var exploiter = simulation.Actors.Single();
    Equal(2, simulation.ObservationRange(exploiter));
    simulation.Step([new ScheduledWorldCommand(1, 0, new HarvestVentIntent(exploiter.Seed.InstanceId, 0))]);
    Equal(HarvesterDeploymentOutcome.Preparing, simulation.LastHarvesterDeployments.Single().Outcome);
    Equal(ScenarioSimulation.NativeHarvesterAttachTicks, simulation.PetraVents[0].AttachTicksRemaining);
    Equal(0, simulation.ResourceForTeam(0));
    simulation.Step([]);
    Equal(DayNightPhase.Night, simulation.DayNight.Phase);
    Equal(9, simulation.ObservationRange(exploiter));
    for (var tick = 1; tick < ScenarioSimulation.NativeHarvesterAttachTicks; tick++) simulation.Step([]);
    Equal(true, simulation.PetraVents[0].HarvesterInstanceId == exploiter.Seed.InstanceId);
    for (var tick = ScenarioSimulation.NativeHarvesterAttachTicks + 1;
         tick < 100; tick++) simulation.Step([]);
    Equal(5, simulation.ResourceForTeam(0));
    Equal(new[] { 1, 4 }, simulation.LastP7Income.Select(income => income.Amount).ToArray());
});

Check("team visibility follows each observer's decoded day and night range", () =>
{
    var catalog = EntityCatalog.Parse("1\nSCOUT 0 255 25 2 5 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n2 2 0 0 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 100];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 10), dayNight: new DayNightCycle(1));
    var scout = simulation.Actors.Single();
    Equal(true, simulation.IsCellVisibleToTeam(0, new CellCoordinate(4, 2)));
    Equal(false, simulation.IsCellVisibleToTeam(0, new CellCoordinate(5, 2)));
    simulation.Step([]);
    Equal(true, simulation.IsActorVisibleToTeam(0, scout));
    Equal(true, simulation.IsCellVisibleToTeam(0, new CellCoordinate(7, 2)));
    Equal(false, simulation.IsCellVisibleToTeam(0, new CellCoordinate(8, 2)));
});

Check("harvester deployment walks to a vent then attaches deterministically", () =>
{
    var catalog = EntityCatalog.Parse("2\nEXPL 0 255 25 2 9 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 8 5 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 0 0 0 100 0\n3 3 40 0 100\n";
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

Check("tower builders deploy into their paired armed static forms", () =>
{
    var catalog = EntityCatalog.Parse("5\nTURR 0 10 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nT 0 5 0 1 1 1 -1 -1 1 1 6 60 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nXDEPLOY 1 5 0 1 1 1 -1 -1 1 1 6 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nLAB 0 0 0 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 100 4 0 0 0 0 0\n");
    var dependencies = DependencyCatalog.Parse("1\n71 0 0 2 1 0 1 -1\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n100\n%Money\nTEAM 1 1\n1\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n2 0 3 0 100 0\n3 0 4 1 100 0\n";
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

Check("Cyborg stealing stance deploys and retracts through recovered forms", () =>
{
    var catalog = EntityCatalog.Parse("2\nSARG 0 10 45 10 10 13 14 14 125 150 4 800 0 31 0 1 0 0 0 0 3 96 0 1 4 0 0 5 129 50 12 0\nSARGSTL 0 10 0 10 10 -1 -1 -1 125 150 4 800 0 1 0 1 0 0 0 0 0 0 0 0 0 0 0 5 0 0 78 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 16];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 4));
    simulation.Step([new ScheduledWorldCommand(1, 0, new DeployStealIntent(1))]);
    var thief = simulation.Actor(1)!;
    Equal(StealDeploymentOutcome.Deployed, simulation.LastStealDeployments.Single().Outcome);
    Equal(1, thief.DeployedEntityId!.Value);
    Equal("SARGSTL", simulation.EffectiveDefinition(thief).Code);
    Equal(0, simulation.EffectiveDefinition(thief).MovementSpeed);
    Equal(800, thief.Health);
    simulation.Step([new ScheduledWorldCommand(2, 0, new MoveIntent(1, new CellCoordinate(2, 1)))]);
    Equal(0, simulation.LastMoveOutcomes.Single().StepCount);
    simulation.Step([new ScheduledWorldCommand(3, 0, new RetractStealIntent(1))]);
    Equal(StealDeploymentOutcome.Retracted, simulation.LastStealDeployments.Single().Outcome);
    Equal(false, thief.DeployedEntityId.HasValue);
    Equal("SARG", simulation.EffectiveDefinition(thief).Code);
    simulation.Step([new ScheduledWorldCommand(4, 0, new MoveIntent(1, new CellCoordinate(2, 1)))]);
    Equal(true, simulation.LastMoveOutcomes.Single().StepCount > 0);
});

Check("deployed SARGE intercepts half of nearby hostile miner income without visibility", () =>
{
    var catalog = EntityCatalog.Parse("4\nEXPL 0 255 25 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nSARG 0 10 45 1 1 -1 -1 -1 1 1 4 800 0 31 0 1 0 0 0 0 3 96 0 1 4 0 0 5 129 50 3 0\nSARGSTL 0 10 0 1 1 -1 -1 -1 1 1 4 800 0 1 0 1 0 0 0 0 0 0 0 0 0 0 0 5 0 0 2 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n1\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n11 1 2 0 100 0\n1 1 0 1 100 0\n1 1 40 0 100\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 16 * 4];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 16, 4),
        petraFlowRules: new PetraFlowRules(1, 0, 10), petraStealRules: new PetraStealRules(12, 1, 2));

    simulation.Step([
        new ScheduledWorldCommand(1, 0, new DeployStealIntent(1)),
        new ScheduledWorldCommand(1, 1, new HarvestVentIntent(2, 0)),
    ]);
    for (var tick = 0; tick < ScenarioSimulation.NativeHarvesterAttachTicks; tick++) simulation.Step([]);

    Equal(5, simulation.ResourceForTeam(0));
    Equal(5, simulation.ResourceForTeam(1));
    var theft = simulation.LastP7Thefts.Single();
    Equal(1, theft.ThiefInstanceId);
    Equal(2, theft.VictimHarvesterInstanceId);
    Equal(5, theft.Amount);
    Equal(new[] { 5, 5 }, simulation.LastP7Income.OrderBy(entry => entry.TeamId).Select(entry => entry.Amount).ToArray());
});

Check("engineer mine deployment resolves the faction-matched HMINE form", () =>
{
    var catalog = EntityCatalog.Parse("3\nENGI 0 15 30 6 4 -1 -1 -1 1 1 5 800 0 0 0 1 0 0 0 0 0 0 0 0 0 0 0 3 0 0 0 0\nUNUSED 0 0 0 0 0 -1 -1 -1 1 1 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nHMINE 0 40 0 6 4 38 38 38 1 1 7 800 0 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n";
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
    Equal(true, simulation.AlternateOccupancy.TryGetOwner(new CellCoordinate(1, 1), out _));
    for (var tick = 0; tick < ScenarioSimulation.NativeImmediateSpecialTicks; tick++) simulation.Step([]);
    Equal(MineDeploymentOutcome.Deployed, simulation.LastMineDeployments.Single().Outcome);
    Equal(true, simulation.MineOccupancy.TryGetOwner(new CellCoordinate(1, 1), out var mineId));
    Equal(deployment.EntityInstanceId, mineId);
    Equal(false, simulation.AlternateOccupancy.IsOccupied(new CellCoordinate(1, 1)));
    Equal("HMINE", simulation.EffectiveDefinition(simulation.Actor(mineId)!).Code);
    Equal(0, simulation.EffectiveDefinition(simulation.Actor(mineId)!).MovementSpeed);
});

Check("a deployed mine follows native three-trigger integrity and weapon cooldown", () =>
{
    var catalog = EntityCatalog.Parse("4\nENGI 0 15 30 6 4 -1 -1 -1 1 1 5 800 0 0 0 1 0 0 0 0 0 0 0 0 0 0 0 3 0 0 0 0\nUNUSED 0 0 0 0 0 -1 -1 -1 1 1 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nHMINE 0 40 0 6 4 38 38 38 1 1 7 800 0 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 10000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n38 weapons 6 38 2 100 90 1 2 -1 -1 0 0\n");
    var areas = AreaEffectCatalog.Parse("1\n2 3\nNONE\n0 0 0\n0 100 0\n0 0 0\n0 0 0\n0 100 0\n0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 800 0\n1 1 3 1 10000 0\n";
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

Check("mine acquisition uses native target priority instead of nearest instance ID", () =>
{
    var catalog = EntityCatalog.Parse("5\nENGI 0 15 30 6 4 -1 -1 -1 1 1 5 800 0 0 0 1 0 0 0 0 0 0 0 0 0 0 0 3 0 0 0 0\nUNUSED 0 0 0 0 0 -1 -1 -1 1 1 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nHMINE 0 40 0 6 4 38 38 38 1 1 7 800 0 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nUNARMED 1 1 25 1 1 -1 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nARMED 1 1 25 1 1 39 39 39 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("2\n38 MINE 6 38 20 100 1 1 2 -1 -1 0 0\n39 GUN 0 0 20 10 1 1 0 -1 -1 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n2 2 0 0 800 0\n2 3 3 1 1000 0\n2 1 4 1 1000 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 25];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog,
        PathRegionMap.Parse(bytes, 5, 5), weaponCatalog: weapons);
    simulation.Step([new ScheduledWorldCommand(1, 0, new DeployMineIntent(1))]);
    for (var tick = 0; tick < ScenarioSimulation.NativeImmediateSpecialTicks; tick++) simulation.Step([]);
    Equal(3, simulation.Projectiles.Single().TargetActorInstanceId);
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

Check("scenario simulation preserves queued move waypoints", () =>
{
    var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n";
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

Check("active move order caps native waypoint list and ignores consecutive duplicates", () =>
{
    var order = new ActiveMoveOrder(new CellCoordinate(1, 1));
    Equal(true, order.TryAppendWaypoint(new CellCoordinate(2, 1)));
    Equal(false, order.TryAppendWaypoint(new CellCoordinate(2, 1)));
    Equal(new[] { new CellCoordinate(2, 1) }, order.PendingWaypoints.ToArray());
    for (var index = 3; index <= 9; index++) Equal(true, order.TryAppendWaypoint(new CellCoordinate(index, 1)));
    Equal(ActiveMoveOrder.MaximumWaypoints, order.PendingWaypointCount);
    Equal(false, order.TryAppendWaypoint(new CellCoordinate(10, 1)));
});

Check("scenario simulation stops an in-flight move coherently", () =>
{
    var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n";
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
    Equal(new CellCoordinate(1, 1), actor.Movement.OccupiedCell);
    Equal(new FixedPointPosition(1 * 256 + 128, 1 * 256 + 128), actor.Movement.VisualPosition);
    Equal(true, simulation.GroundOccupancy.IsOccupied(new CellCoordinate(1, 1)));
    Equal(false, simulation.GroundOccupancy.IsOccupied(reserved));
    Equal(true, actor.Playback is null);
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

Check("scenario simulation repairs a blocked local route before waiting", () =>
{
    var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n";
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

    Check("original area-effect templates retain radial patterns and weapon links", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var effects = AreaEffectCatalog.Load(install.DataFile("gamestat", "boomstat.txt"));
        var weapons = WeaponCatalog.Load(install.DataFile("gamestat", "weapstat.txt"));
        Equal(13, effects.Templates.Count);
        Equal(5, effects.Templates[1].PatternSize);
        Equal(new[] { "NUKE", "GASY" }, effects.Templates[1].EffectNames.ToArray());
        Equal(100, effects.Templates[1].DamagePattern[2][2]);
        Equal(7, effects.Templates[10].PatternSize);
        Equal(new[] { "NAPALM" }, effects.Templates[10].EffectNames.ToArray());
        Equal(100, effects.Templates[10].AimWeights[1][1]);
        Equal(1, weapons.Weapons[10].AreaEffectTemplateId);
        Equal(10, weapons.Weapons[50].AreaEffectTemplateId);
        Equal(true, weapons.Weapons.Values.Where(weapon => weapon.HasAreaEffect)
            .All(weapon => effects.Templates.ContainsKey(weapon.AreaEffectTemplateId)));
    });

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

        const string scenarioText = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n1\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 2 4 0 100 0\n3 2 0 1 100 0\n4 2 1 1 100 0\n";
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

        const string humanScenario = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n1\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 2 71 0 100 0\n";
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

        const string grayScenario = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n1\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 2 74 1 100 0\n8 2 0 0 100 0\n";
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
    });

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
    });

    Check("campaign mission text preserves briefings messages and outcomes", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var mission = ScenarioMissionText.LoadForScenario(install.DataFile("scenario", "human", "human01.scn"));
        Equal(true, mission.Briefing.Contains("MARS, COMMANDER", StringComparison.Ordinal));
        Equal("MISSION COMPLETE...PREPARE FOR PICKUP, COMMANDER", mission.Messages[2]);
        Equal(true, mission.Outcomes.ContainsKey("001"));
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
        var scenario = ScenarioDefinition.Parse($"{header}TEAM 0 1\n{candidate.BuildingFaction}\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 0 {entityId} 0 100 0\n");
        var bytes = new byte[PathRegionMap.RouteTableSize + 1];
        bytes[^1] = 1;
        var path = PathRegionMap.Parse(bytes, 1, 1);
        var simulation = ScenarioSimulation.Create(scenario, catalog, path, footprints, dependencyCatalog: dependencies);
        Equal(true, simulation.EconomyForTeam(0)!.CompletedItems.Contains(candidate.Id));
    });

    Check("healing uses the original class-7 resistance formula", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var matrix = DamageMatrix.Load(install.DataFile("gamestat", "mbullet.txt"));
        var catalog = EntityCatalog.Parse("3\nBEON 0 255 25 5 3 -1 -1 -1 1 1 2 400 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 0 255 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nATTACKER 1 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
        ((int[])catalog[0].Values)[24] = 1;
        var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 100 10 90 4 0 0 0 0 0\n");
        const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 0 0 0 100 0\n1 0 1 0 100 0\n2 0 2 1 100 0\n";
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
        Equal(Math.Min(missingBeforeHeal, 36 * matrix[7, target.Definition.ArmorClass] / 256), heal.Amount);
        Equal(target.MaximumHealth - missingBeforeHeal + heal.Amount, target.Health);
        Equal(0, simulation.Actor(1)!.AbilityCharge);

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
    });

    Check("original weapon catalog loads", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var weapons = WeaponCatalog.Load(install.DataFile("gamestat", "weapstat.txt"));
        Equal(64, weapons.Weapons.Count);
        var humanWeapon = weapons.Weapons[1];
        Equal("Human weapon 0", humanWeapon.DisplayName);
        Equal(0, humanWeapon.WeaponClass);
        Equal(100, humanWeapon.Damage);
        Equal(4, humanWeapon.Range);
        Equal(0, humanWeapon.AreaEffectTemplateId);
        Equal(0, humanWeapon.ProjectileMode);
        Equal(1, weapons.Weapons[10].AreaEffectTemplateId);
        Equal(1, weapons.Weapons[10].ProjectileMode);
        var effects = WeaponEffectCatalog.Build(weapons, install.DataFile("animate"));
        var barragerWeapon = weapons.Weapons.Values.First(weapon => weapon.Sprite.Equals("BARR", StringComparison.OrdinalIgnoreCase));
        Equal(true, effects.Bullet(barragerWeapon.Id) is not null);
        var spakWeapon = weapons.Weapons.Values.First(weapon => weapon.Sprite.Equals("SPAK", StringComparison.OrdinalIgnoreCase));
        Equal("SPAKEXPLODE0", effects.Impact(spakWeapon.Id)!.AnimationName);
        var napalm = weapons.Weapons[50];
        Equal("BARR", napalm.Sprite);
        Equal(6, napalm.WeaponClass);
        Equal(10, napalm.AreaEffectTemplateId);
        Equal(4, napalm.ProjectileMode);
        var psyEffect = weapons.Weapons[51];
        Equal("BARR", psyEffect.Sprite);
        Equal(12, psyEffect.AreaEffectTemplateId);
        Equal(4, psyEffect.ProjectileMode);
        Equal(5, weapons.Weapons[57].ProjectileMode);
        Equal(6, weapons.Weapons[58].ProjectileMode);
        Equal(7, weapons.Weapons[59].ProjectileMode);
        Equal(8, weapons.Weapons[60].ProjectileMode);
        Equal(9, weapons.Weapons[63].ProjectileMode);
        Equal(10, weapons.Weapons[64].ProjectileMode);
        var spak = weapons.Weapons[37];
        Equal(5, spak.AreaEffectTemplateId);
        Equal(3, spak.BurstShotLimit);
        Equal(30, spak.BurstReloadTicks);
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
    });

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
    });

    Check("commander Inspire waits 50 ticks then applies the recovered aim countdown", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
        var weapons = WeaponCatalog.Load(install.DataFile("gamestat", "weapstat.txt"));
        const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n10 10 69 0 100 0\n10 5 0 0 100 0\n11 5 0 1 100 0\n";
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
    });

    Check("boomstat aim weights scatter ordinary area shots while Inspire locks center", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
        var weapons = WeaponCatalog.Load(install.DataFile("gamestat", "weapstat.txt"));
        var areaSource = entities.Entities.First(entity => entity.MovementSpeed > 0 && !entity.HasImmediateAreaEffect &&
            entity.WeaponSlots[0] >= 0 && weapons.Weapons[entity.WeaponSlots[0]].AreaEffectTemplateId == 1);
        var forcedTopLeft = AreaEffectCatalog.Parse("1\n1 3\nNONE\n0 0 0\n0 100 0\n0 0 0\n100 0 0\n0 0 0\n0 0 0\n");
        const string header = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n";
        var bytes = new byte[PathRegionMap.RouteTableSize + 32 * 32];
        bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
        var path = PathRegionMap.Parse(bytes, 32, 32);

        var ordinaryScenario = ScenarioDefinition.Parse(header +
            $"10 5 {areaSource.Id} 0 100 0\n15 5 0 1 100 0\n");
        var ordinary = ScenarioSimulation.Create(ordinaryScenario, entities, path,
            weaponCatalog: weapons, areaEffects: forcedTopLeft);
        ordinary.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
        for (var tick = 0; tick < 32 && ordinary.Projectiles.Count == 0; tick++) ordinary.Step([]);
        Equal(new CellCoordinate(14, 4), ordinary.Projectiles.Single().TimedImpactCell!.Value);

        var inspiredScenario = ScenarioDefinition.Parse(header +
            $"10 10 69 0 100 0\n10 5 {areaSource.Id} 0 100 0\n15 5 0 1 100 0\n");
        var inspired = ScenarioSimulation.Create(inspiredScenario, entities, path,
            weaponCatalog: weapons, areaEffects: forcedTopLeft);
        inspired.Step([new ScheduledWorldCommand(1, 0, new InspireTroopsIntent(1))]);
        for (var tick = 0; tick < ScenarioSimulation.NativeImmediateSpecialTicks; tick++) inspired.Step([]);
        Equal(true, inspired.Actor(2)!.InspirationTicksRemaining > 0);
        inspired.Step([new ScheduledWorldCommand(2, 0, new AttackIntent(2, 3))]);
        for (var tick = 0; tick < 32 && inspired.Projectiles.Count == 0; tick++) inspired.Step([]);
        Equal(new CellCoordinate(15, 5), inspired.Projectiles.Single().TimedImpactCell!.Value);
    });

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
    });

    Check("native viewport hotkeys resolve the installed Human and Gray unit pairs", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
        var expected = new[]
        {
            "TRSC,TRSC,TRSC,TRSC,GRAY,GRAY,GRAY,GRAY",
            "TRSC,GRAY", "REAP,SCYT", "BEON,ZISP", "SCGM,ORTU",
            "BARR,ATRIL", "ENGI,SLOM", "SARG,PSYC", "EXPL,SLUG", "TURR,XENO",
        };
        for (var functionKey = 1; functionKey <= expected.Length; functionKey++)
            Equal(expected[functionKey - 1], string.Join(',', NativeUnitSelectionHotkeys.EntityIds(functionKey)
                .Select(entityId => entities[entityId].Code)));
    });

    Check("Cyborg and Psy-raider firing assets expose native common-fire variants", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var sarg = AnimationDefinition.Load(install.DataFile("animate", "sarg.fin"));
        var psyc = AnimationDefinition.Load(install.DataFile("animate", "psyc.fin"));
        Equal(true, sarg.Animations.Any(animation => animation.Name.StartsWith("SARGFIREA", StringComparison.Ordinal)));
        Equal(true, sarg.Animations.Any(animation => animation.Name.StartsWith("SARGFIREB", StringComparison.Ordinal)));
        Equal(true, psyc.Animations.Any(animation => animation.Name.StartsWith("PSYCFIRE", StringComparison.Ordinal)));
        Equal(false, psyc.Animations.Any(animation => animation.Name.StartsWith("PSYCFIREA", StringComparison.Ordinal) || animation.Name.StartsWith("PSYCFIREB", StringComparison.Ordinal)));
    });

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
    });

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
    });

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
});

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
});

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
});

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
    });

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

        if (files.Length == 0) throw new InvalidOperationException("Installed PTH corpus is empty.");
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
});

Check("Single Player War catalog exposes complete faction-matched scenarios", () =>
{
    var catalog = SinglePlayerWarCatalog.Load(GameInstallation.Open(dataPath));
    Equal(56, catalog.Scenarios.Count);
    Equal("j4play01", catalog.Scenarios[0].Stem);
    Equal("4 Kingdoms", catalog.Scenarios[0].DisplayName);
    Equal(54, catalog.ForRace(0).Count);
    Equal(30, catalog.ForRace(1).Count);
    foreach (var scenario in catalog.ForRace(0))
    {
        Equal(true, scenario.EnabledTeamForRace(0) is not null);
        Equal(true, scenario.TryCreateLaunch(0, out var launch));
        Equal(scenario.Stem, launch.Stem);
        Equal(scenario.EnabledTeamForRace(0)?.TeamId ?? -1, launch.LocalTeamId);
        Equal(SinglePlayerWarSettings.Default, launch.Settings);
    }
    foreach (var scenario in catalog.ForRace(1))
    {
        Equal(true, scenario.EnabledTeamForRace(1) is not null);
        Equal(true, scenario.TryCreateLaunch(1, out var launch));
        Equal(scenario.Stem, launch.Stem);
        Equal(scenario.EnabledTeamForRace(1)?.TeamId ?? -1, launch.LocalTeamId);
    }
    foreach (var scenario in catalog.Scenarios.Where(scenario => scenario.EnabledTeamForRace(1) is null))
        Equal(false, scenario.TryCreateLaunch(1, out _));
    var configured = new SinglePlayerWarSettings(3, 2, true, true, 500, 25, 3);
    Equal(true, catalog.ForRace(0)[0].TryCreateLaunch(0, configured, out var configuredLaunch));
    Equal(configured, configuredLaunch.Settings);
    Equal(false, catalog.ForRace(0)[0].TryCreateLaunch(0, configured with { P7FlowPercent = 110 }, out _));

    // Free-War SCNs seed entity 69 (Human lieutenant) for both teams. The
    // selected launch must transform only the local team's commander slot;
    // otherwise choosing Gray still gives the player a Human marine.
    var twoRaceMap = catalog.Scenarios.Single(scenario => scenario.Stem == "d2play01");
    Equal(true, twoRaceMap.TryCreateLaunch(1, configured with { CommanderRank = 2 }, out var grayLaunch));
    var grayDefinition = grayLaunch.ApplyTo(twoRaceMap.Definition);
    Equal(75, grayDefinition.Placements.Single(placement => placement.Team == grayLaunch.LocalTeamId && placement.EntityId is >= 69 and <= 76).EntityId);
    Equal(69, grayDefinition.Placements.Single(placement => placement.Team == 0 && placement.EntityId is >= 69 and <= 76).EntityId);

    Equal(true, twoRaceMap.TryCreateLaunch(0, configured with { CommanderRank = 3 }, out var humanLaunch));
    var humanDefinition = humanLaunch.ApplyTo(twoRaceMap.Definition);
    Equal(72, humanDefinition.Placements.Single(placement => placement.Team == humanLaunch.LocalTeamId && placement.EntityId is >= 69 and <= 76).EntityId);

    var eightPlayerMap = catalog.Scenarios.Single(scenario => scenario.Stem == "d8play01");
    Equal(true, eightPlayerMap.TryCreateLaunch(1, out var eightPlayerGrayLaunch));
    var eightPlayerGrayDefinition = eightPlayerGrayLaunch.ApplyTo(eightPlayerMap.Definition);
    Equal(8, eightPlayerGrayDefinition.Placements.Single(placement => placement.Team == eightPlayerGrayLaunch.LocalTeamId && placement.EntityId is 0 or 8).EntityId);
    Equal(73, eightPlayerGrayDefinition.Placements.Single(placement => placement.Team == eightPlayerGrayLaunch.LocalTeamId && placement.EntityId is >= 69 and <= 76).EntityId);
});

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

        if (entityCount == 0) throw new InvalidOperationException("Installed scenarios seeded no entities.");
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

    Check("single-player War maps load controllable local units", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
        var footprints = BuildingFootprintCatalog.Load(install.ExecutablePath);
        var files = Directory.GetFiles(install.DataFile("scenario", "mplayer"), "*.scn")
            .Where(file => File.Exists(Path.ChangeExtension(file, ".map")) && File.Exists(Path.ChangeExtension(file, ".pth")))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) throw new InvalidOperationException("No complete mplayer scenario triplets found.");
        var controllableActors = 0;
        foreach (var file in files)
        {
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Load(file), catalog, path, footprints);
            var localActors = simulation.Actors.Count(actor => actor.Seed.Team == 0 && actor.Definition.MovementSpeed > 0);
            if (localActors == 0)
                throw new InvalidDataException($"{Path.GetFileName(file)} has no mobile team-0 unit for local control.");
            controllableActors += localActors;
        }
        Console.WriteLine($"  single-player War: {files.Length} maps / {controllableActors} local mobile actors");
    });

    Check("single-player War rosters expose controllable Human and Gray teams", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
        var footprints = BuildingFootprintCatalog.Load(install.ExecutablePath);
        var available = new int[2];
        foreach (var file in Directory.GetFiles(install.DataFile("scenario", "mplayer"), "*.scn"))
        {
            if (!File.Exists(Path.ChangeExtension(file, ".map")) || !File.Exists(Path.ChangeExtension(file, ".pth"))) continue;
            var scenario = ScenarioDefinition.Load(file);
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            var simulation = ScenarioSimulation.Create(scenario, catalog, path, footprints);
            for (var faction = 0; faction <= 1; faction++)
            {
                var team = scenario.EnabledTeamForRace(faction);
                if (team is null) continue;
                if (!simulation.Actors.Any(actor => actor.Seed.Team == team.TeamId && actor.Definition.MovementSpeed > 0))
                    throw new InvalidDataException($"{Path.GetFileName(file)} {faction} team {team.TeamId} has no mobile unit.");
                available[faction]++;
            }
        }
        if (available[0] == 0 || available[1] == 0)
            throw new InvalidOperationException("Installed War maps do not expose both playable factions.");
        Console.WriteLine($"  selectable War rosters: Human {available[0]} / Gray {available[1]}");
    });

Check("single-player War maps complete a local movement order", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
        var footprints = BuildingFootprintCatalog.Load(install.ExecutablePath);
        var files = Directory.GetFiles(install.DataFile("scenario", "mplayer"), "*.scn")
            .Where(file => File.Exists(Path.ChangeExtension(file, ".map")) && File.Exists(Path.ChangeExtension(file, ".pth")))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var movedMaps = 0;
        foreach (var file in files)
        {
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Load(file), catalog, path, footprints);
            var finder = new DiagnosticLocalPathfinder(path, simulation.GroundOccupancy, simulation.AlternateOccupancy);
            SimulatedActor? actor = null;
            CellCoordinate? target = null;
            foreach (var candidateActor in simulation.Actors
                         .Where(candidate => candidate.Seed.Team == 0 && candidate.Definition.MovementSpeed > 0)
                         .OrderBy(candidate => candidate.Seed.InstanceId))
            {
                for (var radius = 1; radius <= 4 && target is null; radius++)
                for (var z = -radius; z <= radius && target is null; z++)
                for (var x = -radius; x <= radius; x++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius) continue;
                    var candidate = new CellCoordinate(candidateActor.Movement.OccupiedCell.X + x, candidateActor.Movement.OccupiedCell.Z + z);
                    if (finder.Find(candidateActor.Movement.OccupiedCell, candidate, candidateActor.Definition.MovementClass, candidateActor.Seed.InstanceId).Steps.Count == 0) continue;
                    actor = candidateActor;
                    target = candidate;
                    break;
                }
            }
            if (actor is null || target is null)
                throw new InvalidDataException($"{Path.GetFileName(file)} has no mobile team-0 unit with a legal nearby movement target.");
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(actor.Seed.InstanceId, target.Value))]);
            for (var tick = 0; tick < 1_000 && actor.MoveOrder is not null; tick++) simulation.Step([]);
            if (actor.Movement.OccupiedCell != target.Value || actor.MoveOrder is not null)
                throw new InvalidDataException($"{Path.GetFileName(file)} local unit {actor.Seed.InstanceId} did not complete its movement order.");
            movedMaps++;
        }
        Equal(files.Length, movedMaps);
    Console.WriteLine($"  single-player War movement: {movedMaps} maps completed one local order");
});

Check("faction-selected War rosters complete a local movement order", () =>
{
    var install = GameInstallation.Open(dataPath);
    var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
    var footprints = BuildingFootprintCatalog.Load(install.ExecutablePath);
    var warCatalog = SinglePlayerWarCatalog.Load(install);
    var movedRosters = 0;
    for (var faction = 0; faction <= 1; faction++)
    foreach (var choice in warCatalog.ForRace(faction))
    {
        var file = install.DataFile("scenario", "mplayer", $"{choice.Stem}.scn");
        var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
        var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
        var team = choice.EnabledTeamForRace(faction) ?? throw new InvalidDataException($"{choice.Stem} lost its {faction} team.");
        if (!choice.TryCreateLaunch(faction, out var launch))
            throw new InvalidDataException($"{choice.Stem} cannot create its {faction} launch.");
        var simulation = ScenarioSimulation.Create(launch.ApplyTo(choice.Definition), catalog, path, footprints);
        Equal(Math.Max(0, team.StartingResource ?? 0), simulation.ResourceForTeam(team.TeamId));
        var commanders = simulation.Actors
            .Where(actor => actor.Seed.Team == team.TeamId && actor.Seed.EntityId is >= 69 and <= 76)
            .ToArray();
        if (commanders.Any(actor => actor.Definition.Faction != faction))
            throw new InvalidDataException($"{choice.Stem} {faction} launch retained an opposing-faction commander.");
        var localMobileActors = simulation.Actors
            .Where(actor => actor.Seed.Team == team.TeamId && actor.Definition.MovementSpeed > 0)
            .ToArray();
        if (localMobileActors.Any(actor => actor.Definition.Faction != faction))
            throw new InvalidDataException($"{choice.Stem} {faction} launch exposes an opposing-faction local mobile actor.");
        var finder = new DiagnosticLocalPathfinder(path, simulation.GroundOccupancy, simulation.AlternateOccupancy);
        SimulatedActor? actor = null;
        CellCoordinate? target = null;
        foreach (var candidateActor in simulation.Actors
                     .Where(candidate => candidate.Seed.Team == team.TeamId && candidate.Definition.MovementSpeed > 0)
                     .OrderBy(candidate => candidate.Seed.InstanceId))
        {
            for (var radius = 1; radius <= 4 && target is null; radius++)
            for (var z = -radius; z <= radius && target is null; z++)
            for (var x = -radius; x <= radius; x++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius) continue;
                var candidate = new CellCoordinate(candidateActor.Movement.OccupiedCell.X + x, candidateActor.Movement.OccupiedCell.Z + z);
                if (finder.Find(candidateActor.Movement.OccupiedCell, candidate, candidateActor.Definition.MovementClass, candidateActor.Seed.InstanceId).Steps.Count == 0) continue;
                actor = candidateActor;
                target = candidate;
                break;
            }
        }

        if (actor is null || target is null)
            throw new InvalidDataException($"{choice.Stem} {faction} team {team.TeamId} has no legal local movement target.");
        simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(actor.Seed.InstanceId, target.Value))]);
        for (var tick = 0; tick < 1_000 && actor.MoveOrder is not null; tick++) simulation.Step([]);
        if (actor.Movement.OccupiedCell != target.Value || actor.MoveOrder is not null)
            throw new InvalidDataException($"{choice.Stem} {faction} team {team.TeamId} did not complete its local move.");
        movedRosters++;
    }

    Equal(84, movedRosters);
    Console.WriteLine($"  faction-selected War movement: {movedRosters} rosters completed one local order");
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

        if (files.Length == 0 || frameCount == 0)
            throw new InvalidOperationException("Installed SPR corpus is empty.");
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
        var cyborgArticle = EncyclopediaArticle.Load(install.RootPath, "encyclo/cybo");
        Equal("REMOTE ASSASSINATION CYBORG", cyborgArticle.Lines[0].Text);
        Equal(1, cyborgArticle.Lines[0].PaletteIndex);
        Equal(true, cyborgArticle.Lines.Any(line => line.Text.Contains("NAPALM", StringComparison.Ordinal)));
        Equal(true, File.Exists(Path.Combine(install.RootPath, "encyclo", "cybo.wav")));
        var parsed = EncyclopediaArticle.Parse("~1TITLE\n~0NAME: ~4VALUE\n");
        Equal("TITLE", parsed.Lines[0].Text);
        Equal(1, parsed.Lines[0].PaletteIndex);
        Equal("NAME: VALUE", parsed.Lines[1].Text);
        Equal(0, parsed.Lines[1].PaletteIndex);
        Equal(2, parsed.Lines[1].Segments.Count);
        Equal("NAME: ", parsed.Lines[1].Segments[0].Text);
        Equal(0, parsed.Lines[1].Segments[0].PaletteIndex);
        Equal("VALUE", parsed.Lines[1].Segments[1].Text);
        Equal(4, parsed.Lines[1].Segments[1].PaletteIndex);
        var layout = File.ReadAllText(install.DataFile("intrface", "encycloe"));
        Equal(true, layout.Contains("gadget  22      0       343     220", StringComparison.Ordinal));
        Equal(true, layout.Contains("gadget  23      0       445     220", StringComparison.Ordinal));
        Equal(true, layout.Contains("gadget  24      0       511     220", StringComparison.Ordinal));
        Equal(true, layout.Contains("gadget  25      0       601     220", StringComparison.Ordinal));
        Equal(true, EncyclopediaUnitIdentityCatalog.TryGetEntityId(catalog.Categories[1].Entries.Single(entry => entry.ResourceStem.EndsWith("cybo", StringComparison.Ordinal)), out var sargeId));
        Equal(4, sargeId);
        Equal(true, EncyclopediaUnitIdentityCatalog.TryGetEntityId(catalog.Categories[0].Entries.Single(entry => entry.ResourceStem.EndsWith("psych", StringComparison.Ordinal)), out var psyId));
        Equal(12, psyId);
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

static int[] ValuesWith(int movementSpeed)
{
    var values = new int[32];
    values[2] = movementSpeed;
    return values;
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
