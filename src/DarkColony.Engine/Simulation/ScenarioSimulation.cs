using DarkColony.Engine.Commands;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

public sealed class SimulatedActor
{
    internal SimulatedActor(WorldEntity seed, EntityDefinition definition)
    {
        Seed = seed;
        Definition = definition;
        Movement = new MovementState(seed.SpawnCell);
        Facing = new FacingState();
        Health = definition.Health;
        MaximumHealth = definition.Health;
        AbilityCharge = NativeInitialAbilityCharge;
    }

    public WorldEntity Seed { get; }
    public EntityDefinition Definition { get; }
    public MovementState Movement { get; }
    public FacingState Facing { get; }
    /// <summary>Authoritative live health; catalog health is the immutable maximum.</summary>
    public int Health { get; internal set; }
    /// <summary>
    /// Current form's health ceiling. Most actors keep their seed definition,
    /// but an in-place deployment must not retain a mobile form's stat cap.
    /// </summary>
    public int MaximumHealth { get; internal set; }
    public bool IsDestroyed => Health <= 0;
    public const int NativeInitialAbilityCharge = 0x40;
    public const int NativeMaximumAbilityCharge = 0xff;
    /// <summary>Native actor byte +0x0a, used as the BEON/ZISP heal charge.</summary>
    public int AbilityCharge { get; internal set; }
    public PackedPathPlayback? Playback { get; internal set; }
    public ActiveMoveOrder? MoveOrder { get; internal set; }
    /// <summary>
    /// The vent this harvester is travelling to or is currently attached to.
    /// This is authoritative economy state, rather than a presentation-only
    /// Deploy cursor mode.
    /// </summary>
    public int? HarvestVentId { get; internal set; }
    /// <summary>
    /// Data-resolved visual form used while a mobile P7 harvester is deployed.
    /// The seed identity remains the original Exploiter/Slug so economy and
    /// commands do not pretend this is a separately spawned building.
    /// </summary>
    public int? DeployedEntityId { get; internal set; }
    public int? AttackTargetInstanceId { get; internal set; }
    /// <summary>Pending one-shot opcode-0x1b/state-18 ground target.</summary>
    public CellCoordinate? GroundSpecialAttackTarget { get; internal set; }
    /// <summary>Player destination retained while attack-move pursues hostiles.</summary>
    public CellCoordinate? AttackMoveDestination { get; internal set; }
    /// <summary>
    /// Native actor byte <c>+0x34</c>: shots fired in the active weapon burst.
    /// It resets when the weapon's decoded burst limit is reached.
    /// </summary>
    public int BurstShotCount { get; internal set; }
    public int CooldownTicks { get; internal set; }
    /// <summary>Remaining ticks in the native state-13 mine deployment.</summary>
    public int MineDeployTicksRemaining { get; internal set; }
    /// <summary>Remaining ticks in the commander's native state-13 cast.</summary>
    public int InspireCastTicksRemaining { get; internal set; }
    /// <summary>
    /// Native actor byte <c>+0xd6</c>. A nonzero value forces exact-center aim;
    /// it is decremented once per 16 world updates.
    /// </summary>
    public int InspirationTicksRemaining { get; internal set; }
    /// <summary>Native actor word <c>+0xd8</c>: the commander supplying Inspire.</summary>
    public int? InspirationSourceActorInstanceId { get; internal set; }
}

/// <summary>Persistent player intent, segmented by the native 32-step buffer.</summary>
public sealed class ActiveMoveOrder
{
    // dc.exe keeps a fixed eight-point player waypoint list. Retaining that
    // boundary in the authoritative model makes UI and later replay/network
    // input agree on what a single order can contain.
    public const int MaximumWaypoints = 8;
    public ActiveMoveOrder(CellCoordinate target) => Target = target;

    private readonly Queue<CellCoordinate> waypoints = [];

    /// <summary>The destination currently being segmented into packed local paths.</summary>
    public CellCoordinate Target { get; private set; }
    public int PendingWaypointCount => waypoints.Count;
    /// <summary>
    /// Remaining player destinations in execution order. This is a read-only
    /// projection for HUD/replay inspection; queue mutation remains internal
    /// to deterministic command processing.
    /// </summary>
    public IReadOnlyList<CellCoordinate> PendingWaypoints => waypoints.ToArray();
    public int SegmentCount { get; internal set; }
    public int BlockedTicksRemaining { get; internal set; }
    public CellCoordinate? LastBlockedCell { get; internal set; }

    public bool TryAppendWaypoint(CellCoordinate target)
    {
        if (waypoints.Count >= MaximumWaypoints || waypoints.LastOrDefault() == target) return false;
        waypoints.Enqueue(target);
        return true;
    }

    public bool AdvanceWaypoint()
    {
        if (!waypoints.TryDequeue(out var next)) return false;
        Target = next;
        SegmentCount = 0;
        BlockedTicksRemaining = 0;
        LastBlockedCell = null;
        return true;
    }
}

public sealed record MoveCommandOutcome(
    int EntityInstanceId,
    CellCoordinate Target,
    DiagnosticPathTermination PathTermination,
    int StepCount);

public sealed record DestroyedActorEvent(int EntityInstanceId, int EntityId, FixedPointPosition Position);
/// <summary>
/// One authoritative shot. The presentation roll supplies a deterministic
/// choice among the entity's native FIRE/FIREA/B/C pointer array; exact
/// synchronization with dc.exe's shared global random table remains open.
/// </summary>
public sealed record WeaponFireEvent(int SourceActorInstanceId, int WeaponId, byte PresentationVariantRoll);
/// <summary>Resolved impact identity, retaining the firing actor for owner-keyed sound feedback.</summary>
public sealed record ProjectileImpactEvent(int SourceActorInstanceId, int TargetActorInstanceId, int WeaponId, int WeaponClass, FixedPointPosition Position);
public enum HealOutcome { Healed, SourceMissing, SourceDestroyed, SourceNotHealer, InsufficientCharge, NoEligibleTargets, MatrixUnavailable }
public sealed record HealEvent(int SourceActorInstanceId, int TargetActorInstanceId, int Amount, HealOutcome Outcome);
public enum InspireOutcome { Preparing, Applied, SourceMissing, SourceDestroyed, SourceNotCommander, NoEligibleTargets }
public sealed record InspireEvent(int SourceActorInstanceId, int TargetActorInstanceId, int Countdown, InspireOutcome Outcome);
public enum AttackOrderOutcome { Acquired, SourceMissing, TargetMissing, SameActor, SourceDestroyed, TargetDestroyed, Unarmed, NonHostile }
public sealed record AttackOrderEvent(int SourceActorInstanceId, int TargetActorInstanceId, AttackOrderOutcome Outcome);
public enum GroundSpecialAttackOutcome { Accepted, SourceMissing, SourceDestroyed, Unsupported, ResearchRequired, WeaponUnavailable, InvalidTarget }
public sealed record GroundSpecialAttackEvent(int SourceActorInstanceId, CellCoordinate Target, int? WeaponId, GroundSpecialAttackOutcome Outcome);
/// <summary>
/// A mode-5..10 packet impact. Entity 92 is the Human dropship and entity 93
/// is the Gray saucer; payload IDs are newly inserted friendly actors or
/// hostile actors removed by abduction.
/// </summary>
public sealed record BattlefieldTransportEvent(
    BattlefieldTransportEventKind Kind,
    int TransportInstanceId,
    int SourceActorInstanceId,
    int TransportEntityId,
    CellCoordinate Target,
    IReadOnlyList<int> ReinforcementInstanceIds,
    IReadOnlyList<int> AbductedInstanceIds);
public enum AttackMoveOrderOutcome { Accepted, SourceMissing, SourceDestroyed, Unarmed, InvalidEndpoint }
public sealed record AttackMoveOrderEvent(int SourceActorInstanceId, CellCoordinate Target, AttackMoveOrderOutcome Outcome);
public sealed record AttackMoveAcquisitionEvent(int SourceActorInstanceId, int TargetActorInstanceId);
public sealed record PurchaseReservedEvent(int TeamId, int DependencyItemId, PurchaseEligibility Eligibility);
public enum HarvesterDeploymentOutcome { Attached, Retracted, Preparing, EnRoute, SourceInvalid, VentUnavailable, NoApproach }
public sealed record HarvesterDeploymentEvent(int EntityInstanceId, int VentId, HarvesterDeploymentOutcome Outcome)
{
    public bool Attached => Outcome == HarvesterDeploymentOutcome.Attached;
}
public enum MineDeploymentOutcome { Preparing, Deployed, SourceInvalid, AlreadyDeployed, EntityUnresolved, Occupied }
public sealed record MineDeploymentEvent(int SourceActorInstanceId, int EntityInstanceId, int EntityId, CellCoordinate Target, MineDeploymentOutcome Outcome);
public enum TowerDeploymentOutcome { Deployed, SourceInvalid, AlreadyDeployed, EntityUnresolved }
public sealed record TowerDeploymentEvent(int EntityInstanceId, int EntityId, TowerDeploymentOutcome Outcome);
public enum StealDeploymentOutcome { Deployed, Retracted, SourceInvalid, AlreadyDeployed, EntityUnresolved }
public sealed record StealDeploymentEvent(int EntityInstanceId, int EntityId, StealDeploymentOutcome Outcome);
public sealed record P7IncomeEvent(int TeamId, int Amount, int? VentId);
public sealed record P7TheftEvent(
    int ThiefInstanceId,
    int VictimHarvesterInstanceId,
    int ThiefTeamId,
    int VictimTeamId,
    int Amount,
    int VentId);
public sealed record DayNightChangedEvent(DayNightPhase Phase);
public enum BuildingDropOutcome { Placed, CatalogUnavailable, UnknownItem, NotBuilding, NotReserved, WrongFaction, EntityUnresolved, InvalidFootprint, OutOfBounds, Occupied }
public sealed record BuildingPlacedEvent(int TeamId, int DependencyItemId, int EntityInstanceId, int EntityId, CellCoordinate Origin, BuildingDropOutcome Outcome);
public enum UnitProductionOutcome { Produced, CatalogUnavailable, UnknownItem, NotTroop, NotReserved, SourceInvalid, PrerequisiteMissing, SpawnBlocked }
public sealed record UnitProducedEvent(int TeamId, int DependencyItemId, int EntityInstanceId, int EntityId, int SourceBuildingInstanceId, UnitProductionOutcome Outcome);
public enum ResearchOutcome { Completed, CatalogUnavailable, UnknownItem, NotUpgrade, NotReserved, SourceInvalid, PrerequisiteMissing }
public sealed record ResearchCompletedEvent(int TeamId, int DependencyItemId, int SourceBuildingInstanceId, ResearchOutcome Outcome);
public sealed record AutonomousWanderEvent(int EntityInstanceId, int GroupId, CellCoordinate Target, bool MovementStarted);

/// <summary>Authoritative local mission state; presentation only reads it.</summary>
public sealed class ScenarioSimulation
{
    // dc.exe 0x413621 stores 0x32 while a mobile EXPL/SLUG is present on
    // entity-40 VENT, then counts it down before the 6->47 / 14->48 mutation.
    public const int NativeHarvesterAttachTicks = 0x32;
    /// <summary>
    /// Main world update <c>0x419C0E</c> calls dispatcher <c>0x44293C</c>
    /// once; that dispatcher invokes projectile update <c>0x4423F8</c> four
    /// times before returning to the 66 ms world tick.
    /// </summary>
    public const int NativeProjectileSubstepsPerTick = 4;
    public const int NativeImmediateSpecialTicks = 0x32;
    public const int NativeInspireCountdownCadence = 0x10;
    public const int NativeInspireMinimumCountdown = 0x14;
    public const int NativeInspireCountdownMask = 0x0f;
    public const int NativeTransportFlightTicks = 0x32;
    public const int NativeDropShipBaseHeightRaw = 0x258;
    public const int NativeSaucerBaseHeightRaw = 0x4b0;
    public const int NativeHealMinimumCharge = 4;
    public const int NativeAbilityChargeCadence = 0x20;
    /// <summary>
    /// Common fire <c>0x41311C</c> subtracts <c>0x12C</c> from HMINE 45/46
    /// before constructing each projectile, clamping the live health to one.
    /// </summary>
    public const int NativeMineFireIntegrityCost = 0x12c;

    // First two rings of dc.exe's target-search table at 0x434090. The full
    // table extends beyond the mine's range, but these are the only offsets
    // whose cell centres pass weapon 38's range-one squared-distance test.
    private static readonly CellCoordinate[] NativeMineTargetOffsets =
    [
        new(0, 0),
        new(0, 1), new(0, -1), new(1, 0), new(-1, 0),
        new(1, -1), new(-1, 1), new(-1, -1), new(1, 1),
    ];

    private sealed class AutonomousGroupRuntime(AutonomousSpawnGroup definition, IEnumerable<int> memberIds)
    {
        public AutonomousSpawnGroup Definition { get; } = definition;
        public List<int> MemberIds { get; } = memberIds.ToList();
    }

    private readonly PathRegionMap path;
    private readonly IReadOnlyList<EntityDefinition> entityDefinitions;
    private readonly Dictionary<int, SimulatedActor> actorsById;
    private readonly WeaponCatalog? weaponCatalog;
    private readonly DamageMatrix? damageMatrix;
    private readonly AreaEffectCatalog? areaEffects;
    private readonly Dictionary<int, TeamEconomy> teamEconomies;
    private readonly Dictionary<int, int?> teamRaces;
    private readonly DependencyCatalog? dependencyCatalog;
    private readonly BuildingFootprintCatalog? footprints;
    private readonly PetraFlowRules petraFlowRules;
    private readonly PetraStealRules petraStealRules;
    private int petraPulseTicks;
    private int nextProjectileInstanceId = 1;
    private uint firePresentationRandomState = 0x46495245; // "FIRE"
    private uint aimRandomState = 0x41494d21; // "AIM!"
    private uint inspireRandomState = 0x494e5350; // "INSP"
    private uint transportRandomState = 0x5452414e; // "TRAN"
    private int nextActorInstanceId;
    private int nextTransportInstanceId = 1;
    private readonly List<SimulatedActor> actors;
    private readonly List<BattlefieldTransportState> battlefieldTransports = [];
    private readonly List<AutonomousGroupRuntime> autonomousGroups = [];
    private ulong simulationTicks;
    // The native random algorithm has not been identified. This fixed LCG only
    // supplies deterministic replayable values to the recovered masks below.
    private uint autonomousRandomState = 0x4d435254;

