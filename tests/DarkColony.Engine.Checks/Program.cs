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
using System.Buffers.Binary;

if (DeterminismCli.TryRun(args, out var determinismExitCode)) return determinismExitCode;

var failures = new List<string>();
// DARKCOLONY_CHECK_FILTER runs only checks whose name contains the given text.
var checkFilter = Environment.GetEnvironmentVariable("DARKCOLONY_CHECK_FILTER");

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

Check("blocked allied actor receives a yield notification before jitter wait", () =>
{
    var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n1 3 0 0 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 6];
    bytes[1 * 256 + 1] = 1;
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    // Cursor zero increments before each read: entries one and two select
    // X +0 (1 % 3 - 1) and Z -1 (0 % 3 - 1).
    var stream = new uint[NativeRandomTable.Length];
    stream[1] = 1;
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 6, 1),
        randomTable: NativeRandomTable.FromValues(stream));
    // The blocker exists as an actor but only enters the occupancy grid after
    // the mover has committed its initial packed segment, matching a dynamic
    // playback collision rather than an initial route obstruction.
    simulation.GroundOccupancy.Release(2);
    simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(5, 0)))]);
    simulation.GroundOccupancy.ReplaceClaims(2, [new CellCoordinate(3, 0)]);
    var mover = simulation.Actor(1)!;
    var blocker = simulation.Actor(2)!;
    for (var tick = 0; tick < 80 && blocker.YieldNotificationDirection is null; tick++) simulation.Step([]);
    Equal(PathDirection.East, blocker.YieldNotificationDirection ?? throw new InvalidOperationException("Yield notification missing."));
    Equal(true, mover.MoveOrder is not null);
    // The injected X +0 / Z -1 jitter is clamped by the one-row map, leaving
    // the active target unchanged.
    Equal(new CellCoordinate(5, 0), mover.MoveOrder!.Target);
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

// Shared fixtures for the automatic target selection checks below.
const string AcquisitionHeader = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n";
const string AcquisitionEntities = "5\n" +
    "GUARD 0 255 25 8 8 1 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
    "INTRUDER 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
    "SHOOTER 0 255 25 8 8 2 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
    "NEARSIGHT 0 255 25 1 1 1 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
    "PROP 0 1 1 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1\n";
const string AcquisitionWeapons = "2\n1 BULLET 0 0 1 10 90 3 0 0 0 0 0\n2 BULLET 0 0 1 10 90 8 0 0 0 0 0\n";

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

Check("notified idle blockers step aside, never back toward the mover", () =>
{
    // A mover heading east notified the idle unarmed blocker at (5,5). The
    // sideways order (0x479288) tries south first, then north once south is
    // taken.
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    ScenarioSimulation Notified(string extra)
    {
        var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "5 5 1 0 100 0\n" + extra),
            catalog, OpenPath(12, 12), targetRings: EuclideanRings());
        simulation.Actor(1)!.YieldNotificationDirection = PathDirection.East;
        simulation.Step([]);
        return simulation;
    }
    var open = Notified("");
    var blocker = open.Actor(1)!;
    Equal(new CellCoordinate(5, 6), blocker.MoveOrder!.Target);
    Equal(true, blocker.YieldNotificationDirection is null);
    for (var tick = 0; tick < 40 && blocker.MoveOrder is not null; tick++) open.Step([]);
    Equal(new CellCoordinate(5, 6), blocker.Movement.OccupiedCell);

    var southTaken = Notified("5 6 4 0 100 0\n");
    Equal(new CellCoordinate(5, 4), southTaken.Actor(1)!.MoveOrder!.Target);
});

Check("armed blockers with a hostile in range yield straight ahead instead of attacking", () =>
{
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "5 5 0 0 1000 0\n8 5 1 1 100 0\n"),
        catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
    var blocker = simulation.Actor(1)!;
    blocker.YieldNotificationDirection = PathDirection.East;
    simulation.Step([]);
    // 0x412A50 tries the notified direction first (0x4792A4).
    Equal(new CellCoordinate(6, 5), blocker.MoveOrder!.Target);
    Equal(true, blocker.AttackTargetInstanceId is null);
});