    private ScenarioSimulation(
        PathRegionMap path,
        EntityCatalog catalog,
        IEnumerable<SimulatedActor> actors,
        CellOccupancy groundOccupancy,
        CellOccupancy alternateOccupancy,
        CellOccupancy mineOccupancy,
        WeaponCatalog? weaponCatalog,
        DamageMatrix? damageMatrix,
        AreaEffectCatalog? areaEffects,
        IReadOnlyDictionary<int, int> teamResources,
        IReadOnlyDictionary<int, int?> teamRaces,
        DependencyCatalog? dependencyCatalog,
        PetraFlowRules petraFlowRules,
        PetraStealRules petraStealRules,
        DayNightCycle dayNight,
        BuildingFootprintCatalog? footprints,
        TeamRelationMatrix teamRelations)
    {
        this.path = path;
        entityDefinitions = catalog.Entities;
        this.actors = actors.ToList();
        Actors = this.actors;
        actorsById = this.actors.ToDictionary(actor => actor.Seed.InstanceId);
        GroundOccupancy = groundOccupancy;
        AlternateOccupancy = alternateOccupancy;
        MineOccupancy = mineOccupancy;
        this.weaponCatalog = weaponCatalog;
        this.damageMatrix = damageMatrix;
        this.areaEffects = areaEffects;
        teamEconomies = teamResources.ToDictionary(pair => pair.Key, pair => new TeamEconomy(pair.Value));
        this.teamRaces = teamRaces.ToDictionary(pair => pair.Key, pair => pair.Value);
        this.dependencyCatalog = dependencyCatalog;
        this.petraFlowRules = petraFlowRules;
        this.petraStealRules = petraStealRules;
        DayNight = dayNight;
        this.footprints = footprints;
        TeamRelations = teamRelations;
        nextActorInstanceId = actorsById.Count == 0 ? 1 : actorsById.Keys.Max() + 1;
    }

    public IReadOnlyList<SimulatedActor> Actors { get; }
    public CellOccupancy GroundOccupancy { get; }
    public CellOccupancy AlternateOccupancy { get; }
    public CellOccupancy MineOccupancy { get; }
    public IReadOnlyList<MoveCommandOutcome> LastMoveOutcomes { get; private set; } = [];
    public IReadOnlyList<ProjectileState> Projectiles => projectiles;
    public IReadOnlyList<BattlefieldTransportState> BattlefieldTransports => battlefieldTransports;
    public TeamRelationMatrix TeamRelations { get; }
    /// <summary>The game has one resource; values are keyed by scenario team.</summary>
    public IReadOnlyDictionary<int, int> TeamResources => teamEconomies.ToDictionary(pair => pair.Key, pair => pair.Value.P7);
    public int ResourceForTeam(int teamId) => teamEconomies.TryGetValue(teamId, out var economy) ? economy.P7 : 0;
    public TeamEconomy? EconomyForTeam(int teamId) => teamEconomies.GetValueOrDefault(teamId);
    private readonly List<ProjectileState> projectiles = [];
    public IReadOnlyList<DestroyedActorEvent> LastDestroyedActors { get; private set; } = [];
    public IReadOnlyList<WeaponFireEvent> LastWeaponFires { get; private set; } = [];
    public IReadOnlyList<ProjectileImpactEvent> LastProjectileImpacts { get; private set; } = [];
    public IReadOnlyList<HealEvent> LastHeals { get; private set; } = [];
    public IReadOnlyList<InspireEvent> LastInspires { get; private set; } = [];
    public IReadOnlyList<AttackOrderEvent> LastAttackOrders { get; private set; } = [];
    public IReadOnlyList<GroundSpecialAttackEvent> LastGroundSpecialAttacks { get; private set; } = [];
    public IReadOnlyList<BattlefieldTransportEvent> LastBattlefieldTransports { get; private set; } = [];
    public IReadOnlyList<AttackMoveOrderEvent> LastAttackMoveOrders { get; private set; } = [];
    public IReadOnlyList<AttackMoveAcquisitionEvent> LastAttackMoveAcquisitions { get; private set; } = [];
    public IReadOnlyList<PurchaseReservedEvent> LastPurchaseReservations { get; private set; } = [];
    public IReadOnlyList<HarvesterDeploymentEvent> LastHarvesterDeployments { get; private set; } = [];
    public IReadOnlyList<MineDeploymentEvent> LastMineDeployments { get; private set; } = [];
    public IReadOnlyList<TowerDeploymentEvent> LastTowerDeployments { get; private set; } = [];
    public IReadOnlyList<StealDeploymentEvent> LastStealDeployments { get; private set; } = [];
    public IReadOnlyList<P7IncomeEvent> LastP7Income { get; private set; } = [];
    public IReadOnlyList<P7TheftEvent> LastP7Thefts { get; private set; } = [];
    public IReadOnlyList<DayNightChangedEvent> LastDayNightChanges { get; private set; } = [];
    public IReadOnlyList<BuildingPlacedEvent> LastBuildingPlacements { get; private set; } = [];
    public IReadOnlyList<UnitProducedEvent> LastUnitProductions { get; private set; } = [];
    public IReadOnlyList<ResearchCompletedEvent> LastResearchCompletions { get; private set; } = [];
    public IReadOnlyList<AutonomousWanderEvent> LastAutonomousWanders { get; private set; } = [];
    public DayNightCycle DayNight { get; }
    public IReadOnlyList<PetraVent> PetraVents { get; private set; } = [];

    public static ScenarioSimulation Create(
        ScenarioDefinition scenario,
        EntityCatalog catalog,
        PathRegionMap path,
        BuildingFootprintCatalog? footprints = null,
        WeaponCatalog? weaponCatalog = null,
        DamageMatrix? damageMatrix = null,
        DependencyCatalog? dependencyCatalog = null,
        PetraFlowRules? petraFlowRules = null,
        PetraStealRules? petraStealRules = null,
        DayNightCycle? dayNight = null,
        TeamRelationMatrix? teamRelations = null,
        AreaEffectCatalog? areaEffects = null)
    {
        var seeds = scenario.Placements.Where(placement => placement.Team != -1).Select((placement, index) =>
        {
            var cell = new CellCoordinate(placement.X, placement.Z);
            return new WorldEntity(index + 1, placement.EntityId, placement.Team, cell,
                FixedPointPosition.AtCellCenter(cell), placement.Value, placement.Flag);
        }).ToList();
        var ground = new CellOccupancy();
        var alternate = new CellOccupancy();
        var mine = new CellOccupancy();
        foreach (var seed in seeds)
        {
            if ((uint)seed.EntityId >= (uint)catalog.Entities.Count)
                throw new InvalidDataException($"SCN entity ID {seed.EntityId} is outside gamestat.txt.");
            var footprint = footprints?.OccupiedCells(seed.EntityId, seed.SpawnCell) ?? [];
            var definition = catalog[seed.EntityId];
            if (footprint.Count == 0 && definition.MovementSpeed <= 0) continue;
            var occupancy = definition.UsesNativeMineLayer
                ? mine
                : footprint.Count != 0 || definition.MovementClass == 0 ? ground : alternate;
            IReadOnlyList<CellCoordinate> cells = footprint.Count == 0 ? [seed.SpawnCell] : footprint;
            occupancy.ReplaceClaims(seed.InstanceId, cells);
        }

        var autonomous = AutonomousSpawnSeeder.Seed(
            scenario.AutonomousSpawnGroups, catalog, path, ground, alternate, seeds.Count + 1);
        seeds.AddRange(autonomous.Entities);
        var actors = seeds.Select(seed => new SimulatedActor(seed, catalog[seed.EntityId])).ToArray();
        var resources = scenario.Teams.Where(team => team.Enabled)
            .ToDictionary(team => team.TeamId, team => Math.Max(0, team.StartingResource ?? 0));
        var races = scenario.Teams.Where(team => team.Enabled)
            .ToDictionary(team => team.TeamId, team => team.Race);
        var simulation = new ScenarioSimulation(path, catalog, actors, ground, alternate, mine, weaponCatalog, damageMatrix, areaEffects, resources, races, dependencyCatalog,
            petraFlowRules ?? PetraFlowRules.ProvisionalDefault, petraStealRules ?? PetraStealRules.ProvisionalDefault,
            dayNight ?? (scenario.DayNight.IsNativeValid ? DayNightCycle.FromNativeScenario(scenario.DayNight) : new DayNightCycle()), footprints,
            teamRelations ?? TeamRelationMatrix.CreateDefault());
        if (!simulation.petraFlowRules.IsValid) throw new ArgumentOutOfRangeException(nameof(petraFlowRules));
        if (!simulation.petraStealRules.IsValid) throw new ArgumentOutOfRangeException(nameof(petraStealRules));
        simulation.SeedScenarioBuildingDependencies();
        simulation.PetraVents = scenario.Vents.Select((vent, index) => new PetraVent(index, new CellCoordinate(vent.X, vent.Z), vent.Value, vent.Interval)).ToArray();
        simulation.autonomousGroups.AddRange(scenario.AutonomousSpawnGroups.Select(group => new AutonomousGroupRuntime(
            group, autonomous.EntitiesByGroup.GetValueOrDefault(group.GroupId, []).Select(entity => entity.InstanceId))));
        return simulation;
    }

    /// <summary>
    /// A stationary building placed by an SCN is already live when the mission
    /// begins.  Mirror that fact into the dependency state so a campaign map
    /// does not have to buy its authored prerequisite buildings again.
    /// The executable-backed building tuple/entity mapping is deliberately
    /// supplied by <see cref="BuildingFootprintCatalog"/> rather than guessed
    /// from a display name.
    /// </summary>
    private void SeedScenarioBuildingDependencies()
    {
        if (dependencyCatalog is null || footprints is null) return;

        foreach (var actor in actors.OrderBy(actor => actor.Seed.InstanceId))
        {
            if (!teamEconomies.TryGetValue(actor.Seed.Team, out var economy) ||
                !teamRaces.TryGetValue(actor.Seed.Team, out var race) || race is null)
                continue;

            foreach (var item in dependencyCatalog.Items.Values
                         .Where(item => item.IsBuilding && item.BuildingFaction == race)
                         .OrderBy(item => item.Id))
            {
                if (footprints.TryResolveBuildingEntity(item.BuildingFaction!.Value, item.BuildingVariant!.Value,
                        item.BuildingSlot!.Value, out var entityId) && entityId == actor.Seed.EntityId)
                {
                    economy.SeedCompletedBuilding(dependencyCatalog, item.Id);
                }
            }
        }
    }

    public SimulatedActor? Actor(int instanceId) => actorsById.GetValueOrDefault(instanceId);

    /// <summary>
    /// Construction calls this after a delivered building becomes live. It is
    /// separate from <see cref="PurchaseIntent"/> so a paid pedestal order does
    /// not unlock its dependents early.
    /// </summary>
    public bool MarkDependencyBuildingCompleted(int teamId, int dependencyItemId) =>
        teamEconomies.TryGetValue(teamId, out var economy) && economy.MarkCompleted(dependencyCatalog, dependencyItemId);

    /// <summary>Uses the catalog's separate day/night observation columns.</summary>
    public int ObservationRange(SimulatedActor actor) => DayNight.Phase == DayNightPhase.Day
        ? EffectiveDefinition(actor).DayObservation : EffectiveDefinition(actor).NightObservation;

    /// <summary>
    /// Returns whether a map cell is currently inside the live sight radius of
    /// an enabled team's surviving actors. This is an engine query so rendering,
    /// targeting, and future fog-memory rules share day/night observation data.
    /// </summary>
    public bool IsCellVisibleToTeam(int teamId, CellCoordinate cell)
    {
        foreach (var observer in Actors)
        {
            if (observer.IsDestroyed || observer.Seed.Team != teamId) continue;
            var range = ObservationRange(observer);
            var origin = observer.Movement.OccupiedCell;
            var x = (long)cell.X - origin.X;
            var z = (long)cell.Z - origin.Z;
            if (x * x + z * z <= (long)range * range) return true;
        }
        return false;
    }

    /// <summary>Visibility of a live actor at its authoritative occupied cell.</summary>
    public bool IsActorVisibleToTeam(int teamId, SimulatedActor actor) =>
        !actor.IsDestroyed && IsCellVisibleToTeam(teamId, actor.Movement.OccupiedCell);

    /// <summary>Returns an actor's current shipped form for all live simulation capabilities.</summary>
    public EntityDefinition EffectiveDefinition(SimulatedActor actor) => actor.DeployedEntityId is { } entityId
        ? EntityDefinitionFor(entityId)
        : actor.Definition;

    /// <summary>
    /// Number of native 300-integrity mine triggers still represented by the
    /// current live health. The final trigger leaves one health until weapon
    /// 38's ordinary splash resolves.
    /// </summary>
    public int MineTriggersRemainingFor(SimulatedActor actor) =>
        !actor.IsDestroyed && EffectiveDefinition(actor).Code == "HMINE"
            ? Math.Max(1, (actor.Health + NativeMineFireIntegrityCost - 1) / NativeMineFireIntegrityCost)
            : 0;

    /// <summary>
    /// Completed weapon research maps directly to the levelled weapon slots
    /// carried by the original entity definition. This intentionally applies
    /// to both existing and later-produced actors because upgrades are
    /// team-owned technology, not a production-time stat copy.
    /// </summary>
    public int WeaponUpgradeLevel(SimulatedActor actor) =>
        teamEconomies.TryGetValue(actor.Seed.Team, out var economy)
            ? economy.CompletedUpgradeLevel(dependencyCatalog, EffectiveDefinition(actor).Id, category: 0)
            : 0;

    /// <summary>Tracked armor technology for HUD/debug use; its damage multiplier is not yet recovered.</summary>
    public int ArmorUpgradeLevel(SimulatedActor actor) =>
        teamEconomies.TryGetValue(actor.Seed.Team, out var economy)
            ? economy.CompletedUpgradeLevel(dependencyCatalog, EffectiveDefinition(actor).Id, category: 1)
            : 0;

    public WeaponDefinition? EffectiveWeaponFor(SimulatedActor actor) =>
        TryGetWeapon(actor, out var weapon) ? weapon : null;

    public WeaponDefinition? GroundSpecialWeaponFor(SimulatedActor actor) =>
        weaponCatalog?.TryGet(EffectiveDefinition(actor).GroundSpecialWeaponId, out var weapon) == true ? weapon : null;

    public bool IsGroundSpecialTargetInRange(SimulatedActor actor)
    {
        if (actor.GroundSpecialAttackTarget is not { } target || GroundSpecialWeaponFor(actor) is not { } weapon) return false;
        var range = (long)weapon.Range * FixedPointPosition.One;
        return DistanceSquared(actor.Movement.VisualPosition, FixedPointPosition.AtCellCenter(target)) < range * range;
    }

    /// <summary>
    /// Native firing compares squared 8.8 distances strictly against the
    /// weapon range converted to fixed-point scale. This is only the firing
    /// precondition; projectile and damage processing remain separate.
    /// </summary>
    public bool IsAttackTargetInRange(SimulatedActor attacker)
    {
        if (weaponCatalog is null || attacker.IsDestroyed || attacker.AttackTargetInstanceId is not { } targetId ||
            !actorsById.TryGetValue(targetId, out var target) || target.Health <= 0) return false;
        if (!TryGetWeapon(attacker, out var weapon)) return false;
        var source = attacker.Movement.VisualPosition;
        var destination = target.Movement.VisualPosition;
        var dx = (long)destination.XRaw - source.XRaw;
        var dz = (long)destination.ZRaw - source.ZRaw;
        var range = (long)weapon.Range * FixedPointPosition.One;
        return dx * dx + dz * dz < range * range;
    }