Check("a hostile mine is a target only after a detector of the scanner's team reveals it", () =>
{
    // GUARD (team 0) has the mine (team 1, value 15) within weapon range. The
    // selector skips it (0x435829) until a value-16 detector of team 0 sees
    // its cell at a visibility refresh (0x44A6D4), which sets +0xCA bit 0.
    var catalog = EntityCatalog.Parse("3\n" +
        "GUARD 0 255 25 8 8 1 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
        // Speed 1 only so the SCN seed enters the mine grid (speed-0 seeds without a footprint enter none).
        "MINE 0 0 1 1 1 -1 -1 -1 1 1 0 800 0 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
        "DETECTOR 0 255 25 4 4 -1 -1 -1 1 1 0 100 0 0 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
    ScenarioSimulation Run(string extra)
    {
        var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "5 5 0 0 1000 0\n7 5 1 1 800 0\n" + extra),
            catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
        for (var tick = 0; tick < 20; tick++) simulation.Step([]);
        return simulation;
    }
    var unrevealed = Run("");
    Equal(0, unrevealed.Actor(2)!.RevealedTeamMask);
    Equal(true, unrevealed.Actor(1)!.AttackTargetInstanceId is null);
    var revealed = Run("6 3 2 0 100 0\n");
    Equal(1, revealed.Actor(2)!.RevealedTeamMask);
    Equal(2, revealed.Actor(1)!.AttackTargetInstanceId ?? -1);
});

Check("a fully blocked yield shuffles its order with the shared random stream", () =>
{
    // Every sideways candidate around (5,5) is held, so 0x4126A8 fails and
    // 0x412820 draws: entry 1 = 3 swaps preference 3 (offset -1) to the
    // front, which turns east into north-east (6,4). Held live cells are
    // accepted, so the first step there is blocked with no free cell left,
    // and 0x4155D5 jitters the target by entries 2 and 3 (0 % 3 - 1 each).
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    var placements = "5 5 1 0 100 0\n" + string.Concat(
        new[] { (5, 6), (5, 4), (6, 6), (6, 4), (4, 6), (4, 4), (6, 5) }.Select(cell => $"{cell.Item1} {cell.Item2} 4 0 100 0\n"));
    var stream = new uint[NativeRandomTable.Length];
    stream[1] = 3;
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + placements), catalog, OpenPath(12, 12),
        randomTable: NativeRandomTable.FromValues(stream), targetRings: EuclideanRings());
    var blocker = simulation.Actor(1)!;
    blocker.YieldNotificationDirection = PathDirection.East;
    simulation.Step([]);
    Equal(new CellCoordinate(5, 3), blocker.MoveOrder!.Target);
});

Check("a move onto an occupied cell routes there and settles beside it", () =>
{
    // The native search seeds the target, so an occupied destination still
    // has a route; the last step is blocked with no free cell left and the
    // target is jittered (entries 1 and 2: 0 % 3 - 1 = -1 each).
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 5 1 0 100 0\n6 5 4 0 100 0\n"),
        catalog, OpenPath(12, 12), randomTable: NativeRandomTable.FromValues(new uint[NativeRandomTable.Length]),
        targetRings: EuclideanRings());
    var mover = simulation.Actor(1)!;
    simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new MoveIntent(1, new CellCoordinate(6, 5)))]);
    Equal(true, mover.Playback is not null);
    for (var tick = 0; tick < 200 && mover.MoveOrder is not null; tick++) simulation.Step([]);
    Equal(true, mover.MoveOrder is null);
    Equal(new CellCoordinate(5, 4), mover.Movement.OccupiedCell);
});

Check("the idle record survives the moves it pushes itself", () =>
{
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "5 5 1 0 100 0\n"),
        catalog, OpenPath(12, 12), targetRings: EuclideanRings());
    var blocker = simulation.Actor(1)!;
    simulation.Step([]);
    blocker.IdleMissCount = 2;
    blocker.YieldNotificationDirection = PathDirection.East;
    simulation.Step([]);
    for (var tick = 0; tick < 40 && blocker.MoveOrder is not null; tick++) simulation.Step([]);
    simulation.Step([]);
    Equal(true, blocker.IdleCommandActive);
    Equal(2, blocker.IdleMissCount);

    // A player move replaces the command stack, so the record restarts.
    simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new MoveIntent(1, new CellCoordinate(5, 8)))]);
    for (var tick = 0; tick < 60 && blocker.MoveOrder is not null; tick++) simulation.Step([]);
    simulation.Step([]);
    Equal(0, blocker.IdleMissCount);
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
    // alien08/human09 "(b(1,3)&&==0)": the stray && takes its left operand
    // from the enclosing chain, so the whole conjunction collapses to 0.
    Equal(0, Run("((1==1)&&(b(1,3)&&==0))"));
});