    public void Step(IEnumerable<ScheduledWorldCommand> commands)
    {
        var inspires = UpdateInspireState();
        UpdateAbilityCharge();
        var outcomes = new List<MoveCommandOutcome>();
        var destroyed = new List<DestroyedActorEvent>();
        var fired = new List<WeaponFireEvent>();
        var impacts = new List<ProjectileImpactEvent>();
        var battlefieldTransports = new List<BattlefieldTransportEvent>();
        var heals = new List<HealEvent>();
        var attacks = new List<AttackOrderEvent>();
        var groundSpecialAttacks = new List<GroundSpecialAttackEvent>();
        var attackMoves = new List<AttackMoveOrderEvent>();
        var attackMoveAcquisitions = new List<AttackMoveAcquisitionEvent>();
        var purchases = new List<PurchaseReservedEvent>();
        var harvesterDeployments = new List<HarvesterDeploymentEvent>();
        var mineDeployments = new List<MineDeploymentEvent>();
        var towerDeployments = new List<TowerDeploymentEvent>();
        var stealDeployments = new List<StealDeploymentEvent>();
        var buildingPlacements = new List<BuildingPlacedEvent>();
        var unitProductions = new List<UnitProducedEvent>();
        var researchCompletions = new List<ResearchCompletedEvent>();
        mineDeployments.AddRange(UpdateMineDeploymentState());
        UpdateBattlefieldTransports(battlefieldTransports);
        foreach (var scheduled in commands.OrderBy(command => command.Sequence))
        {
            if (scheduled.Command is PurchaseIntent purchase)
            {
                var eligibility = teamEconomies.TryGetValue(purchase.TeamId, out var economy)
                    ? economy.TryReserve(dependencyCatalog, purchase.DependencyItemId)
                    : PurchaseEligibility.UnknownItem;
                purchases.Add(new PurchaseReservedEvent(purchase.TeamId, purchase.DependencyItemId, eligibility));
                continue;
            }
            if (scheduled.Command is HarvestVentIntent harvest)
            {
                harvesterDeployments.Add(RequestHarvesterDeployment(harvest.EntityInstanceId, harvest.VentId));
                continue;
            }
            if (scheduled.Command is RetractHarvesterIntent retractHarvester)
            {
                harvesterDeployments.Add(RetractHarvester(retractHarvester));
                continue;
            }
            if (scheduled.Command is DeployMineIntent mine)
            {
                mineDeployments.Add(BeginMineDeployment(mine));
                continue;
            }
            if (scheduled.Command is DeployTowerIntent tower)
            {
                towerDeployments.Add(DeployTower(tower));
                continue;
            }
            if (scheduled.Command is DeployStealIntent steal)
            {
                stealDeployments.Add(DeploySteal(steal));
                continue;
            }
            if (scheduled.Command is RetractStealIntent retractSteal)
            {
                stealDeployments.Add(RetractSteal(retractSteal));
                continue;
            }
            if (scheduled.Command is PlaceBuildingIntent placement)
            {
                buildingPlacements.Add(PlaceBuilding(placement));
                continue;
            }
            if (scheduled.Command is ProduceUnitIntent production)
            {
                unitProductions.Add(ProduceUnit(production));
                continue;
            }
            if (scheduled.Command is ResearchIntent research)
            {
                researchCompletions.Add(CompleteResearch(research));
                continue;
            }
            if (scheduled.Command is StopIntent stop && actorsById.TryGetValue(stop.EntityInstanceId, out var stoppedActor))
            {
                if (stoppedActor.IsDestroyed) continue;
                stoppedActor.Playback?.Cancel();
                stoppedActor.Playback = null;
                stoppedActor.MoveOrder = null;
                stoppedActor.AttackTargetInstanceId = null;
                stoppedActor.GroundSpecialAttackTarget = null;
                stoppedActor.AttackMoveDestination = null;
                stoppedActor.InspireCastTicksRemaining = 0;
                stoppedActor.MineDeployTicksRemaining = 0;
                // Stop cancels an en-route/pending vent order. An attached
                // EDPLY/SDPL form is static and requires its proven reverse
                // transition instead of silently becoming mobile.
                if (stoppedActor.DeployedEntityId is null) DetachHarvester(stoppedActor);
                continue;
            }
            if (scheduled.Command is AttackIntent attack)
            {
                attacks.Add(IssueAttackOrder(attack));
                continue;
            }
            if (scheduled.Command is GroundSpecialAttackIntent groundSpecial)
            {
                groundSpecialAttacks.Add(IssueGroundSpecialAttack(groundSpecial));
                continue;
            }
            if (scheduled.Command is HealAreaIntent heal)
            {
                heals.AddRange(IssueAreaHeal(heal));
                continue;
            }
            if (scheduled.Command is InspireTroopsIntent inspire)
            {
                inspires.Add(BeginInspire(inspire));
                continue;
            }
            if (scheduled.Command is AttackMoveIntent attackMove)
            {
                attackMoves.Add(IssueAttackMoveOrder(attackMove));
                continue;
            }
            if (scheduled.Command is not MoveIntent move || !actorsById.TryGetValue(move.EntityInstanceId, out var actor)) continue;
            if (actor.IsDestroyed) continue;
            if (EffectiveDefinition(actor).MovementSpeed <= 0)
            {
                outcomes.Add(new MoveCommandOutcome(actor.Seed.InstanceId, move.TargetCell, DiagnosticPathTermination.InvalidEndpoint, 0));
                continue;
            }
            if (move.AppendWaypoint && actor.MoveOrder is { } activeOrder)
            {
                activeOrder.TryAppendWaypoint(move.TargetCell);
                outcomes.Add(new MoveCommandOutcome(actor.Seed.InstanceId, move.TargetCell, DiagnosticPathTermination.ReachedTarget, 0));
                continue;
            }

            DetachHarvester(actor);
            actor.MineDeployTicksRemaining = 0;
            actor.AttackMoveDestination = null;
            actor.GroundSpecialAttackTarget = null;
            actor.MoveOrder = new ActiveMoveOrder(move.TargetCell);
            outcomes.Add(StartSegment(actor));
        }
        LastMoveOutcomes = outcomes;
        LastAttackOrders = attacks;
        LastGroundSpecialAttacks = groundSpecialAttacks;
        LastHeals = heals;
        LastInspires = inspires;
        LastAttackMoveOrders = attackMoves;
        LastPurchaseReservations = purchases;
        LastHarvesterDeployments = harvesterDeployments;
        LastMineDeployments = mineDeployments;
        LastTowerDeployments = towerDeployments;
        LastStealDeployments = stealDeployments;
        LastBuildingPlacements = buildingPlacements;
        LastUnitProductions = unitProductions;
        LastResearchCompletions = researchCompletions;
        LastAutonomousWanders = UpdateAutonomousActors();

        foreach (var actor in Actors)
        {
            if (actor.IsDestroyed) continue;
            if (actor.GroundSpecialAttackTarget is { } specialTarget)
                UpdateGroundSpecialAttack(actor, specialTarget, fired);
            if (actor.AttackTargetInstanceId is { } targetId)
            {
                if (!actorsById.TryGetValue(targetId, out var target) || target.Health <= 0)
                    actor.AttackTargetInstanceId = null;
                else
                {
                    actor.Facing.FaceTowards(actor.Movement.VisualPosition, target.Movement.VisualPosition);
                    PursueAttackTarget(actor, target);
                }
            }
            if (actor.AttackTargetInstanceId is null && actor.AttackMoveDestination is { } attackMoveDestination)
            {
                var hostile = FindAttackMoveTarget(actor);
                if (hostile is not null)
                {
                    actor.AttackTargetInstanceId = hostile.Seed.InstanceId;
                    attackMoveAcquisitions.Add(new AttackMoveAcquisitionEvent(actor.Seed.InstanceId, hostile.Seed.InstanceId));
                }
                else if (actor.Movement.OccupiedCell == attackMoveDestination)
                {
                    actor.AttackMoveDestination = null;
                }
                else if (actor.MoveOrder is null && actor.Playback is null)
                {
                    actor.MoveOrder = new ActiveMoveOrder(attackMoveDestination);
                    _ = StartSegment(actor);
                }
            }
            if (actor.Facing.Current != actor.Facing.Target && EffectiveDefinition(actor).TurnSpeed > 0)
                actor.Facing.Step(EffectiveDefinition(actor).TurnSpeed);
            if (actor.CooldownTicks > 0) actor.CooldownTicks--;
            if (actor.Playback is not null)
            {
                var status = actor.Playback.Step();
                if (status == PackedPathPlaybackStatus.Blocked)
                {
                    actor.MoveOrder ??= new ActiveMoveOrder(actor.Movement.OccupiedCell);
                    actor.MoveOrder.LastBlockedCell = actor.Playback.BlockedCell;
                    actor.Playback = null;
                    // Native blockage handling first attempts to reconstruct a
                    // usable local suffix. The four-execution wait is only the
                    // failure path (yield/jitter remains unrecovered).
                    var repair = StartSegment(actor);
                    if (repair.StepCount == 0)
                    {
                        if (actor.MoveOrder is { } waitingOrder) waitingOrder.BlockedTicksRemaining = 4;
                    }
                }
                else if (status == PackedPathPlaybackStatus.Complete)
                {
                    actor.Playback = null;
                    if (actor.MoveOrder?.Target == actor.Movement.OccupiedCell && !actor.MoveOrder.AdvanceWaypoint())
                        actor.MoveOrder = null;
                }
            }

            if (actor.Playback is not null || actor.MoveOrder is null) continue;
            if (actor.MoveOrder.BlockedTicksRemaining > 0)
            {
                actor.MoveOrder.BlockedTicksRemaining--;
                continue;
            }
            _ = StartSegment(actor);
        }

        UpdateHarvesterDeploymentOrders(harvesterDeployments);

        // HMINE records resolve weapon 38, explicitly named "mine" in the
        // shipped weapon catalog. Common fire 0x41311C removes 300 live health
        // (floor 1) for every trigger, then follows the ordinary weapon path.
        // Weapon 38's own same-team area splash kills the one-health mine on
        // its third trigger. Target-scan cadence remains provisional, but fire
        // cadence is the normal decoded weapon cooldown rather than one shot
        // per world update.
        foreach (var mine in Actors.Where(actor => !actor.IsDestroyed && EffectiveDefinition(actor).Code == "HMINE").OrderBy(actor => actor.Seed.InstanceId))
        {
            if (mine.CooldownTicks > 0 || !TryGetWeapon(mine, out var weapon)) continue;
            var target = FindMineTriggerTarget(mine, weapon);
            if (target is null) continue;
            mine.Health = Math.Max(1, mine.Health - NativeMineFireIntegrityCost);
            SpawnProjectile(mine, target, weapon, fired);
        }

        foreach (var attacker in Actors)
        {
            if (attacker.AttackTargetInstanceId is not { } targetId || attacker.CooldownTicks > 0 ||
                attacker.Facing.Current != attacker.Facing.Target || !actorsById.TryGetValue(targetId, out var target) ||
                !IsAttackTargetInRange(attacker)) continue;
            if (!TryGetWeapon(attacker, out var weapon)) continue;
            SpawnProjectile(attacker, target, weapon, fired);
        }

        for (var index = projectiles.Count - 1; index >= 0; index--)
        {
            var projectile = projectiles[index];
            WeaponDefinition? weapon = null;
            var weaponClass = -1;
            if (weaponCatalog?.TryGet(projectile.WeaponId, out var resolvedWeapon) == true)
            {
                weapon = resolvedWeapon;
                weaponClass = weapon.WeaponClass;
            }
            var resolved = false;
            for (var substep = 0; substep < NativeProjectileSubstepsPerTick && !resolved; substep++)
            {
                projectile.Step();

                // dc.exe 0x442494 advances the projectile before probing current
                // occupancy. The shot is no longer locked to its originally aimed
                // actor: an intervening hostile can take the hit, while the source
                // and cooperative teams are ignored by 0x4425F6-0x442675.
                // A completed state-18 shot is still reported as a ground impact;
                // occupants in earlier cells can intercept it, but an occupant of
                // the commanded destination does not turn the command back into an
                // actor-targeted order in the port's event model.
                var reachedTimedAim = projectile.TimedImpactCell is { } aimedCell &&
                    (projectile.Position.Cell == aimedCell || projectile.ReachedAimedPosition);
                var collision = projectile.TimedImpactCell is null ? FindProjectileCollision(projectile) : null;
                if (collision is not null)
                {
                    projectiles.RemoveAt(index);
                    if (weaponClass >= 0) impacts.Add(new ProjectileImpactEvent(projectile.SourceActorInstanceId, collision.Seed.InstanceId, projectile.WeaponId, weaponClass, projectile.Position));
                    if (weapon is { HasAreaEffect: true } && areaEffects?.TryGet(weapon.AreaEffectTemplateId, out var collisionEffect) == true)
                        ApplyAreaDamage(projectile, weapon, projectile.Position.Cell, collisionEffect, destroyed);
                    else ApplyDamage(collision, projectile.Damage, destroyed);
                    resolved = true;
                    continue;
                }

                // State-18 ground fire supplies an exact cell rather than an actor.
                // Its vertical trajectory is not represented yet, so reaching the
                // aimed X/Z position is the explicit adapter for the native ground
                // collision; ordinary missed shots instead continue until expiry.
                if (projectile.TimedImpactCell is { } timedImpactCell && reachedTimedAim)
                {
                    projectiles.RemoveAt(index);
                    var impactPosition = FixedPointPosition.AtCellCenter(timedImpactCell);
                    var impactTargetId = projectile.GroundTargetCell is null ? projectile.TargetActorInstanceId : -1;
                    if (weaponClass >= 0) impacts.Add(new ProjectileImpactEvent(projectile.SourceActorInstanceId, impactTargetId, projectile.WeaponId, weaponClass, impactPosition));
                    if (weapon is not null && ResolveBattlefieldTransportImpact(projectile, weapon, timedImpactCell, battlefieldTransports))
                    {
                        resolved = true;
                        continue;
                    }
                    if (weapon is { HasAreaEffect: true } && areaEffects?.TryGet(weapon.AreaEffectTemplateId, out var timedEffect) == true)
                        ApplyAreaDamage(projectile, weapon, timedImpactCell, timedEffect, destroyed);
                    resolved = true;
                    continue;
                }

                if (!projectile.ExceededMaximumLifetime) continue;
                projectiles.RemoveAt(index);
                resolved = true;
            }
            if (!resolved) projectile.CompleteWorldTick();
        }
        LastDestroyedActors = destroyed;
        LastWeaponFires = fired;
        LastProjectileImpacts = impacts;
        LastBattlefieldTransports = battlefieldTransports;
        LastAttackMoveAcquisitions = attackMoveAcquisitions;
        LastDayNightChanges = DayNight.Step() ? [new DayNightChangedEvent(DayNight.Phase)] : [];
        LastP7Income = ApplyPetraFlow();
        simulationTicks++;
    }