Check("idle armed actors acquire a visible hostile in weapon range", () =>
{
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 1000 0\n4 2 1 1 100 0\n"),
        catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
    simulation.Step([]);
    Equal(2, simulation.Actor(1)!.AttackTargetInstanceId!.Value);
    var acquisition = simulation.LastIdleAcquisitions.Single();
    Equal(new IdleAcquisitionEvent(1, 2, Approach: false), acquisition);
});

Check("idle selection keeps ring order for ordinary weapons and scores area weapons", () =>
{
    // Ring 1 lists (+1,0) before (-1,0). The unarmed intruder sits first and
    // the armed guard second.
    var rings = NativeTargetRings.FromRings([[new CellCoordinate(0, 0)], [new CellCoordinate(1, 0), new CellCoordinate(-1, 0)]]);
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    const string placements = "5 5 0 0 1000 0\n6 5 1 1 100 0\n4 5 0 1 1000 0\n";
    var ordinary = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + placements), catalog, OpenPath(12, 12),
        weaponCatalog: WeaponCatalog.Parse(AcquisitionWeapons), targetRings: rings);
    ordinary.Step([]);
    // The native arithmetic zeroes every ordinary-weapon score, so the first
    // candidate in ring order wins even though the other one is armed.
    Equal(2, ordinary.Actor(1)!.AttackTargetInstanceId!.Value);

    var area = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + placements), catalog, OpenPath(12, 12),
        weaponCatalog: WeaponCatalog.Parse("2\n1 BULLET 0 0 1 10 90 3 7 0 0 0 0\n2 BULLET 0 0 1 10 90 8 0 0 0 0 0\n"), targetRings: rings);
    area.Step([]);
    // Area weapons score armed targets (150) above unarmed ones (50).
    Equal(3, area.Actor(1)!.AttackTargetInstanceId!.Value);
});

Check("idle second scan radius depends on player kind and damage", () =>
{
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
    // The intruder is six cells away: outside weapon range 3 and the calm
    // human radius 4, inside the computer radius 16.
    var human = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 1000 0\n8 2 1 1 100 0\n"),
        catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
    human.Step([]);
    Equal(true, human.Actor(1)!.AttackTargetInstanceId is null && human.Actor(1)!.MoveOrder is null);
    Equal(ScenarioSimulation.NativeIdleShortWaitTicks, human.Actor(1)!.IdleWaitTicks);
    Equal(1, human.Actor(1)!.IdleMissCount);

    const string computerTeams = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n4\n%AI\nTEAM 1 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n";
    var computer =ScenarioSimulation.Create(ScenarioDefinition.Parse(computerTeams + "2 2 0 0 1000 0\n8 2 1 1 100 0\n"),
        catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
    computer.Step([]);
    var approach = computer.LastIdleAcquisitions.Single();
    Equal(new IdleAcquisitionEvent(1, 2, Approach: true), approach);
    Equal(true, computer.Actor(1)!.MoveOrder!.StopOnContact);
    // The closest free cell inside weapon range of the hostile at (8,2).
    Equal(new CellCoordinate(6, 2), computer.Actor(1)!.MoveOrder!.Target);
});

Check("damaged human units widen their idle scan and close on the shooter", () =>
{
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
    // A long-range shooter six cells away hits the idle guard; the guard
    // cannot see past radius 4 until it has been damaged, then looks 9 out.
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 1000 0\n8 2 2 1 1000 0\n"),
        catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(2, 1))]);
    Equal(true, simulation.Actor(1)!.MoveOrder is null);
    IdleAcquisitionEvent? approach = null;
    for (var tick = 0; tick < 120 && approach is null; tick++)
    {
        simulation.Step([]);
        approach = simulation.LastIdleAcquisitions.SingleOrDefault(acquisition => acquisition.SourceActorInstanceId == 1);
    }
    Equal(true, simulation.Actor(1)!.Health < 1000);
    Equal(new IdleAcquisitionEvent(1, 2, Approach: true), approach ?? throw new InvalidOperationException("The damaged guard never closed on its attacker."));
});

Check("idle scans wait 15 ticks for three misses and then 45", () =>
{
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 1000 0\n"),
        catalog, OpenPath(8, 8), weaponCatalog: WeaponCatalog.Parse(AcquisitionWeapons), targetRings: EuclideanRings());
    var guard = simulation.Actors.Single();
    var scans = new List<int>();
    for (var tick = 1; tick <= 140; tick++)
    {
        var before = guard.IdleWaiting;
        simulation.Step([]);
        if (!before && guard.IdleWaiting) scans.Add(tick);
    }
    // Each miss pushes a wait (0x412274) and returns 0. The wait counts its
    // word down to zero and pops one update later (0x4122C8), so a 15 wait
    // gives a 17-tick period until the third miss, then 45 gives 47.
    Equal(new[] { 1, 18, 35, 52, 99 }, scans.ToArray());
    Equal(3, guard.IdleMissCount);
});

Check("damage ends an idle wait and the idle record scans in the same update", () =>
{
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 1000 0\n"),
        catalog, OpenPath(8, 8), weaponCatalog: WeaponCatalog.Parse(AcquisitionWeapons), targetRings: EuclideanRings());
    var guard = simulation.Actors.Single();
    for (var tick = 0; tick < 5; tick++) simulation.Step([]);
    Equal(true, guard.IdleWaiting);
    Equal(ScenarioSimulation.NativeIdleShortWaitTicks - 4, guard.IdleWaitTicks);
    // 0x4122C8 compares the health stored at the push: a change pops the wait
    // and returns 1, so the idle record scans (and misses) in this update.
    guard.Health -= 10;
    simulation.Step([]);
    Equal(true, guard.IdleWaiting);
    Equal(ScenarioSimulation.NativeIdleShortWaitTicks, guard.IdleWaitTicks);
    Equal(guard.Health, guard.IdleWaitHealth);
});

Check("an idle fidget turns to its random bearing after the wait before scanning again", () =>
{
    // Turn rate 2: the fidget record (type 4, 0x412358) steps the facing by 2
    // per update and pops on arrival, letting the idle record scan in the same
    // update.
    var catalog = EntityCatalog.Parse("1\nSLOW 0 2 25 8 8 1 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 1000 0\n"),
        catalog, OpenPath(8, 8), weaponCatalog: WeaponCatalog.Parse(AcquisitionWeapons), targetRings: EuclideanRings());
    var guard = simulation.Actors.Single();
    for (var tick = 0; tick < 5000 && guard.IdleFidgetFacing is null; tick++) simulation.Step([]);
    var bearing = guard.IdleFidgetFacing ?? throw new InvalidOperationException("No fidget was drawn in 5000 ticks.");
    for (var tick = 0; tick < 100 && guard.IdleWaiting; tick++) simulation.Step([]);
    var difference = (bearing - guard.Facing.Current + 256) % 256;
    var turns = Math.Max(1, (Math.Min(difference, 256 - difference) + 1) / 2);
    var updates = 0;
    while (!guard.IdleWaiting && updates < 200)
    {
        simulation.Step([]);
        updates++;
    }
    Equal(turns, updates);
    Equal(bearing, guard.Facing.Current);
    Equal(false, guard.IdleFidgetFacing.HasValue);
});

Check("idle selection skips critters, untargetable props, and unseen cells", () =>
{
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
    // Team 9 critter and an untargetable prop next to the guard, plus a
    // hostile in weapon range but outside the guard's sight.
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 3 0 1000 0\n3 2 1 9 100 0\n2 3 4 1 100 0\n5 2 1 1 100 0\n"),
        catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
    simulation.Step([]);
    Equal(true, simulation.Actor(1)!.AttackTargetInstanceId is null);
    Equal(0, simulation.LastIdleAcquisitions.Count);
});