    private List<InspireEvent> UpdateInspireState()
    {
        var events = new List<InspireEvent>();
        // 0x4192F0 gates actor byte +0xd6 on the low four world-counter bits.
        if ((simulationTicks & (NativeInspireCountdownCadence - 1)) == 0)
        {
            foreach (var actor in Actors.Where(actor => actor.InspirationTicksRemaining > 0))
            {
                actor.InspirationTicksRemaining--;
                if (actor.InspirationTicksRemaining == 0)
                    actor.InspirationSourceActorInstanceId = null;
            }
        }

        foreach (var commander in Actors.Where(actor => actor.InspireCastTicksRemaining > 0)
                     .OrderBy(actor => actor.Seed.InstanceId))
        {
            commander.InspireCastTicksRemaining--;
            if (commander.InspireCastTicksRemaining == 0)
                events.AddRange(ApplyInspire(commander));
        }
        return events;
    }

    private void UpdateAbilityCharge()
    {
        // Common actor update 0x4192C0 runs this only when the low five world
        // counter bits are zero. Runtime +0xf8 is gamestat source value 25.
        if ((simulationTicks & (NativeAbilityChargeCadence - 1)) != 0) return;
        foreach (var actor in Actors.Where(actor => !actor.IsDestroyed))
        {
            var recovery = Math.Max(0, EffectiveDefinition(actor).AbilityChargeRecovery);
            if (recovery == 0) continue;
            actor.AbilityCharge = actor.AbilityCharge >= SimulatedActor.NativeMaximumAbilityCharge - recovery
                ? SimulatedActor.NativeMaximumAbilityCharge
                : actor.AbilityCharge + recovery;
        }
    }

    private IReadOnlyList<MineDeploymentEvent> UpdateMineDeploymentState()
    {
        var events = new List<MineDeploymentEvent>();
        foreach (var actor in Actors.Where(actor => actor.MineDeployTicksRemaining > 0)
                     .OrderBy(actor => actor.Seed.InstanceId))
        {
            if (actor.IsDestroyed)
            {
                actor.MineDeployTicksRemaining = 0;
                continue;
            }
            actor.MineDeployTicksRemaining--;
            if (actor.MineDeployTicksRemaining == 0)
                events.Add(CompleteMineDeployment(actor));
        }
        return events;
    }