Check("a stop-on-contact approach ends once a hostile is in weapon range", () =>
{
    var catalog = EntityCatalog.Parse(AcquisitionEntities);
    // Both teams share race 0 so the loader's race swap leaves the fixtures alone.
    const string computerTeams = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n4\n%AI\nTEAM 1 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n";
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(computerTeams + "1 2 0 0 1000 0\n8 2 1 1 100 0\n"),
        catalog, OpenPath(14, 6), weaponCatalog: WeaponCatalog.Parse(AcquisitionWeapons), targetRings: EuclideanRings());
    var guard = simulation.Actor(1)!;
    var start = guard.Movement.VisualPosition;
    simulation.Step([]);
    Equal(true, guard.MoveOrder!.StopOnContact);
    // The idle handler returns 0 after pushing the move (0x414C20): no step
    // yet. The next update reserves the first cell.
    Equal(start, guard.Movement.VisualPosition);
    Equal(guard.Movement.OccupiedCell, guard.Movement.ReservedDestination);
    simulation.Step([]);
    Equal(true, guard.Movement.ReservedDestination != guard.Movement.OccupiedCell);
    for (var tick = 0; tick < 300 && guard.AttackTargetInstanceId is null; tick++) simulation.Step([]);
    Equal(2, guard.AttackTargetInstanceId ?? throw new InvalidOperationException("The approach never turned into an attack."));
    // Contact came at ring 3 (distance 3, at X = 5), one cell before the
    // (6,2) approach destination; the idle scan then took the target.
    Equal(new CellCoordinate(5, 2), guard.Movement.OccupiedCell);
});

Check("replacement move cancels an explicit attack target", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 4 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n1 1 1 1 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 12];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 3), weaponCatalog: weapons);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    Equal(2, simulation.Actor(1)!.AttackTargetInstanceId!.Value);
    simulation.Step([new ScheduledWorldCommand(2, 0, new MoveIntent(1, new CellCoordinate(3, 0)))]);
    Equal(true, simulation.Actor(1)!.AttackTargetInstanceId is null);
});

Check("a direct attack lets the in-flight step finish, then drops the move", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 1 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 1 4 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n1 1 0 2 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 5 * 3];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 3), weaponCatalog: weapons);
    simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(4, 0)))]);
    var oldReserved = simulation.Actor(1)!.Movement.ReservedDestination;
    Equal(true, simulation.GroundOccupancy.IsOccupied(oldReserved));
    simulation.Step([new ScheduledWorldCommand(2, 0, new AttackIntent(1, 2))]);
    // The step command (type 5, 0x4125BC) ignores the pending order.
    var attacker = simulation.Actor(1)!;
    Equal(true, simulation.GroundOccupancy.IsOccupied(oldReserved));
    Equal(true, attacker.FinishingStep is not null);
    Equal(2, attacker.AttackTargetInstanceId!.Value);
    for (var tick = 0; tick < 100 && attacker.FinishingStep is not null; tick++) simulation.Step([]);
    Equal(oldReserved, attacker.Movement.OccupiedCell);
    Equal(true, attacker.MoveOrder is null);
});

Check("an attack-move lets the in-flight step finish first", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 1 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 1 4 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n1 1 0 2 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 5 * 3];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 3), weaponCatalog: weapons);
    simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(4, 0)))]);
    var oldReserved = simulation.Actor(1)!.Movement.ReservedDestination;
    simulation.Step([new ScheduledWorldCommand(2, 0, new AttackMoveIntent(1, new CellCoordinate(0, 2)))]);
    var mover = simulation.Actor(1)!;
    Equal(true, simulation.GroundOccupancy.IsOccupied(oldReserved));
    Equal(new CellCoordinate(0, 2), mover.AttackMoveDestination!.Value);
    for (var tick = 0; tick < 100 && mover.FinishingStep is not null; tick++) simulation.Step([]);
    Equal(oldReserved, mover.Movement.OccupiedCell);
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
    // Stat 8 counts every projectile the player launches (0x44178F).
    Equal(3, simulation.PlayerStatistic(simulation.Actor(1)!.Seed.Team, 8));
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
    // Resolve impact damage against the intervening actor's armor class.
    ((int[])catalog[2].Values)[10] = 1;
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 20 25 90 5 0 0 0 0 0\n");
    var matrix = DamageMatrix.Parse("10\n9\n25 12 25 18 25 90 5 50 0 5\n100 25 0 25 50 100 10 50 0 10\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n");
    const string source = "t\ni\nd\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 1 0 0 100 0\n3 1 1 1 100 0\n1 1 2 1 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 15]; bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 3), weaponCatalog: weapons, damageMatrix: matrix);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    for (var tick = 0; tick < 20 && simulation.LastProjectileImpacts.Count == 0; tick++) simulation.Step([]);
    Equal(3, simulation.LastProjectileImpacts.Single().TargetActorInstanceId);
    Equal(97, simulation.Actor(3)!.Health);
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
    // Relations among players follow the mutual alliance bits, recomputed at
    // the start of every update (0x4198D3).
    simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(opposingTeams), catalog, PathRegionMap.Parse(bytes, 4, 3), weaponCatalog: weapons);
    simulation.SetAllianceBit(0, 1, true);
    simulation.SetAllianceBit(1, 0, true);
    simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
    Equal(AttackOrderOutcome.NonHostile, simulation.LastAttackOrders.Single().Outcome);
    // One side withdrawing ends the alliance.
    simulation.SetAllianceBit(1, 0, false);
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

Check("target destruction cancels another attacker's reserved pursuit", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 200 90 2 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n6 1 1 1 100 0\n5 1 0 0 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3), weaponCatalog: weapons);

    simulation.Step([
        new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2)),
        new ScheduledWorldCommand(1, 1, new AttackIntent(3, 2)),
    ]);
    for (var tick = 0; tick < 120 && !simulation.Actor(2)!.IsDestroyed; tick++) simulation.Step([]);

    if (!simulation.Actor(2)!.IsDestroyed)
        throw new InvalidOperationException($"Killer did not destroy target: health={simulation.Actor(2)!.Health}, fires={simulation.LastWeaponFires.Count}, projectileCount={simulation.Projectiles.Count}, killerTarget={simulation.Actor(3)!.AttackTargetInstanceId}");
    var pursuer = simulation.Actor(1)!;
    Equal(false, pursuer.AttackTargetInstanceId.HasValue);
    Equal(true, pursuer.Playback is null && pursuer.MoveOrder is null);
});

Check("attack-move resumes its destination when another attacker destroys its target", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 200 90 2 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n6 1 1 1 100 0\n5 1 0 0 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3), weaponCatalog: weapons);

    simulation.Step([
        new ScheduledWorldCommand(1, 0, new AttackMoveIntent(1, new CellCoordinate(9, 1))),
        new ScheduledWorldCommand(1, 1, new AttackIntent(3, 2)),
    ]);
    for (var tick = 0; tick < 120 && !simulation.Actor(2)!.IsDestroyed; tick++) simulation.Step([]);
    Equal(true, simulation.Actor(2)!.IsDestroyed);
    Equal(new CellCoordinate(9, 1), simulation.Actor(1)!.AttackMoveDestination!.Value);
    for (var tick = 0; tick < 240 && simulation.Actor(1)!.AttackMoveDestination is not null; tick++) simulation.Step([]);
    Equal(new CellCoordinate(9, 1), simulation.Actor(1)!.Movement.OccupiedCell);
    Equal(false, simulation.Actor(1)!.AttackMoveDestination.HasValue);
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