    private InspireEvent BeginInspire(InspireTroopsIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source))
            return new(intent.EntityInstanceId, intent.EntityInstanceId, 0, InspireOutcome.SourceMissing);
        if (source.IsDestroyed)
            return new(intent.EntityInstanceId, intent.EntityInstanceId, 0, InspireOutcome.SourceDestroyed);
        var definition = EffectiveDefinition(source);
        if (definition.Id is < 69 or > 76 || definition.ImmediateSpecialCode == 0 || !definition.HasImmediateAreaEffect)
            return new(intent.EntityInstanceId, intent.EntityInstanceId, 0, InspireOutcome.SourceNotCommander);

        source.Playback?.Cancel();
        source.Playback = null;
        source.MoveOrder = null;
        source.AttackTargetInstanceId = null;
        source.GroundSpecialAttackTarget = null;
        source.AttackMoveDestination = null;
        source.InspireCastTicksRemaining = NativeImmediateSpecialTicks;
        return new(source.Seed.InstanceId, source.Seed.InstanceId, NativeImmediateSpecialTicks, InspireOutcome.Preparing);
    }

    private IReadOnlyList<InspireEvent> ApplyInspire(SimulatedActor source)
    {
        var remainingHits = Math.Max(0, EffectiveDefinition(source).ImmediateAreaTargetLimit);
        var events = new List<InspireEvent>();
        var origin = source.Movement.OccupiedCell;
        // 0x4171C7-0x41739E scans four sides for radii 0..10. Each side spans
        // -20..20 cells and probes the ordinary layer before the alternate
        // layer. Deliberately retain duplicate centre-line hits: the native
        // helper decrements its 6/8/10/12 limit for every qualifying probe.
        for (var radius = 0; radius <= 10 && remainingHits > 0; radius++)
        for (var offset = -20; offset <= 20 && remainingHits > 0; offset++)
        for (var side = 0; side < 4 && remainingHits > 0; side++)
        {
            var cell = side switch
            {
                0 => new CellCoordinate(origin.X - radius, origin.Z + offset),
                1 => new CellCoordinate(origin.X + radius, origin.Z + offset),
                2 => new CellCoordinate(origin.X + offset, origin.Z - radius),
                _ => new CellCoordinate(origin.X + offset, origin.Z + radius),
            };
            if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height) continue;
            if (TryInspireOccupant(GroundOccupancy, cell, source, ref remainingHits, events) && remainingHits == 0)
                break;
            _ = TryInspireOccupant(AlternateOccupancy, cell, source, ref remainingHits, events);
        }
        return events.Count != 0
            ? events
            : [new InspireEvent(source.Seed.InstanceId, source.Seed.InstanceId, 0, InspireOutcome.NoEligibleTargets)];
    }

    private bool TryInspireOccupant(
        CellOccupancy occupancy,
        CellCoordinate cell,
        SimulatedActor source,
        ref int remainingHits,
        ICollection<InspireEvent> events)
    {
        if (remainingHits == 0 || !occupancy.TryGetOwner(cell, out var targetId) ||
            !actorsById.TryGetValue(targetId, out var target) || target.IsDestroyed ||
            target.Seed.Team != source.Seed.Team || EffectiveDefinition(target).HasImmediateAreaEffect ||
            !TryGetWeapon(target, out _)) return false;
        var countdown = NativeInspireMinimumCountdown + (int)(NextInspireRandom() & NativeInspireCountdownMask);
        target.InspirationTicksRemaining = countdown;
        target.InspirationSourceActorInstanceId = source.Seed.InstanceId;
        events.Add(new(source.Seed.InstanceId, target.Seed.InstanceId, countdown, InspireOutcome.Applied));
        remainingHits--;
        return true;
    }

    private uint NextInspireRandom()
    {
        // dc.exe uses its shared 256-entry random stream. Until that table is
        // recovered, keep the proven low-nibble mask deterministic for replay.
        inspireRandomState = unchecked(inspireRandomState * 214013 + 2531011);
        return inspireRandomState >> 16;
    }

    private void ApplyAreaDamage(ProjectileState projectile, WeaponDefinition weapon, CellCoordinate center,
        AreaEffectTemplate effect, ICollection<DestroyedActorEvent> destroyed)
    {
        if (!actorsById.TryGetValue(projectile.SourceActorInstanceId, out var source)) return;
        var radius = effect.PatternSize / 2;
        foreach (var actor in Actors.Where(actor => !actor.IsDestroyed).OrderBy(actor => actor.Seed.InstanceId))
        {
            var dx = actor.Movement.OccupiedCell.X - center.X;
            var dz = actor.Movement.OccupiedCell.Z - center.Z;
            if (Math.Abs(dx) > radius || Math.Abs(dz) > radius) continue;
            var percent = effect.DamagePattern[dz + radius][dx + radius];
            if (percent <= 0) continue;
            // 0x4420F4 compares exact team bytes, not the alliance matrix.
            // Same-team splash uses 0x40/0x100 (one quarter); every other
            // occupied team receives the authored pattern at full strength.
            if (actor.Seed.Team == source.Seed.Team) percent = percent * 0x40 / 0x100;
            if (percent <= 0) continue;
            var baseDamage = damageMatrix is null ? weapon.Damage : damageMatrix.CalculateBaseDamage(weapon.Damage, weapon.WeaponClass, EffectiveDefinition(actor).ArmorClass);
            ApplyDamage(actor, checked(baseDamage * percent / 100), destroyed);
        }
    }

    private bool ResolveBattlefieldTransportImpact(
        ProjectileState projectile,
        WeaponDefinition weapon,
        CellCoordinate center,
        ICollection<BattlefieldTransportEvent> events)
    {
        if (weapon.ProjectileMode is < 5 or > 10) return false;
        if (!actorsById.TryGetValue(projectile.SourceActorInstanceId, out var source) || source.IsDestroyed) return true;

        if (weapon.ProjectileMode <= 7)
        {
            // dc.exe 0x441F29 builds two five-byte payload arrays before
            // 0x418F4C creates entity 92. Mode 5 carries 2x entity 0; mode 6
            // falls through and adds entity 2; mode 7 also adds entity 3.
            var payload = new List<int> { 0, 0 };
            if (weapon.ProjectileMode >= 6) payload.Add(2);
            if (weapon.ProjectileMode >= 7) payload.Add(3);
            StartBattlefieldTransport(source, 92, center, payload, [], events);
            return true;
        }

        // Modes 8/9/10 pass radii 4/6/8 to 0x416F54, cap the scan at nine,
        // reject cooperative/unarmed/commander actors, then call 0x418F4C in
        // groups of at most three. Each call creates entity 93.
        var radius = (weapon.ProjectileMode - 6) * 2;
        var abductees = Actors
            .Where(candidate => !candidate.IsDestroyed && candidate.Seed.InstanceId != source.Seed.InstanceId)
            .Where(candidate => TeamRelations.IsHostile(source.Seed.Team, candidate.Seed.Team))
            .Where(candidate => !EffectiveDefinition(candidate).HasImmediateAreaEffect && TryGetWeapon(candidate, out _))
            .Select(candidate => new
            {
                Actor = candidate,
                Radius = Math.Max(Math.Abs(candidate.Movement.OccupiedCell.X - center.X), Math.Abs(candidate.Movement.OccupiedCell.Z - center.Z)),
            })
            .Where(candidate => candidate.Radius <= radius)
            .OrderBy(candidate => candidate.Radius)
            .ThenBy(candidate => candidate.Actor.Movement.OccupiedCell.Z)
            .ThenBy(candidate => candidate.Actor.Movement.OccupiedCell.X)
            .ThenBy(candidate => candidate.Actor.Seed.InstanceId)
            .Take(9)
            .Select(candidate => candidate.Actor)
            .ToArray();
        foreach (var group in abductees.Chunk(3))
        {
            var ids = group.Select(candidate => candidate.Seed.InstanceId).ToArray();
            StartBattlefieldTransport(source, 93, center, [], ids, events);
        }
        return true;
    }

    private void StartBattlefieldTransport(
        SimulatedActor source,
        int transportEntityId,
        CellCoordinate target,
        IReadOnlyList<int> reinforcementEntityIds,
        IReadOnlyList<int> abducteeInstanceIds,
        ICollection<BattlefieldTransportEvent> events)
    {
        // 0x41906F..0x4190E5 consumes two random-table entries, masks each
        // mixed low bit, and produces target center +/- one complete cell.
        // The original table index is global; this deterministic stream keeps
        // the recovered two-way offset replayable until shared RNG parity.
        var xOffset = (NextTransportRandom() & 1) == 0 ? -FixedPointPosition.One : FixedPointPosition.One;
        var zOffset = (NextTransportRandom() & 1) == 0 ? -FixedPointPosition.One : FixedPointPosition.One;
        var position = FixedPointPosition.AtCellCenter(target).AddRaw(xOffset, zOffset);
        var baseHeight = transportEntityId == 93 ? NativeSaucerBaseHeightRaw : NativeDropShipBaseHeightRaw;
        var transportDefinition = EntityDefinitionFor(transportEntityId);
        var state = new BattlefieldTransportState(nextTransportInstanceId++, source.Seed.InstanceId,
            transportEntityId, source.Seed.Team, target, position, baseHeight,
            transportDefinition.InitialFacing,
            reinforcementEntityIds, abducteeInstanceIds);
        battlefieldTransports.Add(state);
        events.Add(new BattlefieldTransportEvent(BattlefieldTransportEventKind.Started, state.InstanceId,
            source.Seed.InstanceId, transportEntityId, target, [], []));
    }

    private void UpdateBattlefieldTransports(ICollection<BattlefieldTransportEvent> events)
    {
        for (var index = battlefieldTransports.Count - 1; index >= 0; index--)
        {
            var transport = battlefieldTransports[index];
            if (transport.PursuitTarget is { } pursuitTarget)
            {
                if (transport.IsTurning)
                {
                    var turnSpeed = Math.Max(1, EntityDefinitionFor(transport.TransportEntityId).TurnSpeed);
                    if (!transport.Facing.Step(turnSpeed)) transport.IsTurning = false;
                    // Command 4 consumes this actor update even when it reaches
                    // the requested bearing and removes itself.
                    continue;
                }
                if (transport.HorizontalExecutionsRemaining > 0)
                {
                    transport.Position = transport.Position.AddRaw(
                        transport.HorizontalVelocityXRaw, transport.HorizontalVelocityZRaw);
                    transport.HorizontalExecutionsRemaining--;
                    continue;
                }

                // Command 5 removes itself at count zero. State 21 resumes on
                // the next update and copies its saved exact target position.
                transport.Position = pursuitTarget;
                transport.PursuitTarget = null;
                continue;
            }
            if (transport.Phase == BattlefieldTransportPhase.Descending)
            {
                if (transport.FlightCounter > 0)
                {
                    // State-22 handler 0x4183B8 uses the pre-decrement counter:
                    // height = base + 6*t*t/2.
                    var counter = transport.FlightCounter;
                    transport.HeightRaw = transport.BaseHeightRaw + 3 * counter * counter;
                    transport.FlightCounter--;
                    continue;
                }
                transport.Position = FixedPointPosition.AtCellCenter(transport.Target);
                transport.HeightRaw = transport.BaseHeightRaw;
                transport.Phase = BattlefieldTransportPhase.Delivering;
                continue;
            }

            if (transport.Phase == BattlefieldTransportPhase.Delivering)
            {
                // State-21 phase zero only advances its phase word. Payload
                // processing starts on the following actor update.
                if (!transport.DeliveryInitialized)
                {
                    transport.DeliveryInitialized = true;
                    continue;
                }

                // Saucer payload word zero has high byte 0xff. State 21 treats
                // it as a header, advances the payload cursor, and returns
                // before inspecting the first victim on the following update.
                if (transport.TransportEntityId == 93 && !transport.PayloadHeaderProcessed)
                {
                    transport.PayloadHeaderProcessed = true;
                    continue;
                }

                if (transport.PayloadIndex < transport.PayloadCount)
                {
                    if (transport.TransportEntityId == 92)
                    {
                        var entityId = transport.PendingEntityIds[transport.PayloadIndex++];
                        var spawned = TrySpawnTransportPayload(entityId, transport.TeamId, transport.Target, out var instanceId)
                            ? new[] { instanceId }
                            : [];
                        events.Add(new BattlefieldTransportEvent(BattlefieldTransportEventKind.PayloadResolved,
                            transport.InstanceId, transport.SourceActorInstanceId, transport.TransportEntityId,
                            transport.Target, spawned, []));
                    }
                    else
                    {
                        var abducteeId = transport.PendingAbducteeInstanceIds[transport.PayloadIndex];
                        var removed = Array.Empty<int>();
                        if (actorsById.TryGetValue(abducteeId, out var abductee) && !abductee.IsDestroyed)
                        {
                            var victimPosition = abductee.Movement.VisualPosition;
                            var dx = victimPosition.XRaw - transport.Position.XRaw;
                            var dz = victimPosition.ZRaw - transport.Position.ZRaw;
                            // 0x418BDC compares Manhattan 8.8 distance with
                            // 0x100. Farther victims receive a direct 0x412388
                            // command-5 interpolation before state 21 resumes.
                            if (Math.Abs(dx) + Math.Abs(dz) > FixedPointPosition.One)
                            {
                                BeginTransportPursuit(transport, victimPosition);
                                continue;
                            }
                            RemoveActorFromWorld(abductee);
                            removed = [abducteeId];
                        }
                        transport.PayloadIndex++;
                        events.Add(new BattlefieldTransportEvent(BattlefieldTransportEventKind.PayloadResolved,
                            transport.InstanceId, transport.SourceActorInstanceId, transport.TransportEntityId,
                            transport.Target, [], removed));
                    }
                    continue;
                }

                transport.Phase = BattlefieldTransportPhase.Ascending;
                transport.FlightCounter = 0;
                transport.HeightRaw = transport.BaseHeightRaw;
                continue;
            }

            // Ascending state 22 uses the pre-increment counter and removes
            // itself when the stored value reaches 50.
            var ascentCounter = transport.FlightCounter;
            transport.FlightCounter++;
            if (transport.FlightCounter >= NativeTransportFlightTicks)
            {
                events.Add(new BattlefieldTransportEvent(BattlefieldTransportEventKind.Departed,
                    transport.InstanceId, transport.SourceActorInstanceId, transport.TransportEntityId,
                    transport.Target, [], []));
                battlefieldTransports.RemoveAt(index);
                continue;
            }
            transport.HeightRaw = transport.BaseHeightRaw + 3 * ascentCounter * ascentCounter;
        }
    }

    private void BeginTransportPursuit(BattlefieldTransportState transport, FixedPointPosition target)
    {
        var dx = target.XRaw - transport.Position.XRaw;
        var dz = target.ZRaw - transport.Position.ZRaw;
        transport.Facing.Face(NativeBearing.FromDelta(dx, dz));
        transport.IsTurning = true;
        var vector = NativeBearing.Vector(transport.Facing.Target);
        var speed = Math.Max(1, EntityDefinitionFor(transport.TransportEntityId).MovementSpeed);
        var projectedDistance = (vector.X * dx + vector.Z * dz) / NativeDirectionVector.Scale;
        transport.HorizontalExecutionsRemaining = Math.Max(0, projectedDistance / speed);
        transport.HorizontalVelocityXRaw = vector.X * speed / NativeDirectionVector.Scale;
        transport.HorizontalVelocityZRaw = vector.Z * speed / NativeDirectionVector.Scale;
        transport.PursuitTarget = target;
    }

    private uint NextTransportRandom()
    {
        transportRandomState = unchecked(transportRandomState * 214013 + 2531011);
        return transportRandomState >> 16;
    }

    private bool TrySpawnTransportPayload(int entityId, int teamId, CellCoordinate center, out int instanceId)
    {
        instanceId = 0;
        if ((uint)entityId >= (uint)entityDefinitions.Count) return false;
        var definition = EntityDefinitionFor(entityId);
        var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        var validator = new SpawnCellValidator(path, GroundOccupancy, AlternateOccupancy);
        var cell = validator.FindNearestValid(center, definition.MovementClass);
        if (cell is null) return false;
        instanceId = nextActorInstanceId++;
        if (!occupancy.TryClaim(instanceId, [cell.Value])) return false;
        var seed = new WorldEntity(instanceId, entityId, teamId, cell.Value,
            FixedPointPosition.AtCellCenter(cell.Value), 0, 0);
        var actor = new SimulatedActor(seed, definition);
        actors.Add(actor);
        actorsById.Add(instanceId, actor);
        return true;
    }

    private void RemoveActorFromWorld(SimulatedActor actor)
    {
        actor.Health = 0;
        actor.Playback?.Cancel();
        actor.Playback = null;
        actor.MoveOrder = null;
        actor.AttackTargetInstanceId = null;
        actor.AttackMoveDestination = null;
        actor.GroundSpecialAttackTarget = null;
        GroundOccupancy.Release(actor.Seed.InstanceId);
        AlternateOccupancy.Release(actor.Seed.InstanceId);
        MineOccupancy.Release(actor.Seed.InstanceId);
        foreach (var other in Actors.Where(other => other.AttackTargetInstanceId == actor.Seed.InstanceId))
            other.AttackTargetInstanceId = null;
    }

    private SimulatedActor? FindProjectileCollision(ProjectileState projectile)
    {
        if (!actorsById.TryGetValue(projectile.SourceActorInstanceId, out var source)) return null;
        var cell = projectile.Position.Cell;
        foreach (var occupancy in new[] { GroundOccupancy, AlternateOccupancy, MineOccupancy })
        {
            if (!occupancy.TryGetOwner(cell, out var candidateId) || candidateId == projectile.SourceActorInstanceId ||
                !actorsById.TryGetValue(candidateId, out var candidate) || candidate.IsDestroyed) continue;
            if (TeamRelations.IsHostile(source.Seed.Team, candidate.Seed.Team)) return candidate;
        }
        return null;
    }

    private void ApplyDamage(SimulatedActor target, int damage, ICollection<DestroyedActorEvent> destroyed)
    {
        target.Health = Math.Max(0, target.Health - damage);
        if (target.Health == 0) Destroy(target, destroyed);
    }

    private IReadOnlyList<AutonomousWanderEvent> UpdateAutonomousActors()
    {
        if ((simulationTicks & 7) != 0 || autonomousGroups.Count == 0) return [];
        var events = new List<AutonomousWanderEvent>();
        foreach (var group in autonomousGroups.OrderBy(group => group.Definition.GroupId))
        {
            foreach (var memberId in group.MemberIds.ToArray())
            {
                if (!actorsById.TryGetValue(memberId, out var actor) || actor.IsDestroyed || actor.Seed.Team != AutonomousSpawnSeeder.InternalNeutralTeam) continue;
                if ((NextAutonomousRandom() & 3) != 0) continue;
                CellCoordinate? target = null;
                // Native code retries out-of-map candidates. A bounded loop is
                // needed for a malformed/tiny map while preserving its formula.
                for (var attempt = 0; attempt < 32; attempt++)
                {
                    var candidate = new CellCoordinate(
                        group.Definition.Origin.X + (int)(NextAutonomousRandom() & 15) - 7,
                        group.Definition.Origin.Z + (int)(NextAutonomousRandom() & 15) - 7);
                    if ((uint)candidate.X < (uint)path.Width && (uint)candidate.Z < (uint)path.Height)
                    {
                        target = candidate;
                        break;
                    }
                }
                if (target is null) continue;
                actor.MoveOrder = new ActiveMoveOrder(target.Value);
                var result = StartSegment(actor);
                events.Add(new AutonomousWanderEvent(memberId, group.Definition.GroupId, target.Value, result.StepCount > 0));
            }

            if ((NextAutonomousRandom() & 0x7f) == 0) TryRespawnAutonomousMember(group);
        }
        return events;
    }

    private uint NextAutonomousRandom()
    {
        autonomousRandomState = unchecked(autonomousRandomState * 214013 + 2531011);
        return autonomousRandomState >> 16;
    }

    private void TryRespawnAutonomousMember(AutonomousGroupRuntime group)
    {
        var live = group.MemberIds.Where(id => actorsById.TryGetValue(id, out var actor) && !actor.IsDestroyed).ToArray();
        if (live.Length >= group.Definition.DesiredPopulation) return;
        group.MemberIds.RemoveAll(id => !actorsById.TryGetValue(id, out var actor) || actor.IsDestroyed);
        var definition = EntityDefinitionFor(group.Definition.EntityId);
        var validator = new SpawnCellValidator(path, GroundOccupancy, AlternateOccupancy);
        var cell = validator.FindNearestValid(group.Definition.Origin, definition.MovementClass);
        if (cell is null) return;
        var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        var instanceId = nextActorInstanceId++;
        if (!occupancy.TryClaim(instanceId, [cell.Value])) return;
        var seed = new WorldEntity(instanceId, group.Definition.EntityId, AutonomousSpawnSeeder.InternalNeutralTeam, cell.Value,
            FixedPointPosition.AtCellCenter(cell.Value), definition.Health, group.Definition.ScenarioFlag);
        actors.Add(new SimulatedActor(seed, definition));
        actorsById.Add(instanceId, actors[^1]);
        group.MemberIds.Add(instanceId);
    }

    private HarvesterDeploymentEvent RequestHarvesterDeployment(int entityInstanceId, int ventId)
    {
        if (!actorsById.TryGetValue(entityInstanceId, out var actor) || actor.IsDestroyed ||
            actor.Seed.Team < 0 || actor.Definition.Code is not ("EXPL" or "SLUG") ||
            (uint)ventId >= (uint)PetraVents.Count)
            return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.SourceInvalid);
        var vent = PetraVents[ventId];
        var cell = actor.Movement.OccupiedCell;
        if (vent.HarvesterInstanceId is { } other && other != entityInstanceId ||
            vent.PendingHarvesterInstanceId is { } pending && pending != entityInstanceId)
            return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.VentUnavailable);
        if (cell == vent.Position)
            return BeginHarvesterAttachment(actor, vent);

        var occupancy = actor.Definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        if (occupancy.TryGetOwner(vent.Position, out var occupant) && occupant != entityInstanceId)
            return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.NoApproach);
        DetachHarvester(actor);
        actor.AttackTargetInstanceId = null;
        actor.GroundSpecialAttackTarget = null;
        actor.HarvestVentId = ventId;
        actor.MoveOrder = new ActiveMoveOrder(vent.Position);
        var route = StartSegment(actor);
        if (route.StepCount == 0 && actor.Movement.OccupiedCell != vent.Position)
        {
            actor.HarvestVentId = null;
            actor.MoveOrder = null;
            return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.NoApproach);
        }
        return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.EnRoute);
    }

    private void UpdateHarvesterDeploymentOrders(ICollection<HarvesterDeploymentEvent> events)
    {
        foreach (var actor in Actors.Where(actor => !actor.IsDestroyed && actor.HarvestVentId is not null).OrderBy(actor => actor.Seed.InstanceId))
        {
            var ventId = actor.HarvestVentId!.Value;
            if ((uint)ventId >= (uint)PetraVents.Count)
            {
                actor.HarvestVentId = null;
                continue;
            }
            var vent = PetraVents[ventId];
            if (vent.HarvesterInstanceId == actor.Seed.InstanceId) continue;
            if (vent.HarvesterInstanceId is { } other && other != actor.Seed.InstanceId)
            {
                actor.HarvestVentId = null;
                continue;
            }
            if (actor.Movement.OccupiedCell != vent.Position) continue;
            if (vent.PendingHarvesterInstanceId is null)
            {
                events.Add(BeginHarvesterAttachment(actor, vent));
                continue;
            }
            if (vent.PendingHarvesterInstanceId != actor.Seed.InstanceId)
            {
                actor.HarvestVentId = null;
                continue;
            }
            // A request issued while already on the vent stores the native
            // 0x32 timer this step; countdown begins on the following step.
            if (events.Any(entry => entry.EntityInstanceId == actor.Seed.InstanceId &&
                    entry.VentId == vent.Id && entry.Outcome == HarvesterDeploymentOutcome.Preparing))
                continue;
            if (vent.AttachTicksRemaining > 0) vent.AttachTicksRemaining--;
            if (vent.AttachTicksRemaining == 0) events.Add(AttachHarvester(actor, vent));
        }
    }

    private static HarvesterDeploymentEvent BeginHarvesterAttachment(SimulatedActor actor, PetraVent vent)
    {
        actor.Playback?.Cancel();
        actor.Playback = null;
        actor.MoveOrder = null;
        actor.AttackTargetInstanceId = null;
        actor.AttackMoveDestination = null;
        actor.HarvestVentId = vent.Id;
        vent.PendingHarvesterInstanceId = actor.Seed.InstanceId;
        vent.AttachTicksRemaining = NativeHarvesterAttachTicks;
        return new HarvesterDeploymentEvent(actor.Seed.InstanceId, vent.Id, HarvesterDeploymentOutcome.Preparing);
    }

    private HarvesterDeploymentEvent AttachHarvester(SimulatedActor actor, PetraVent vent)
    {
        DetachHarvester(actor);
        actor.Playback?.Cancel();
        actor.Playback = null;
        actor.MoveOrder = null;
        actor.AttackTargetInstanceId = null;
        actor.AttackMoveDestination = null;
        actor.HarvestVentId = vent.Id;
        actor.DeployedEntityId = FindHarvesterDeployedForm(actor);
        if (actor.DeployedEntityId is { } deployedEntityId)
        {
            actor.MaximumHealth = EntityDefinitionFor(deployedEntityId).Health;
            actor.Health = Math.Min(actor.Health, actor.MaximumHealth);
        }
        vent.PendingHarvesterInstanceId = null;
        vent.AttachTicksRemaining = 0;
        vent.HarvesterInstanceId = actor.Seed.InstanceId;
        return new HarvesterDeploymentEvent(actor.Seed.InstanceId, vent.Id, HarvesterDeploymentOutcome.Attached);
    }

    private HarvesterDeploymentEvent RetractHarvester(RetractHarvesterIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var actor) || actor.IsDestroyed ||
            actor.Definition.Code is not ("EXPL" or "SLUG") || actor.HarvestVentId is not { } ventId ||
            EffectiveDefinition(actor).Code is not ("EDPLY" or "SDPL"))
            return new HarvesterDeploymentEvent(intent.EntityInstanceId, actor?.HarvestVentId ?? -1,
                HarvesterDeploymentOutcome.SourceInvalid);

        // dc.exe 0x417c40 maps EDPLY 47 -> EXPL 6 and SDPL 48 -> SLUG 14.
        // The actor and its exact-cell ground claim survive the form change.
        DetachHarvester(actor);
        actor.MaximumHealth = actor.Definition.Health;
        actor.Health = Math.Min(actor.Health, actor.MaximumHealth);
        return new HarvesterDeploymentEvent(actor.Seed.InstanceId, ventId, HarvesterDeploymentOutcome.Retracted);
    }

    private int? FindHarvesterDeployedForm(SimulatedActor actor)
    {
        var code = actor.Definition.Code switch
        {
            "EXPL" => "EDPLY",
            "SLUG" => "SDPL",
            _ => null,
        };
        if (code is null) return null;
        var form = entityDefinitions.FirstOrDefault(definition =>
            definition.Faction == actor.Definition.Faction && definition.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        return form?.Id;
    }

    private MineDeploymentEvent BeginMineDeployment(DeployMineIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source) || source.IsDestroyed ||
            source.Seed.Team < 0 || source.Definition.Code is not ("ENGI" or "SLOM") ||
            source.Definition.ImmediateSpecialCode == 0)
            return new MineDeploymentEvent(intent.EntityInstanceId, 0, 0, source?.Movement.OccupiedCell ?? default, MineDeploymentOutcome.SourceInvalid);
        var target = source.Movement.OccupiedCell;
        if (source.DeployedEntityId is not null)
            return new MineDeploymentEvent(intent.EntityInstanceId, source.Seed.InstanceId, source.DeployedEntityId.Value, target, MineDeploymentOutcome.AlreadyDeployed);
        var mine = entityDefinitions.FirstOrDefault(definition =>
            definition.Id == source.Definition.Id + 2 &&
            definition.Faction == source.Definition.Faction &&
            definition.Code == "HMINE");
        if (mine is null)
            return new MineDeploymentEvent(intent.EntityInstanceId, source.Seed.InstanceId, 0, target, MineDeploymentOutcome.EntityUnresolved);
        if (source.MineDeployTicksRemaining > 0)
            return new MineDeploymentEvent(intent.EntityInstanceId, source.Seed.InstanceId, mine.Id, target, MineDeploymentOutcome.Preparing);

        source.Playback?.Cancel();
        source.Playback = null;
        source.MoveOrder = null;
        source.AttackTargetInstanceId = null;
        source.GroundSpecialAttackTarget = null;
        source.AttackMoveDestination = null;
        source.MineDeployTicksRemaining = NativeImmediateSpecialTicks;
        return new MineDeploymentEvent(intent.EntityInstanceId, source.Seed.InstanceId, mine.Id, target, MineDeploymentOutcome.Preparing);
    }

    private MineDeploymentEvent CompleteMineDeployment(SimulatedActor source)
    {
        var target = source.Movement.OccupiedCell;
        // dc.exe 0x417d50 changes ENGI/SLOM (43/44) to entity ID +2
        // (HMINE 45/46), removes the old grid claim, and inserts the same actor
        // into the dedicated mine grid at its current 8.8 cell. This executes
        // only after state 13's recovered 50-tick timer.
        var mine = entityDefinitions.FirstOrDefault(definition =>
            definition.Id == source.Definition.Id + 2 &&
            definition.Faction == source.Definition.Faction &&
            definition.Code == "HMINE");
        if (mine is null)
            return new MineDeploymentEvent(source.Seed.InstanceId, source.Seed.InstanceId, 0, target, MineDeploymentOutcome.EntityUnresolved);
        if (!MineOccupancy.TryClaim(source.Seed.InstanceId, [target]))
            return new MineDeploymentEvent(source.Seed.InstanceId, source.Seed.InstanceId, mine.Id, target, MineDeploymentOutcome.Occupied);

        var previousOccupancy = source.Definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        previousOccupancy.Release(source.Seed.InstanceId);
        source.Playback?.Cancel();
        source.Playback = null;
        source.MoveOrder = null;
        source.AttackTargetInstanceId = null;
        source.AttackMoveDestination = null;
        source.DeployedEntityId = mine.Id;
        source.MaximumHealth = mine.Health;
        source.Health = Math.Min(source.Health, source.MaximumHealth);
        return new MineDeploymentEvent(source.Seed.InstanceId, source.Seed.InstanceId, mine.Id, target, MineDeploymentOutcome.Deployed);
    }

    private TowerDeploymentEvent DeployTower(DeployTowerIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source) || source.IsDestroyed || source.Seed.Team < 0 ||
            source.Definition.Code is not ("TURR" or "XENO"))
            return new TowerDeploymentEvent(intent.EntityInstanceId, 0, TowerDeploymentOutcome.SourceInvalid);
        if (source.DeployedEntityId is not null)
            return new TowerDeploymentEvent(intent.EntityInstanceId, source.DeployedEntityId.Value, TowerDeploymentOutcome.AlreadyDeployed);

        var deployedCode = source.Definition.Code == "TURR" ? "T" : "XDEPLOY";
        var form = entityDefinitions.FirstOrDefault(definition => definition.Faction == source.Definition.Faction && definition.Code == deployedCode);
        if (form is null)
            return new TowerDeploymentEvent(intent.EntityInstanceId, 0, TowerDeploymentOutcome.EntityUnresolved);

        // The paired gamestat forms turn movement speed from 15 to zero and
        // supply the actual tower weapon slots. Keep the existing actor and
        // ground claim: deployment is a state change, not a second building.
        source.Playback?.Cancel();
        source.Playback = null;
        source.MoveOrder = null;
        source.AttackMoveDestination = null;
        source.DeployedEntityId = form.Id;
        // The deployed record is the authoritative active form for combat
        // stats. Preserve live damage where possible, while preventing a
        // previously mobile tower from exceeding its static form's ceiling.
        source.MaximumHealth = form.Health;
        source.Health = Math.Min(source.Health, source.MaximumHealth);
        return new TowerDeploymentEvent(intent.EntityInstanceId, form.Id, TowerDeploymentOutcome.Deployed);
    }

    private StealDeploymentEvent DeploySteal(DeployStealIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source) || source.IsDestroyed || source.Seed.Team < 0 ||
            source.Definition.Code is not ("SARG" or "PSYC"))
            return new StealDeploymentEvent(intent.EntityInstanceId, 0, StealDeploymentOutcome.SourceInvalid);
        if (source.DeployedEntityId is not null)
            return new StealDeploymentEvent(intent.EntityInstanceId, source.DeployedEntityId.Value, StealDeploymentOutcome.AlreadyDeployed);

        var deployedCode = source.Definition.Code == "SARG" ? "SARGSTL" : "PSYCSTL";
        var form = entityDefinitions.FirstOrDefault(definition => definition.Faction == source.Definition.Faction && definition.Code == deployedCode);
        if (form is null)
            return new StealDeploymentEvent(intent.EntityInstanceId, 0, StealDeploymentOutcome.EntityUnresolved);

        // Source data proves the mobile->static transition; campaign scripts
        // additionally establish that this stance intercepts enemy-miner P7.
        source.Playback?.Cancel();
        source.Playback = null;
        source.MoveOrder = null;
        source.AttackTargetInstanceId = null;
        source.AttackMoveDestination = null;
        source.DeployedEntityId = form.Id;
        source.MaximumHealth = form.Health;
        source.Health = Math.Min(source.Health, source.MaximumHealth);
        return new StealDeploymentEvent(intent.EntityInstanceId, form.Id, StealDeploymentOutcome.Deployed);
    }

    private StealDeploymentEvent RetractSteal(RetractStealIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source) || source.IsDestroyed ||
            source.Definition.Code is not ("SARG" or "PSYC") ||
            EffectiveDefinition(source).Code is not ("SARGSTL" or "PSYCSTL"))
            return new StealDeploymentEvent(intent.EntityInstanceId, source?.DeployedEntityId ?? 0,
                StealDeploymentOutcome.SourceInvalid);

        // dc.exe 0x417c40 reverses entity 77 -> 4 and 78 -> 12 while retaining
        // the same actor and world-grid membership.
        source.DeployedEntityId = null;
        source.MaximumHealth = source.Definition.Health;
        source.Health = Math.Min(source.Health, source.MaximumHealth);
        source.AttackTargetInstanceId = null;
        source.AttackMoveDestination = null;
        return new StealDeploymentEvent(source.Seed.InstanceId, source.Definition.Id,
            StealDeploymentOutcome.Retracted);
    }

    private BuildingPlacedEvent PlaceBuilding(PlaceBuildingIntent intent)
    {
        if (dependencyCatalog is null || footprints is null)
            return new BuildingPlacedEvent(intent.TeamId, intent.DependencyItemId, 0, 0, intent.Origin, BuildingDropOutcome.CatalogUnavailable);
        if (!dependencyCatalog.TryGet(intent.DependencyItemId, out var item))
            return new BuildingPlacedEvent(intent.TeamId, intent.DependencyItemId, 0, 0, intent.Origin, BuildingDropOutcome.UnknownItem);
        if (!item.IsBuilding)
            return new BuildingPlacedEvent(intent.TeamId, intent.DependencyItemId, 0, 0, intent.Origin, BuildingDropOutcome.NotBuilding);
        if (!teamRaces.TryGetValue(intent.TeamId, out var teamRace) || teamRace != item.BuildingFaction)
            return new BuildingPlacedEvent(intent.TeamId, intent.DependencyItemId, 0, 0, intent.Origin, BuildingDropOutcome.WrongFaction);
        if (!teamEconomies.TryGetValue(intent.TeamId, out var economy) || !economy.ReservedItems.Contains(intent.DependencyItemId))
            return new BuildingPlacedEvent(intent.TeamId, intent.DependencyItemId, 0, 0, intent.Origin, BuildingDropOutcome.NotReserved);
        if (!footprints.TryResolveBuildingEntity(item.BuildingFaction!.Value, item.BuildingVariant!.Value, item.BuildingSlot!.Value, out var entityId))
            return new BuildingPlacedEvent(intent.TeamId, intent.DependencyItemId, 0, 0, intent.Origin, BuildingDropOutcome.EntityUnresolved);
        var claimedCells = footprints.OccupiedCells(entityId, intent.Origin);
        if (claimedCells.Count == 0)
            return new BuildingPlacedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.Origin, BuildingDropOutcome.InvalidFootprint);
        if (claimedCells.Any(cell => (uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height))
            return new BuildingPlacedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.Origin, BuildingDropOutcome.OutOfBounds);
        var instanceId = nextActorInstanceId;
        if (!GroundOccupancy.TryClaim(instanceId, claimedCells))
            return new BuildingPlacedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.Origin, BuildingDropOutcome.Occupied);
        if (!economy.MarkCompleted(dependencyCatalog, intent.DependencyItemId))
        {
            GroundOccupancy.Release(instanceId);
            return new BuildingPlacedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.Origin, BuildingDropOutcome.NotReserved);
        }
        var seed = new WorldEntity(instanceId, entityId, intent.TeamId, intent.Origin, FixedPointPosition.AtCellCenter(intent.Origin), 0, 0);
        var actor = new SimulatedActor(seed, EntityDefinitionFor(entityId));
        actors.Add(actor);
        actorsById.Add(instanceId, actor);
        nextActorInstanceId++;
        return new BuildingPlacedEvent(intent.TeamId, intent.DependencyItemId, instanceId, entityId, intent.Origin, BuildingDropOutcome.Placed);
    }

    private UnitProducedEvent ProduceUnit(ProduceUnitIntent intent)
    {
        if (dependencyCatalog is null || footprints is null)
            return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, 0, 0, intent.SourceBuildingInstanceId, UnitProductionOutcome.CatalogUnavailable);
        if (!dependencyCatalog.TryGet(intent.DependencyItemId, out var item))
            return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, 0, 0, intent.SourceBuildingInstanceId, UnitProductionOutcome.UnknownItem);
        if (!item.IsTroop || item.TroopEntityId is not { } entityId)
            return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, 0, 0, intent.SourceBuildingInstanceId, UnitProductionOutcome.NotTroop);
        if (!teamEconomies.TryGetValue(intent.TeamId, out var economy) || !economy.ReservedItems.Contains(intent.DependencyItemId))
            return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.SourceBuildingInstanceId, UnitProductionOutcome.NotReserved);
        if (!actorsById.TryGetValue(intent.SourceBuildingInstanceId, out var source) || source.IsDestroyed || source.Seed.Team != intent.TeamId || source.Definition.MovementSpeed > 0)
            return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.SourceBuildingInstanceId, UnitProductionOutcome.SourceInvalid);
        if (!item.PrerequisiteItemIds.Any(prerequisite => SourceMatchesBuildItem(source, prerequisite)))
            return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.SourceBuildingInstanceId, UnitProductionOutcome.PrerequisiteMissing);
        var definition = EntityDefinitionFor(entityId);
        var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        // A structure's SCN/build origin is its footprint anchor, not a unit
        // exit cell. Search outward from the actual decoded mask so a new unit
        // cannot materialize inside its producing structure. Keep occupancy
        // class-specific: alternate movers intentionally use their own grid.
        var sourceFootprint = footprints.OccupiedCells(source.Seed.EntityId, source.Seed.SpawnCell);
        var spawn = FindProductionSpawn(sourceFootprint.Count == 0 ? [source.Seed.SpawnCell] : sourceFootprint, occupancy);
        if (spawn is null)
            return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.SourceBuildingInstanceId, UnitProductionOutcome.SpawnBlocked);
        if (!economy.ConsumeReservation(dependencyCatalog, intent.DependencyItemId))
            return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.SourceBuildingInstanceId, UnitProductionOutcome.NotReserved);
        var instanceId = nextActorInstanceId++;
        occupancy.TryClaim(instanceId, [spawn.Value]);
        var seed = new WorldEntity(instanceId, entityId, intent.TeamId, spawn.Value, FixedPointPosition.AtCellCenter(spawn.Value), 0, 0);
        var actor = new SimulatedActor(seed, definition);
        actors.Add(actor);
        actorsById.Add(instanceId, actor);
        return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, instanceId, entityId, intent.SourceBuildingInstanceId, UnitProductionOutcome.Produced);
    }

    private ResearchCompletedEvent CompleteResearch(ResearchIntent intent)
    {
        if (dependencyCatalog is null || footprints is null)
            return new ResearchCompletedEvent(intent.TeamId, intent.DependencyItemId, intent.SourceBuildingInstanceId, ResearchOutcome.CatalogUnavailable);
        if (!dependencyCatalog.TryGet(intent.DependencyItemId, out var item))
            return new ResearchCompletedEvent(intent.TeamId, intent.DependencyItemId, intent.SourceBuildingInstanceId, ResearchOutcome.UnknownItem);
        if (!item.IsUpgrade)
            return new ResearchCompletedEvent(intent.TeamId, intent.DependencyItemId, intent.SourceBuildingInstanceId, ResearchOutcome.NotUpgrade);
        if (!teamEconomies.TryGetValue(intent.TeamId, out var economy) || !economy.ReservedItems.Contains(intent.DependencyItemId))
            return new ResearchCompletedEvent(intent.TeamId, intent.DependencyItemId, intent.SourceBuildingInstanceId, ResearchOutcome.NotReserved);
        if (!actorsById.TryGetValue(intent.SourceBuildingInstanceId, out var source) || source.IsDestroyed || source.Seed.Team != intent.TeamId || source.Definition.MovementSpeed > 0)
            return new ResearchCompletedEvent(intent.TeamId, intent.DependencyItemId, intent.SourceBuildingInstanceId, ResearchOutcome.SourceInvalid);
        if (!item.PrerequisiteItemIds.Any(prerequisite => SourceMatchesBuildItem(source, prerequisite)))
            return new ResearchCompletedEvent(intent.TeamId, intent.DependencyItemId, intent.SourceBuildingInstanceId, ResearchOutcome.PrerequisiteMissing);
        return new ResearchCompletedEvent(intent.TeamId, intent.DependencyItemId, intent.SourceBuildingInstanceId,
            economy.CompleteResearch(dependencyCatalog, intent.DependencyItemId) ? ResearchOutcome.Completed : ResearchOutcome.NotReserved);
    }

    private bool SourceMatchesBuildItem(SimulatedActor source, int dependencyItemId)
    {
        if (dependencyCatalog?.TryGet(dependencyItemId, out var prerequisite) != true || !prerequisite.IsBuilding || footprints is null) return false;
        return footprints.TryResolveBuildingEntity(prerequisite.BuildingFaction!.Value, prerequisite.BuildingVariant!.Value, prerequisite.BuildingSlot!.Value, out var entityId) &&
               entityId == source.Seed.EntityId;
    }

    private CellCoordinate? FindProductionSpawn(IReadOnlyList<CellCoordinate> sourceFootprint, CellOccupancy occupancy)
    {
        var minX = sourceFootprint.Min(cell => cell.X);
        var maxX = sourceFootprint.Max(cell => cell.X);
        var minZ = sourceFootprint.Min(cell => cell.Z);
        var maxZ = sourceFootprint.Max(cell => cell.Z);
        for (var radius = 1; radius <= 4; radius++)
        for (var z = minZ - radius; z <= maxZ + radius; z++)
        for (var x = minX - radius; x <= maxX + radius; x++)
        {
            var cell = new CellCoordinate(x, z);
            // This is a Chebyshev ring around every occupied source cell,
            // not around the SCN anchor. A single-cell footprint retains the
            // previous deterministic top-left-to-bottom-right ordering.
            var distance = sourceFootprint.Min(source => Math.Max(Math.Abs(cell.X - source.X), Math.Abs(cell.Z - source.Z)));
            if (distance != radius) continue;
            if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height || occupancy.IsOccupied(cell)) continue;
            return cell;
        }
        return null;
    }

    private EntityDefinition EntityDefinitionFor(int entityId)
    {
        // Existing actors retain their catalog definition. Resolve a newly
        // dropped building through its unambiguous catalog-backed peer.
        // The empty-world construction case is handled by the first actor's
        // definition only after ScenarioSimulation.Create has validated every
        // catalog ID, so keep a direct map instead of presentation state.
        return entityDefinitions[entityId];
    }

    private void DetachHarvester(SimulatedActor actor)
    {
        if (actor.Definition.Code is not ("EXPL" or "SLUG")) return;
        foreach (var vent in PetraVents.Where(vent =>
                     vent.HarvesterInstanceId == actor.Seed.InstanceId ||
                     vent.PendingHarvesterInstanceId == actor.Seed.InstanceId))
        {
            vent.HarvesterInstanceId = null;
            vent.PendingHarvesterInstanceId = null;
            vent.AttachTicksRemaining = 0;
        }
        actor.HarvestVentId = null;
        actor.DeployedEntityId = null;
    }

    private IReadOnlyList<P7IncomeEvent> ApplyPetraFlow()
    {
        LastP7Thefts = [];
        if (++petraPulseTicks < petraFlowRules.TicksPerPulse) return [];
        petraPulseTicks = 0;
        var income = new List<P7IncomeEvent>();
        var thefts = new List<P7TheftEvent>();
        foreach (var (teamId, economy) in teamEconomies.OrderBy(pair => pair.Key))
        {
            if (petraFlowRules.PassiveP7PerPulse > 0)
            {
                economy.AddP7(petraFlowRules.PassiveP7PerPulse);
                income.Add(new P7IncomeEvent(teamId, petraFlowRules.PassiveP7PerPulse, null));
            }
        }
        foreach (var vent in PetraVents.OrderBy(vent => vent.Id))
        {
            if (vent.HarvesterInstanceId is not { } harvesterId || !actorsById.TryGetValue(harvesterId, out var harvester) || harvester.IsDestroyed)
            {
                vent.HarvesterInstanceId = null;
                continue;
            }
            var cell = harvester.Movement.OccupiedCell;
            if (Math.Abs(cell.X - vent.Position.X) > 1 || Math.Abs(cell.Z - vent.Position.Z) > 1)
            {
                vent.HarvesterInstanceId = null;
                continue;
            }
            if (!teamEconomies.TryGetValue(harvester.Seed.Team, out var economy)) continue;
            var attachedIncome = petraFlowRules.AttachedP7PerPulse;
            var thief = FindPetraThief(harvester);
            var stolen = thief is null ? 0 : petraStealRules.StolenAmount(attachedIncome);
            var retained = attachedIncome - stolen;
            economy.AddP7(retained);
            if (retained > 0) income.Add(new P7IncomeEvent(harvester.Seed.Team, retained, vent.Id));
            if (thief is null || stolen <= 0 || !teamEconomies.TryGetValue(thief.Seed.Team, out var thiefEconomy)) continue;
            thiefEconomy.AddP7(stolen);
            income.Add(new P7IncomeEvent(thief.Seed.Team, stolen, vent.Id));
            thefts.Add(new P7TheftEvent(thief.Seed.InstanceId, harvester.Seed.InstanceId,
                thief.Seed.Team, harvester.Seed.Team, stolen, vent.Id));
        }
        LastP7Thefts = thefts;
        return income;
    }

    private SimulatedActor? FindPetraThief(SimulatedActor harvester)
    {
        var source = harvester.Movement.OccupiedCell;
        return Actors
            .Where(candidate => !candidate.IsDestroyed && candidate.Seed.Team >= 0 &&
                teamEconomies.ContainsKey(candidate.Seed.Team) &&
                TeamRelations.IsHostile(candidate.Seed.Team, harvester.Seed.Team) &&
                EffectiveDefinition(candidate).Code is "SARGSTL" or "PSYCSTL")
            .Select(candidate => new
            {
                Actor = candidate,
                Distance = Math.Max(
                    Math.Abs(candidate.Movement.OccupiedCell.X - source.X),
                    Math.Abs(candidate.Movement.OccupiedCell.Z - source.Z)),
            })
            .Where(candidate => candidate.Distance <= petraStealRules.MaximumCellDistance)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Actor.Seed.InstanceId)
            .Select(candidate => candidate.Actor)
            .FirstOrDefault();
    }

    private void Destroy(SimulatedActor actor, ICollection<DestroyedActorEvent> destroyed)
    {
        destroyed.Add(new DestroyedActorEvent(actor.Seed.InstanceId, actor.Seed.EntityId, actor.Movement.VisualPosition));
        actor.Playback?.Cancel();
        actor.Playback = null;
        actor.MoveOrder = null;
        actor.AttackTargetInstanceId = null;
        actor.AttackMoveDestination = null;
        GroundOccupancy.Release(actor.Seed.InstanceId);
        AlternateOccupancy.Release(actor.Seed.InstanceId);
        MineOccupancy.Release(actor.Seed.InstanceId);
        foreach (var other in Actors.Where(other => other.AttackTargetInstanceId == actor.Seed.InstanceId))
            other.AttackTargetInstanceId = null;
    }

    private AttackOrderEvent IssueAttackOrder(AttackIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var attacker))
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.SourceMissing);
        if (!actorsById.TryGetValue(intent.TargetEntityInstanceId, out var target))
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.TargetMissing);
        if (attacker.Seed.InstanceId == target.Seed.InstanceId)
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.SameActor);
        if (attacker.IsDestroyed)
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.SourceDestroyed);
        if (target.IsDestroyed)
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.TargetDestroyed);
        if (!TeamRelations.IsHostile(attacker.Seed.Team, target.Seed.Team))
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.NonHostile);
        if (!TryGetWeapon(attacker, out _))
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.Unarmed);
        attacker.AttackMoveDestination = null;
        attacker.MineDeployTicksRemaining = 0;
        attacker.AttackTargetInstanceId = target.Seed.InstanceId;
        attacker.GroundSpecialAttackTarget = null;
        return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.Acquired);
    }

    private GroundSpecialAttackEvent IssueGroundSpecialAttack(GroundSpecialAttackIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var actor))
            return new(intent.EntityInstanceId, intent.TargetCell, null, GroundSpecialAttackOutcome.SourceMissing);
        if (actor.IsDestroyed)
            return new(intent.EntityInstanceId, intent.TargetCell, null, GroundSpecialAttackOutcome.SourceDestroyed);
        var definition = EffectiveDefinition(actor);
        if (!definition.HasGroundSpecialAttack || !UnitSecondaryCommandCatalog.TryGet(definition, out var command) || command.CandidateEffectWeaponId != definition.GroundSpecialWeaponId)
            return new(intent.EntityInstanceId, intent.TargetCell, null, GroundSpecialAttackOutcome.Unsupported);
        if ((uint)intent.TargetCell.X >= (uint)path.Width || (uint)intent.TargetCell.Z >= (uint)path.Height)
            return new(intent.EntityInstanceId, intent.TargetCell, definition.GroundSpecialWeaponId, GroundSpecialAttackOutcome.InvalidTarget);
        if (command.RequiredResearchItemId is { } researchId &&
            (!teamEconomies.TryGetValue(actor.Seed.Team, out var economy) || !economy.CompletedItems.Contains(researchId)))
            return new(intent.EntityInstanceId, intent.TargetCell, definition.GroundSpecialWeaponId, GroundSpecialAttackOutcome.ResearchRequired);
        if (weaponCatalog?.TryGet(definition.GroundSpecialWeaponId, out _) != true)
            return new(intent.EntityInstanceId, intent.TargetCell, definition.GroundSpecialWeaponId, GroundSpecialAttackOutcome.WeaponUnavailable);

        actor.AttackTargetInstanceId = null;
        actor.AttackMoveDestination = null;
        actor.GroundSpecialAttackTarget = intent.TargetCell;
        actor.Playback?.Cancel();
        actor.Playback = null;
        actor.MoveOrder = null;
        return new(intent.EntityInstanceId, intent.TargetCell, definition.GroundSpecialWeaponId, GroundSpecialAttackOutcome.Accepted);
    }

    /// <summary>
    /// Executes the BEON/ZISP contextual area scan recovered at 0x413c20.
    /// Native code scans both ordinary occupancy layers in expanding
    /// Chebyshev squares through radius 7, admits the healer's exact team byte,
    /// and applies the class-7 amount recovered at 0x413e21.
    /// </summary>
    private IReadOnlyList<HealEvent> IssueAreaHeal(HealAreaIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source))
            return [new HealEvent(intent.EntityInstanceId, intent.EntityInstanceId, 0, HealOutcome.SourceMissing)];
        if (source.IsDestroyed)
            return [new HealEvent(intent.EntityInstanceId, intent.EntityInstanceId, 0, HealOutcome.SourceDestroyed)];
        if (source.Definition.Code is not ("BEON" or "ZISP"))
            return [new HealEvent(intent.EntityInstanceId, intent.EntityInstanceId, 0, HealOutcome.SourceNotHealer)];
        if (source.AbilityCharge < NativeHealMinimumCharge)
            return [new HealEvent(intent.EntityInstanceId, intent.EntityInstanceId, 0, HealOutcome.InsufficientCharge)];
        if (damageMatrix is null)
            return [new HealEvent(intent.EntityInstanceId, intent.EntityInstanceId, 0, HealOutcome.MatrixUnavailable)];

        var origin = source.Movement.OccupiedCell;
        // 0x413C8B..0x414066 scans every square from radius 0 through 7,
        // visiting ground then alternate occupancy at each X/Z. The first
        // successful heal clears source +0x0a, so the following loop guard
        // aborts instead of healing every damaged ally in the area.
        for (var radius = 0; radius <= 7; radius++)
        for (var x = origin.X - radius; x <= origin.X + radius; x++)
        for (var z = origin.Z - radius; z <= origin.Z + radius; z++)
        foreach (var occupancy in new[] { GroundOccupancy, AlternateOccupancy })
        {
            var cell = new CellCoordinate(x, z);
            if (!occupancy.TryGetOwner(cell, out var targetId) ||
                !actorsById.TryGetValue(targetId, out var target) || target.IsDestroyed ||
                target.Seed.Team != source.Seed.Team || target.Health >= target.MaximumHealth) continue;
            var resistance = damageMatrix[7, EffectiveDefinition(target).ArmorClass];
            var amount = Math.Min(target.MaximumHealth - target.Health, checked(36 * resistance / 256));
            if (amount <= 0) continue;
            target.Health += amount;
            source.AbilityCharge = 0;
            return [new HealEvent(source.Seed.InstanceId, target.Seed.InstanceId, amount, HealOutcome.Healed)];
        }
        return [new HealEvent(source.Seed.InstanceId, source.Seed.InstanceId, 0, HealOutcome.NoEligibleTargets)];
    }

    private AttackMoveOrderEvent IssueAttackMoveOrder(AttackMoveIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var actor))
            return new AttackMoveOrderEvent(intent.EntityInstanceId, intent.TargetCell, AttackMoveOrderOutcome.SourceMissing);
        if (actor.IsDestroyed)
            return new AttackMoveOrderEvent(intent.EntityInstanceId, intent.TargetCell, AttackMoveOrderOutcome.SourceDestroyed);
        if (EffectiveDefinition(actor).MovementSpeed <= 0 || !TryGetWeapon(actor, out _))
            return new AttackMoveOrderEvent(intent.EntityInstanceId, intent.TargetCell, AttackMoveOrderOutcome.Unarmed);
        if ((uint)intent.TargetCell.X >= (uint)path.Width || (uint)intent.TargetCell.Z >= (uint)path.Height)
            return new AttackMoveOrderEvent(intent.EntityInstanceId, intent.TargetCell, AttackMoveOrderOutcome.InvalidEndpoint);
        DetachHarvester(actor);
        actor.MineDeployTicksRemaining = 0;
        actor.AttackTargetInstanceId = null;
        actor.GroundSpecialAttackTarget = null;
        actor.AttackMoveDestination = intent.TargetCell;
        actor.MoveOrder = new ActiveMoveOrder(intent.TargetCell);
        _ = StartSegment(actor);
        return new AttackMoveOrderEvent(intent.EntityInstanceId, intent.TargetCell, AttackMoveOrderOutcome.Accepted);
    }

    private SimulatedActor? FindAttackMoveTarget(SimulatedActor actor)
    {
        // The source supplies distinct day/night observation columns. The
        // original acquisition radius is still untraced, so use that
        // data-backed visibility boundary rather than a presentation constant.
        var radiusRaw = (long)Math.Max(0, ObservationRange(actor)) * FixedPointPosition.One;
        return Actors
            .Where(candidate => !candidate.IsDestroyed && candidate.Seed.InstanceId != actor.Seed.InstanceId)
            .Where(candidate => TeamRelations.IsHostile(actor.Seed.Team, candidate.Seed.Team))
            .Where(candidate => DistanceSquared(actor.Movement.VisualPosition, candidate.Movement.VisualPosition) <= radiusRaw * radiusRaw)
            .OrderBy(candidate => DistanceSquared(actor.Movement.VisualPosition, candidate.Movement.VisualPosition))
            .ThenBy(candidate => candidate.Seed.InstanceId)
            .FirstOrDefault();
    }

    private SimulatedActor? FindMineTriggerTarget(SimulatedActor source, WeaponDefinition weapon)
    {
        SimulatedActor? best = null;
        var bestScore = -1;
        var rangeRaw = (long)Math.Max(0, weapon.Range) * FixedPointPosition.One;
        foreach (var offset in NativeMineTargetOffsets)
        {
            var cell = new CellCoordinate(
                source.Movement.OccupiedCell.X + offset.X,
                source.Movement.OccupiedCell.Z + offset.Z);
            if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height) continue;
            foreach (var occupancy in new[] { GroundOccupancy, AlternateOccupancy, MineOccupancy })
            {
                if (!occupancy.TryGetOwner(cell, out var candidateId) || candidateId == source.Seed.InstanceId ||
                    !actorsById.TryGetValue(candidateId, out var candidate) || candidate.IsDestroyed ||
                    candidate.Seed.Team is 8 or 9 ||
                    !TeamRelations.IsHostile(source.Seed.Team, candidate.Seed.Team) ||
                    DistanceSquared(source.Movement.VisualPosition, candidate.Movement.VisualPosition) > rangeRaw * rangeRaw)
                    continue;
                // Target search 0x435A06 rejects a weapon/armor pairing whose
                // mbullet entry is zero before assigning any priority.
                if (damageMatrix is not null &&
                    damageMatrix[weapon.WeaponClass, EffectiveDefinition(candidate).ArmorClass] == 0)
                    continue;

                // 0x435A4E starts an armed candidate at 150 and an unarmed one
                // at 50. For a boom weapon, 0x435A97 then inspects the ordinary
                // ground layer in the candidate's 3x3 neighborhood: hostile
                // occupants add 10 and cooperative occupants subtract 15.
                var score = TryGetWeapon(candidate, out _) ? 150 : 50;
                if (weapon.HasAreaEffect)
                {
                    for (var z = cell.Z - 1; z <= cell.Z + 1; z++)
                    for (var x = cell.X - 1; x <= cell.X + 1; x++)
                    {
                        var nearbyCell = new CellCoordinate(x, z);
                        if (!GroundOccupancy.TryGetOwner(nearbyCell, out var nearbyId) ||
                            !actorsById.TryGetValue(nearbyId, out var nearby) || nearby.IsDestroyed) continue;
                        score += TeamRelations.IsHostile(source.Seed.Team, nearby.Seed.Team) ? 10 : -15;
                    }
                }
                // Native replaces its result only for a strictly greater score,
                // preserving the fixed offset/layer order on ties.
                if (score <= bestScore) continue;
                best = candidate;
                bestScore = score;
            }
        }
        return best;
    }

    private static long DistanceSquared(FixedPointPosition left, FixedPointPosition right)
    {
        var dx = (long)right.XRaw - left.XRaw;
        var dz = (long)right.ZRaw - left.ZRaw;
        return dx * dx + dz * dz;
    }

    private bool TryGetWeapon(SimulatedActor actor, out WeaponDefinition weapon)
    {
        weapon = null!;
        if (weaponCatalog is null) return false;
        // Slot zero is the base weapon; upgrade levels one and two choose the
        // next two source slots. Fall back through lower populated slots for
        // entities whose table does not ship every level.
        var definition = EffectiveDefinition(actor);
        var requestedLevel = Math.Min(WeaponUpgradeLevel(actor), definition.WeaponSlots.Count - 1);
        for (var level = requestedLevel; level >= 0; level--)
        {
            var weaponId = definition.WeaponSlots[level];
            if (weaponId >= 0 && weaponCatalog.TryGet(weaponId, out weapon!)) return true;
        }
        return false;
    }

    private void UpdateGroundSpecialAttack(SimulatedActor actor, CellCoordinate target, ICollection<WeaponFireEvent> fired)
    {
        var definition = EffectiveDefinition(actor);
        if (weaponCatalog?.TryGet(definition.GroundSpecialWeaponId, out var weapon) != true)
        {
            actor.GroundSpecialAttackTarget = null;
            return;
        }
        var destination = FixedPointPosition.AtCellCenter(target);
        actor.Facing.FaceTowards(actor.Movement.VisualPosition, destination);
        var range = (long)weapon.Range * FixedPointPosition.One;
        if (DistanceSquared(actor.Movement.VisualPosition, destination) < range * range)
        {
            actor.Playback?.Cancel();
            actor.Playback = null;
            actor.MoveOrder = null;
            if (actor.CooldownTicks > 0 || actor.Facing.Current != actor.Facing.Target) return;
            SpawnGroundProjectile(actor, target, weapon, fired);
            actor.GroundSpecialAttackTarget = null;
            return;
        }
        if (definition.MovementSpeed <= 0 || actor.Playback is not null || actor.MoveOrder is not null) return;
        actor.MoveOrder = new ActiveMoveOrder(target);
        _ = StartSegment(actor);
    }

    private void PursueAttackTarget(SimulatedActor attacker, SimulatedActor target)
    {
        if (EffectiveDefinition(attacker).MovementSpeed <= 0 || !TryGetWeapon(attacker, out var weapon)) return;
        if (IsAttackTargetInRange(attacker))
        {
            // A previously issued approach can otherwise carry an attacker
            // past a target that has moved into weapon range.
            attacker.Playback?.Cancel();
            attacker.Playback = null;
            attacker.MoveOrder = null;
            return;
        }
        // Do not continually replace the same chase segment as logical cells
        // advance ahead of interpolation. Re-evaluate after arrival or if the
        // target moves beyond range again.
        if (attacker.Playback is not null || attacker.MoveOrder is not null) return;
        var approach = FindAttackApproachCell(attacker, target, weapon);
        if (approach is null) return;
        attacker.MoveOrder = new ActiveMoveOrder(approach.Value);
        _ = StartSegment(attacker);
    }

    private CellCoordinate? FindAttackApproachCell(SimulatedActor attacker, SimulatedActor target, WeaponDefinition weapon)
    {
        var occupancy = EffectiveDefinition(attacker).MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        var maxRadius = Math.Max(1, weapon.Range);
        CellCoordinate? best = null;
        var bestDistance = long.MaxValue;
        for (var radius = 1; radius <= maxRadius; radius++)
        for (var z = -radius; z <= radius; z++)
        for (var x = -radius; x <= radius; x++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius) continue;
            var cell = new CellCoordinate(target.Movement.OccupiedCell.X + x, target.Movement.OccupiedCell.Z + z);
            if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height || occupancy.IsOccupied(cell)) continue;
            var dx = (long)x * FixedPointPosition.One;
            var dz = (long)z * FixedPointPosition.One;
            var range = (long)weapon.Range * FixedPointPosition.One;
            if (dx * dx + dz * dz >= range * range) continue;
            var sourceDx = cell.X - attacker.Movement.OccupiedCell.X;
            var sourceDz = cell.Z - attacker.Movement.OccupiedCell.Z;
            var distance = (long)sourceDx * sourceDx + (long)sourceDz * sourceDz;
            if (distance >= bestDistance) continue;
            best = cell;
            bestDistance = distance;
        }
        return best;
    }

    private void SpawnProjectile(SimulatedActor attacker, SimulatedActor target, WeaponDefinition weapon, ICollection<WeaponFireEvent> fired)
    {
        var source = attacker.Movement.VisualPosition;
        var destination = ApplyNativeAimOffset(attacker, target.Movement.VisualPosition, weapon);
        var dx = (long)destination.XRaw - source.XRaw;
        var dz = (long)destination.ZRaw - source.ZRaw;
        var distance = Math.Max(1L, (long)Math.Sqrt(dx * dx + dz * dz));
        var speed = Math.Max(1, weapon.ProjectileSpeed);
        var velocityX = (int)(dx * speed / distance);
        var velocityZ = (int)(dz * speed / distance);
        var ticks = CalculateTrajectoryUpdates(dx, dz, velocityX, velocityZ);
        var damage = damageMatrix is null ? weapon.Damage : damageMatrix.CalculateBaseDamage(weapon.Damage, weapon.WeaponClass, EffectiveDefinition(target).ArmorClass);
        CellCoordinate? timedImpactCell = weapon.HasAreaEffect ? destination.Cell : null;
        projectiles.Add(new ProjectileState(nextProjectileInstanceId++, attacker.Seed.InstanceId, target.Seed.InstanceId,
            weapon.Id, Math.Max(0, damage), source, velocityX, velocityZ, ticks, weapon.ProjectileLifetimeTicks,
            timedImpactCell: timedImpactCell, projectileMode: weapon.ProjectileMode));
        // dc.exe 0x413181 reads the weapon's burst limit (+0x20), increments
        // actor byte +0x34, and substitutes reload (+0x24) only after the
        // final burst shot. Normal shots use the rate field (+0x08). Keep this
        // in authoritative simulation state; a renderer must not decide fire
        // cadence from an animation length.
        ApplyWeaponCooldown(attacker, weapon);
        fired.Add(new WeaponFireEvent(attacker.Seed.InstanceId, weapon.Id, NextFirePresentationRoll()));
    }

    private void SpawnGroundProjectile(SimulatedActor attacker, CellCoordinate target, WeaponDefinition weapon, ICollection<WeaponFireEvent> fired)
    {
        var source = attacker.Movement.VisualPosition;
        var destination = ApplyNativeAimOffset(attacker, FixedPointPosition.AtCellCenter(target), weapon);
        var dx = (long)destination.XRaw - source.XRaw;
        var dz = (long)destination.ZRaw - source.ZRaw;
        var distance = Math.Max(1L, (long)Math.Sqrt(dx * dx + dz * dz));
        var speed = Math.Max(1, weapon.ProjectileSpeed);
        var velocityX = (int)(dx * speed / distance);
        var velocityZ = (int)(dz * speed / distance);
        var ticks = CalculateTrajectoryUpdates(dx, dz, velocityX, velocityZ);
        projectiles.Add(new ProjectileState(nextProjectileInstanceId++, attacker.Seed.InstanceId, -1,
            weapon.Id, Math.Max(0, weapon.Damage), source, velocityX, velocityZ, ticks, weapon.ProjectileLifetimeTicks,
            target, destination.Cell, weapon.ProjectileMode));
        ApplyWeaponCooldown(attacker, weapon);
        fired.Add(new WeaponFireEvent(attacker.Seed.InstanceId, weapon.Id, NextFirePresentationRoll()));
    }

    private byte NextFirePresentationRoll()
    {
        // Native 0x412e13 consumes the game's shared 256-entry random stream,
        // then applies modulo entity.fireVariantCount. Keep the roll in the
        // deterministic event while the rest of that global stream is not yet
        // reconstructed into the port.
        firePresentationRandomState = unchecked(firePresentationRandomState * 214013 + 2531011);
        return (byte)(firePresentationRandomState >> 16);
    }

    private FixedPointPosition ApplyNativeAimOffset(
        SimulatedActor attacker,
        FixedPointPosition target,
        WeaponDefinition weapon)
    {
        if (!weapon.HasAreaEffect || areaEffects?.TryGet(weapon.AreaEffectTemplateId, out var effect) != true)
            return target;
        if (attacker.InspirationTicksRemaining > 0)
            return target;

        var remaining = NextAimRoll();
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            // Loader 0x43b59f stores floor(percent * 256 / 100). Common fire
            // walks the nine entries row-major, subtracting weights until the
            // current entry exceeds the shared-random byte.
            var weight = effect.AimWeights[row][column] * FixedPointPosition.One / 100;
            if (weight > remaining)
                return target.AddRaw((column - 1) * FixedPointPosition.One, (row - 1) * FixedPointPosition.One);
            remaining -= weight;
        }
        // Percent-to-8.8 flooring can leave up to a few unassigned byte
        // values. Native exits with row/column == 3, yielding (+2,+2).
        return target.AddRaw(2 * FixedPointPosition.One, 2 * FixedPointPosition.One);
    }

    private int NextAimRoll()
    {
        // Native uses another value from its shared 256-entry stream. Keep
        // this stream separate from presentation selection for replayability.
        aimRandomState = unchecked(aimRandomState * 214013 + 2531011);
        return (byte)(aimRandomState >> 16);
    }

    private static int CalculateTrajectoryUpdates(long dx, long dz, int velocityX, int velocityZ)
    {
        // 0x413004-0x413054 divides the dominant signed delta by its matching
        // velocity component. A nonzero boom template stores this count at
        // projectile +0x18 and decrements it once per projectile substep.
        if (Math.Abs(velocityX) >= Math.Abs(velocityZ))
            return velocityX == 0 ? 1 : Math.Max(1, checked((int)Math.Abs(dx / velocityX)));
        return velocityZ == 0 ? 1 : Math.Max(1, checked((int)Math.Abs(dz / velocityZ)));
    }

    private static void ApplyWeaponCooldown(SimulatedActor attacker, WeaponDefinition weapon)
    {
        attacker.CooldownTicks = Math.Max(1, weapon.RateOfFire);
        if (weapon.BurstShotLimit > 0 && ++attacker.BurstShotCount >= weapon.BurstShotLimit)
        {
            attacker.BurstShotCount = 0;
            attacker.CooldownTicks = Math.Max(1, weapon.BurstReloadTicks);
        }
    }

    private MoveCommandOutcome StartSegment(SimulatedActor actor)
    {
        var order = actor.MoveOrder ?? throw new InvalidOperationException("Actor has no move order.");
        if (actor.Movement.OccupiedCell == order.Target)
        {
            actor.MoveOrder = null;
            return new MoveCommandOutcome(actor.Seed.InstanceId, order.Target, DiagnosticPathTermination.ReachedTarget, 0);
        }

        var finder = new DiagnosticLocalPathfinder(path, GroundOccupancy, AlternateOccupancy);
        var definition = EffectiveDefinition(actor);
        var local = finder.Find(actor.Movement.OccupiedCell, order.Target, definition.MovementClass, actor.Seed.InstanceId);
        if (local.Steps.Count == 0)
        {
            // A route failure is not a completed order. Keep it active and
            // retry at the recovered four-execution blocked cadence.
            order.BlockedTicksRemaining = 4;
            return new MoveCommandOutcome(actor.Seed.InstanceId, order.Target, local.Termination, 0);
        }

        var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        actor.Playback = new PackedPathPlayback(actor.Seed.InstanceId, definition.MovementSpeed, actor.Movement, local.Steps, occupancy, actor.Facing);
        order.SegmentCount++;
        return new MoveCommandOutcome(actor.Seed.InstanceId, order.Target, local.Termination, local.Steps.Count);
    }
}