Check("alliance and vision bits count only when both players set them", () =>
{
    var catalog = EntityCatalog.Parse("1\nSCOUT 0 255 25 2 2 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n8 1 0 1 100 0\n";
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
});

Check("attack-move excludes a team marked cooperative in the relation matrix", () =>
{
    var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 2 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n2 1 1 1 100 0\n";
    var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3), weaponCatalog: weapons);
    simulation.SetAllianceBit(0, 1, true);
    simulation.SetAllianceBit(1, 0, true);

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

Check("Exploiter vent deployment accelerates P7 and sight blends day and night", () =>
{
    var catalog = EntityCatalog.Parse("2\nEXPL 0 255 25 2 9 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 8 5 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n1 1 40 0 100\n";
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
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n1 1 40 0 4\n";
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

Check("ordinary movement onto a free vent automatically begins harvester deployment", () =>
{
    var catalog = EntityCatalog.Parse("2\nEXPL 0 255 25 2 9 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 8 5 -1 -1 -1 1 1 5 10 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 0 0 0 100 0\n3 3 40 0 100\n";
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

Check("a stealing stance forms after state 13 and retracts when it finds no victim", () =>
{
    var catalog = EntityCatalog.Parse("2\nSARG 0 10 45 10 10 13 14 14 125 150 4 800 0 31 0 1 0 0 0 0 3 96 0 1 4 0 0 5 129 50 12 0\nSARGSTL 0 10 0 10 10 -1 -1 -1 125 150 4 800 0 1 0 1 0 0 0 0 0 0 0 0 0 0 0 5 0 0 78 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 100 0\n";
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

Check("vents pay their own SCN rate per pulse, and a zero-rate vent pays nothing", () =>
{
    // The SCN vent record "x z 40 rate reservoir": the loader stores the
    // team column as the vent's +0x32 rate (x the session option, 256 = x1).
    var catalog = EntityCatalog.Parse("2\nEXPL 0 255 25 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    int Earned(int rate)
    {
        var source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n2 1 0 0 100 0\n" +
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

Check("a steal stance links the first visible deployed harvester and halves its pulses", () =>
{
    // Both forms see 10 cells (the largest sight tree), so both stances see
    // the harvester at (1,1) from 10 and 9 cells away.
    var catalog = EntityCatalog.Parse("4\nEXPL 0 255 25 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nEDPLY 0 0 0 2 2 -1 -1 -1 1 1 5 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nSARG 0 10 45 10 10 -1 -1 -1 1 1 4 800 0 31 0 1 0 0 0 0 3 96 0 1 4 0 0 5 129 50 3 0\nSARGSTL 0 10 0 10 10 -1 -1 -1 1 1 4 800 0 1 0 1 0 0 0 0 0 0 0 0 0 0 0 5 0 0 2 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n" +
        "11 1 2 0 100 0\n10 1 2 0 100 0\n1 1 0 1 100 0\n1 1 40 0 5000\n";
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

Check("queued duplicate waypoint does not discard later destinations", () =>
{
    var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n";
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
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 100 0\n";
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

Check("a blocked step whose repair fails waits four updates, then retries its kept steps", () =>
{
    // A one-cell corridor: the step into (2,0) is blocked, the first free
    // remaining cell (3,0) has no route around the blocker, so 0x415458 waits
    // (type 3, counter 4) with the move record still holding its steps.
    var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
    const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 0 0 0 100 0\n";
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
        Equal(18, matrix.CalculateNativeDamage(100, 0, 0, reduceToThreeQuarters: true));
        Equal(19, matrix.CalculateNativeDamage(104, 0, 0, reduceToThreeQuarters: true));
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
    });

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
    });

    Check("installed troop build timings come from the anim.dat FIN order", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
        var timings = TroopBuildTimings.Load(entities, install);
        int Ticks(string code) => timings.BuildTicks(entities.Entities.First(entity => entity.Code == code).Id)!.Value;
        Equal(22, Ticks("TRSC"));
        Equal(9, Ticks("EXPL"));
        Equal(37, Ticks("GRAY"));
    });

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
    });

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
    });

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
    });

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
    });

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
    });

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
            $"{viewer.X} {viewer.Z} 0 0 100 0\n";
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
    });

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
    });

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
    });

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
    });

    Check("native target rings decode whole-distance rings 0 through 16", () =>
    {
        var rings = NativeTargetRings.Load(GameInstallation.Open(dataPath).ExecutablePath).Rings;
        Equal(new[] { 1, 8, 16, 20, 24, 40, 36, 48, 56, 56, 68, 64, 80, 92, 88, 96, 4 }, rings.Select(ring => ring.Count).ToArray());
        Equal(new CellCoordinate(0, 1), rings[1][0]);
        Equal(new CellCoordinate(16, 0), rings[16][0]);
        for (var ring = 0; ring < rings.Count; ring++)
            Equal(true, rings[ring].All(offset => (int)Math.Floor(Math.Sqrt(offset.X * offset.X + offset.Z * offset.Z)) == ring));
    });

    Check("native random table reads the executable's shared stream", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var table = NativeRandomTable.Load(install.ExecutablePath);
        Equal(0x41c6u, table[0]);
        Equal(0x167eu, table[1]);
        Equal(0x2781u, table[2]);
        Equal(0x5d5eu, table[NativeRandomTable.Length - 1]);
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
            weaponCatalog: weapons, areaEffects: forcedTopLeft, randomTable: NativeRandomTable.Load(install.ExecutablePath));
        ordinary.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
        for (var tick = 0; tick < 32 && ordinary.Projectiles.Count == 0; tick++) ordinary.Step([]);
        Equal(new CellCoordinate(14, 4), ordinary.Projectiles.Single().TimedImpactCell!.Value);
        // The first native table value (index one) selected the ordinary
        // scatter cell. Fire presentation must then consume index two from the
        // same stream, rather than an independent cosmetic PRNG.
        Equal((byte)0x81, ordinary.LastWeaponFires.Single().PresentationVariantRoll);

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
            {
                var owner = simulation.GroundOccupancy.TryGetOwner(target.Value, out var ownerId) ? ownerId.ToString() : "none";
                throw new InvalidDataException(
                    $"{Path.GetFileName(file)} local unit {actor.Seed.InstanceId} did not complete its movement order " +
                    $"({actor.Movement.OccupiedCell} -> {target.Value}, target owner {owner}).");
            }
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

    Check("every installed scenario runs scripted orders without faults", () =>
    {
        const ulong smokeTicks = 300;
        var install = GameInstallation.Open(dataPath);
        var rules = SimulationRules.Load(install);
        var scenarios = DeterminismHarness.InstalledScenarios(install);
        if (scenarios.Count < 101) throw new InvalidOperationException($"Expected at least 101 complete scenarios, found {scenarios.Count}.");
        var started = System.Diagnostics.Stopwatch.StartNew();
        foreach (var scenario in scenarios)
        {
            try
            {
                DeterminismHarness.Run(install, rules, scenario, smokeTicks, [smokeTicks], digestEveryTick: false);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"{scenario}: {error.GetType().Name}: {error.Message}", error);
            }
        }
        Console.WriteLine($"  scripted smoke: {scenarios.Count} scenarios x {smokeTicks} ticks in {started.Elapsed.TotalSeconds:0.0}s");
    });

    Check("scripted scenario runs are repeatable within one process", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var rules = SimulationRules.Load(install);
        ulong[] checkpoints = [50, 150, 300];
        var first = DeterminismHarness.Run(install, rules, "mplayer/j4play01", 300, checkpoints);
        var second = DeterminismHarness.Run(install, rules, "mplayer/j4play01", 300, checkpoints);
        Equal(string.Join(' ', first.Checkpoints), string.Join(' ', second.Checkpoints));
        Equal(first.FinalDescription, second.FinalDescription);
    });

    Check("scripted scenario runs match recorded golden digests", () =>
    {
        var install = GameInstallation.Open(dataPath);
        var mismatches = DeterminismHarness.CompareGoldens(install, SimulationRules.Load(install));
        if (mismatches.Count != 0)
            throw new InvalidOperationException(
                "simulation behavior changed: " + string.Join("; ", mismatches) +
                ". If intended, rerun with --update-goldens and explain the change in the commit; " +
                "compare states with --dump-digest <scenario> <tick> <file> on both builds.");
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

// Whole-distance rings (r <= d < r + 1) shaped like dc.exe's 0x434090 table, in a synthetic
// (Z, then X) order, for checks that run without the executable.
NativeTargetRings EuclideanRings()
{
    var rings = Enumerable.Range(0, NativeTargetRings.MaximumRing + 1).Select(_ => new List<CellCoordinate>()).ToArray();
    for (var dz = -NativeTargetRings.MaximumRing; dz <= NativeTargetRings.MaximumRing; dz++)
    for (var dx = -NativeTargetRings.MaximumRing; dx <= NativeTargetRings.MaximumRing; dx++)
    {
        var ring = (int)Math.Floor(Math.Sqrt(dx * dx + dz * dz));
        if (ring <= NativeTargetRings.MaximumRing) rings[ring].Add(new CellCoordinate(dx, dz));
    }
    return NativeTargetRings.FromRings(rings);
}

PathRegionMap OpenPath(int width, int height)
{
    var bytes = new byte[PathRegionMap.RouteTableSize + width * height];
    bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
    return PathRegionMap.Parse(bytes, width, height);
}

void Check(string name, Action action)
{
    if (!string.IsNullOrEmpty(checkFilter) && !name.Contains(checkFilter, StringComparison.OrdinalIgnoreCase)) return;
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

sealed class FakeTriggerContext : ITriggerExpressionContext
{
    public int Clock => 3;
    public int NextRandom() => 0;
    public (int EntityType, int Team)? Unit => (0, 2);
    public int SlotHealth(int player, int slot) => player == 1 && slot == 3 ? 5 : 0;
    public bool CellVisible(int x, int z, int player) => false;
    public int PlayerStat(int player, int stat) => player * 10 + stat - 13;
    public int TypeStat(int player, int stat, int entityType) => player * 10 + entityType;
    public bool MineAlive(int x, int z) => false;
    public int ScriptWord(int byteOffset) => 0;
}
