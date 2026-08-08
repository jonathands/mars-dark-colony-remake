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
    /// <summary>Player destination retained while attack-move pursues hostiles.</summary>
    public CellCoordinate? AttackMoveDestination { get; internal set; }
    /// <summary>
    /// Native actor byte <c>+0x34</c>: shots fired in the active weapon burst.
    /// It resets when the weapon's decoded burst limit is reached.
    /// </summary>
    public int BurstShotCount { get; internal set; }
    public int CooldownTicks { get; internal set; }
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
public sealed record WeaponFireEvent(int SourceActorInstanceId, int WeaponId);
/// <summary>Resolved impact identity, retaining the firing actor for owner-keyed sound feedback.</summary>
public sealed record ProjectileImpactEvent(int SourceActorInstanceId, int TargetActorInstanceId, int WeaponId, int WeaponClass);
public enum HealOutcome { Healed, SourceMissing, TargetMissing, SourceDestroyed, TargetDestroyed, SourceNotHealer, NonCooperative, OutOfRange, TargetAtFullHealth, MatrixUnavailable }
public sealed record HealEvent(int SourceActorInstanceId, int TargetActorInstanceId, int Amount, HealOutcome Outcome);
public enum AttackOrderOutcome { Acquired, SourceMissing, TargetMissing, SameActor, SourceDestroyed, TargetDestroyed, Unarmed, NonHostile }
public sealed record AttackOrderEvent(int SourceActorInstanceId, int TargetActorInstanceId, AttackOrderOutcome Outcome);
public enum AttackMoveOrderOutcome { Accepted, SourceMissing, SourceDestroyed, Unarmed, InvalidEndpoint }
public sealed record AttackMoveOrderEvent(int SourceActorInstanceId, CellCoordinate Target, AttackMoveOrderOutcome Outcome);
public sealed record AttackMoveAcquisitionEvent(int SourceActorInstanceId, int TargetActorInstanceId);
public sealed record PurchaseReservedEvent(int TeamId, int DependencyItemId, PurchaseEligibility Eligibility);
public enum HarvesterDeploymentOutcome { Attached, EnRoute, SourceInvalid, VentUnavailable, NoApproach }
public sealed record HarvesterDeploymentEvent(int EntityInstanceId, int VentId, HarvesterDeploymentOutcome Outcome)
{
    public bool Attached => Outcome == HarvesterDeploymentOutcome.Attached;
}
public enum MineDeploymentOutcome { Deployed, SourceInvalid, EntityUnresolved, OutOfBounds, Occupied }
public sealed record MineDeploymentEvent(int SourceActorInstanceId, int EntityInstanceId, int EntityId, CellCoordinate Target, MineDeploymentOutcome Outcome);
public enum TowerDeploymentOutcome { Deployed, SourceInvalid, AlreadyDeployed, EntityUnresolved }
public sealed record TowerDeploymentEvent(int EntityInstanceId, int EntityId, TowerDeploymentOutcome Outcome);
public enum StealDeploymentOutcome { Deployed, SourceInvalid, AlreadyDeployed, EntityUnresolved }
/// <summary>State-form transition only; the native transfer/target rule remains untraced.</summary>
public sealed record StealDeploymentEvent(int EntityInstanceId, int EntityId, StealDeploymentOutcome Outcome);
public sealed record P7IncomeEvent(int TeamId, int Amount, int? VentId);
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
    private int petraPulseTicks;
    private int nextProjectileInstanceId = 1;
    private int nextActorInstanceId;
    private readonly List<SimulatedActor> actors;
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
        WeaponCatalog? weaponCatalog,
        DamageMatrix? damageMatrix,
        AreaEffectCatalog? areaEffects,
        IReadOnlyDictionary<int, int> teamResources,
        IReadOnlyDictionary<int, int?> teamRaces,
        DependencyCatalog? dependencyCatalog,
        PetraFlowRules petraFlowRules,
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
        this.weaponCatalog = weaponCatalog;
        this.damageMatrix = damageMatrix;
        this.areaEffects = areaEffects;
        teamEconomies = teamResources.ToDictionary(pair => pair.Key, pair => new TeamEconomy(pair.Value));
        this.teamRaces = teamRaces.ToDictionary(pair => pair.Key, pair => pair.Value);
        this.dependencyCatalog = dependencyCatalog;
        this.petraFlowRules = petraFlowRules;
        DayNight = dayNight;
        this.footprints = footprints;
        TeamRelations = teamRelations;
        nextActorInstanceId = actorsById.Count == 0 ? 1 : actorsById.Keys.Max() + 1;
    }

    public IReadOnlyList<SimulatedActor> Actors { get; }
    public CellOccupancy GroundOccupancy { get; }
    public CellOccupancy AlternateOccupancy { get; }
    public IReadOnlyList<MoveCommandOutcome> LastMoveOutcomes { get; private set; } = [];
    public IReadOnlyList<ProjectileState> Projectiles => projectiles;
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
    public IReadOnlyList<AttackOrderEvent> LastAttackOrders { get; private set; } = [];
    public IReadOnlyList<AttackMoveOrderEvent> LastAttackMoveOrders { get; private set; } = [];
    public IReadOnlyList<AttackMoveAcquisitionEvent> LastAttackMoveAcquisitions { get; private set; } = [];
    public IReadOnlyList<PurchaseReservedEvent> LastPurchaseReservations { get; private set; } = [];
    public IReadOnlyList<HarvesterDeploymentEvent> LastHarvesterDeployments { get; private set; } = [];
    public IReadOnlyList<MineDeploymentEvent> LastMineDeployments { get; private set; } = [];
    public IReadOnlyList<TowerDeploymentEvent> LastTowerDeployments { get; private set; } = [];
    public IReadOnlyList<StealDeploymentEvent> LastStealDeployments { get; private set; } = [];
    public IReadOnlyList<P7IncomeEvent> LastP7Income { get; private set; } = [];
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
        foreach (var seed in seeds)
        {
            if ((uint)seed.EntityId >= (uint)catalog.Entities.Count)
                throw new InvalidDataException($"SCN entity ID {seed.EntityId} is outside gamestat.txt.");
            var footprint = footprints?.OccupiedCells(seed.EntityId, seed.SpawnCell) ?? [];
            var definition = catalog[seed.EntityId];
            if (footprint.Count == 0 && definition.MovementSpeed <= 0) continue;
            var occupancy = footprint.Count != 0 || definition.MovementClass == 0 ? ground : alternate;
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
        var simulation = new ScenarioSimulation(path, catalog, actors, ground, alternate, weaponCatalog, damageMatrix, areaEffects, resources, races, dependencyCatalog,
            petraFlowRules ?? PetraFlowRules.ProvisionalDefault, dayNight ?? new DayNightCycle(), footprints,
            teamRelations ?? TeamRelationMatrix.CreateDefault());
        if (!simulation.petraFlowRules.IsValid) throw new ArgumentOutOfRangeException(nameof(petraFlowRules));
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

    /// <summary>Returns an actor's current shipped form, including a deployed mobile tower.</summary>
    public EntityDefinition EffectiveDefinition(SimulatedActor actor) => actor.DeployedEntityId is { } entityId && actor.HarvestVentId is null
        ? EntityDefinitionFor(entityId)
        : actor.Definition;

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
        var outcomes = new List<MoveCommandOutcome>();
        var destroyed = new List<DestroyedActorEvent>();
        var fired = new List<WeaponFireEvent>();
        var impacts = new List<ProjectileImpactEvent>();
        var heals = new List<HealEvent>();
        var attacks = new List<AttackOrderEvent>();
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
            if (scheduled.Command is DeployMineIntent mine)
            {
                mineDeployments.Add(DeployMine(mine));
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
                stoppedActor.AttackMoveDestination = null;
                DetachHarvester(stoppedActor);
                continue;
            }
            if (scheduled.Command is AttackIntent attack)
            {
                attacks.Add(IssueAttackOrder(attack));
                continue;
            }
            if (scheduled.Command is HealIntent heal)
            {
                heals.Add(IssueHeal(heal));
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
            actor.AttackMoveDestination = null;
            actor.MoveOrder = new ActiveMoveOrder(move.TargetCell);
            outcomes.Add(StartSegment(actor));
        }
        LastMoveOutcomes = outcomes;
        LastAttackOrders = attacks;
        LastHeals = heals;
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
        // shipped weapon catalog. Their native target-scan cadence is still
        // open, so use one deterministic nearest-hostile scan per simulation
        // step and feed the recovered weapon through normal projectile damage.
        foreach (var mine in Actors.Where(actor => !actor.IsDestroyed && actor.Definition.Code == "HMINE").OrderBy(actor => actor.Seed.InstanceId))
        {
            if (!TryGetWeapon(mine, out var weapon)) continue;
            var target = FindHostileWithinWeaponRange(mine, weapon);
            if (target is null) continue;
            SpawnProjectile(mine, target, weapon, fired);
            mine.Health = 0;
            Destroy(mine, destroyed);
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
            if (!projectile.Step()) continue;
            projectiles.RemoveAt(index);
            if (!actorsById.TryGetValue(projectile.TargetActorInstanceId, out var target) || target.Health <= 0) continue;
            WeaponDefinition? weapon = null;
            var weaponClass = -1;
            if (weaponCatalog?.TryGet(projectile.WeaponId, out var resolvedWeapon) == true)
            {
                weapon = resolvedWeapon;
                weaponClass = weapon.WeaponClass;
            }
            if (weaponClass >= 0) impacts.Add(new ProjectileImpactEvent(projectile.SourceActorInstanceId, target.Seed.InstanceId, projectile.WeaponId, weaponClass));
            if (weapon is not null && areaEffects?.TryGet(weapon.AreaEffectTemplateId, out var effect) == true)
                ApplyAreaDamage(projectile, weapon, target, effect, destroyed);
            else ApplyDamage(target, projectile.Damage, destroyed);
        }
        LastDestroyedActors = destroyed;
        LastWeaponFires = fired;
        LastProjectileImpacts = impacts;
        LastAttackMoveAcquisitions = attackMoveAcquisitions;
        LastDayNightChanges = DayNight.Step() ? [new DayNightChangedEvent(DayNight.Phase)] : [];
        LastP7Income = ApplyPetraFlow();
        simulationTicks++;
    }

    private void ApplyAreaDamage(ProjectileState projectile, WeaponDefinition weapon, SimulatedActor center,
        AreaEffectTemplate effect, ICollection<DestroyedActorEvent> destroyed)
    {
        if (!actorsById.TryGetValue(projectile.SourceActorInstanceId, out var source)) return;
        var radius = effect.PatternSize / 2;
        foreach (var actor in Actors.Where(actor => !actor.IsDestroyed && TeamRelations.IsHostile(source.Seed.Team, actor.Seed.Team)).OrderBy(actor => actor.Seed.InstanceId))
        {
            var dx = actor.Movement.OccupiedCell.X - center.Movement.OccupiedCell.X;
            var dz = actor.Movement.OccupiedCell.Z - center.Movement.OccupiedCell.Z;
            if (Math.Abs(dx) > radius || Math.Abs(dz) > radius) continue;
            var percent = effect.DamagePattern[dz + radius][dx + radius];
            if (percent <= 0) continue;
            var baseDamage = damageMatrix is null ? weapon.Damage : damageMatrix.CalculateBaseDamage(weapon.Damage, weapon.WeaponClass, EffectiveDefinition(actor).ArmorClass);
            ApplyDamage(actor, checked(baseDamage * percent / 100), destroyed);
        }
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
        if (vent.HarvesterInstanceId is { } other && other != entityInstanceId)
            return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.VentUnavailable);
        if (IsNextToVent(cell, vent))
            return AttachHarvester(actor, vent);

        var approach = FindHarvesterApproachCell(actor, vent);
        if (approach is null)
            return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.NoApproach);
        DetachHarvester(actor);
        actor.AttackTargetInstanceId = null;
        actor.HarvestVentId = ventId;
        actor.MoveOrder = new ActiveMoveOrder(approach.Value);
        _ = StartSegment(actor);
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
            if (!IsNextToVent(actor.Movement.OccupiedCell, vent)) continue;
            events.Add(AttachHarvester(actor, vent));
        }
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
        vent.HarvesterInstanceId = actor.Seed.InstanceId;
        return new HarvesterDeploymentEvent(actor.Seed.InstanceId, vent.Id, HarvesterDeploymentOutcome.Attached);
    }

    private static bool IsNextToVent(CellCoordinate cell, PetraVent vent) =>
        Math.Abs(cell.X - vent.Position.X) <= 1 && Math.Abs(cell.Z - vent.Position.Z) <= 1;

    private CellCoordinate? FindHarvesterApproachCell(SimulatedActor actor, PetraVent vent)
    {
        var occupancy = actor.Definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        CellCoordinate? best = null;
        var bestDistance = long.MaxValue;
        for (var z = -1; z <= 1; z++)
        for (var x = -1; x <= 1; x++)
        {
            if (x == 0 && z == 0) continue;
            var candidate = new CellCoordinate(vent.Position.X + x, vent.Position.Z + z);
            if ((uint)candidate.X >= (uint)path.Width || (uint)candidate.Z >= (uint)path.Height ||
                (occupancy.IsOccupied(candidate) && candidate != actor.Movement.OccupiedCell)) continue;
            var dx = candidate.X - actor.Movement.OccupiedCell.X;
            var dz = candidate.Z - actor.Movement.OccupiedCell.Z;
            var distance = (long)dx * dx + (long)dz * dz;
            if (distance >= bestDistance) continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
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

    private MineDeploymentEvent DeployMine(DeployMineIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source) || source.IsDestroyed ||
            source.Seed.Team < 0 || source.Definition.Code is not ("ENGI" or "SLOM"))
            return new MineDeploymentEvent(intent.EntityInstanceId, 0, 0, intent.TargetCell, MineDeploymentOutcome.SourceInvalid);
        if ((uint)intent.TargetCell.X >= (uint)path.Width || (uint)intent.TargetCell.Z >= (uint)path.Height)
            return new MineDeploymentEvent(intent.EntityInstanceId, 0, 0, intent.TargetCell, MineDeploymentOutcome.OutOfBounds);
        var mine = entityDefinitions.FirstOrDefault(definition => definition.Faction == source.Definition.Faction && definition.Code == "HMINE");
        if (mine is null)
            return new MineDeploymentEvent(intent.EntityInstanceId, 0, 0, intent.TargetCell, MineDeploymentOutcome.EntityUnresolved);
        var instanceId = nextActorInstanceId;
        if (!GroundOccupancy.TryClaim(instanceId, [intent.TargetCell]))
            return new MineDeploymentEvent(intent.EntityInstanceId, 0, mine.Id, intent.TargetCell, MineDeploymentOutcome.Occupied);
        var seed = new WorldEntity(instanceId, mine.Id, source.Seed.Team, intent.TargetCell,
            FixedPointPosition.AtCellCenter(intent.TargetCell), 0, 0);
        var actor = new SimulatedActor(seed, mine);
        actors.Add(actor);
        actorsById.Add(instanceId, actor);
        nextActorInstanceId++;
        return new MineDeploymentEvent(intent.EntityInstanceId, instanceId, mine.Id, intent.TargetCell, MineDeploymentOutcome.Deployed);
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

        // The source data proves the mobile->static transition (including the
        // DPY sound and DEPLOY FIN family). It does not prove which victim,
        // range, or transfer pulse turns this stance into stolen P7, so this
        // command intentionally owns only the recovered form change.
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
        foreach (var vent in PetraVents.Where(vent => vent.HarvesterInstanceId == actor.Seed.InstanceId))
            vent.HarvesterInstanceId = null;
        actor.HarvestVentId = null;
        actor.DeployedEntityId = null;
    }

    private IReadOnlyList<P7IncomeEvent> ApplyPetraFlow()
    {
        if (++petraPulseTicks < petraFlowRules.TicksPerPulse) return [];
        petraPulseTicks = 0;
        var income = new List<P7IncomeEvent>();
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
            economy.AddP7(petraFlowRules.AttachedP7PerPulse);
            income.Add(new P7IncomeEvent(harvester.Seed.Team, petraFlowRules.AttachedP7PerPulse, vent.Id));
        }
        return income;
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
        attacker.AttackTargetInstanceId = target.Seed.InstanceId;
        return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.Acquired);
    }

    /// <summary>
    /// Executes the native class-7 healing amount against a cooperative target.
    /// The original collision routine at 0x413e21 reads row 7 of mbullet and
    /// restores floor(36 * resistance / 256), capped by missing health. It does
    /// not establish the command's target range or cadence; the current range
    /// uses the healer's decoded live observation radius as a port policy.
    /// </summary>
    private HealEvent IssueHeal(HealIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source))
            return new HealEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, 0, HealOutcome.SourceMissing);
        if (!actorsById.TryGetValue(intent.TargetEntityInstanceId, out var target))
            return new HealEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, 0, HealOutcome.TargetMissing);
        if (source.IsDestroyed) return new HealEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, 0, HealOutcome.SourceDestroyed);
        if (target.IsDestroyed) return new HealEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, 0, HealOutcome.TargetDestroyed);
        if (source.Definition.Code is not ("BEON" or "ZISP"))
            return new HealEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, 0, HealOutcome.SourceNotHealer);
        if (TeamRelations.IsHostile(source.Seed.Team, target.Seed.Team))
            return new HealEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, 0, HealOutcome.NonCooperative);
        var range = (long)Math.Max(1, ObservationRange(source)) * FixedPointPosition.One;
        var dx = (long)source.Movement.VisualPosition.XRaw - target.Movement.VisualPosition.XRaw;
        var dz = (long)source.Movement.VisualPosition.ZRaw - target.Movement.VisualPosition.ZRaw;
        if (dx * dx + dz * dz >= range * range)
            return new HealEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, 0, HealOutcome.OutOfRange);
        if (target.Health >= target.MaximumHealth)
            return new HealEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, 0, HealOutcome.TargetAtFullHealth);
        if (damageMatrix is null)
            return new HealEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, 0, HealOutcome.MatrixUnavailable);
        var resistance = damageMatrix[7, EffectiveDefinition(target).ArmorClass];
        var amount = Math.Min(target.MaximumHealth - target.Health, checked(36 * resistance / 256));
        if (amount <= 0) return new HealEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, 0, HealOutcome.MatrixUnavailable);
        target.Health += amount;
        source.Facing.FaceTowards(source.Movement.VisualPosition, target.Movement.VisualPosition);
        return new HealEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, amount, HealOutcome.Healed);
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
        actor.AttackTargetInstanceId = null;
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

    private SimulatedActor? FindHostileWithinWeaponRange(SimulatedActor source, WeaponDefinition weapon)
    {
        var rangeRaw = (long)Math.Max(0, weapon.Range) * FixedPointPosition.One;
        return Actors
            .Where(candidate => !candidate.IsDestroyed && candidate.Seed.InstanceId != source.Seed.InstanceId)
            .Where(candidate => TeamRelations.IsHostile(source.Seed.Team, candidate.Seed.Team))
            // Ordinary weapon fire is a strict range test. A static range-1
            // mine cannot ever see an adjacent cell centre through that test,
            // so this provisional proximity adapter includes the contact
            // boundary until its native mine-specific scan is recovered.
            .Where(candidate => DistanceSquared(source.Movement.VisualPosition, candidate.Movement.VisualPosition) <= rangeRaw * rangeRaw)
            .OrderBy(candidate => DistanceSquared(source.Movement.VisualPosition, candidate.Movement.VisualPosition))
            .ThenBy(candidate => candidate.Seed.InstanceId)
            .FirstOrDefault();
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
        var destination = target.Movement.VisualPosition;
        var dx = (long)destination.XRaw - source.XRaw;
        var dz = (long)destination.ZRaw - source.ZRaw;
        var distance = Math.Max(1L, (long)Math.Sqrt(dx * dx + dz * dz));
        // The original uses the weapon's speed field to construct a vector.
        // This duration is a documented provisional linear interpolation until
        // the projectile record's update/lifetime routine is traced.
        var speed = Math.Max(1, weapon.ProjectileSpeed);
        var velocityX = (int)(dx * speed / distance);
        var velocityZ = (int)(dz * speed / distance);
        var ticks = Math.Max(1, (int)((distance + speed - 1) / speed));
        var damage = damageMatrix is null ? weapon.Damage : damageMatrix.CalculateBaseDamage(weapon.Damage, weapon.WeaponClass, EffectiveDefinition(target).ArmorClass);
        projectiles.Add(new ProjectileState(nextProjectileInstanceId++, attacker.Seed.InstanceId, target.Seed.InstanceId,
            weapon.Id, Math.Max(0, damage), source, velocityX, velocityZ, ticks));
        // dc.exe 0x413181 reads the weapon's burst limit (+0x20), increments
        // actor byte +0x34, and substitutes reload (+0x24) only after the
        // final burst shot. Normal shots use the rate field (+0x08). Keep this
        // in authoritative simulation state; a renderer must not decide fire
        // cadence from an animation length.
        attacker.CooldownTicks = Math.Max(1, weapon.RateOfFire);
        if (weapon.Shots > 0 && ++attacker.BurstShotCount >= weapon.Shots)
        {
            attacker.BurstShotCount = 0;
            attacker.CooldownTicks = Math.Max(1, weapon.Reload);
        }
        fired.Add(new WeaponFireEvent(attacker.Seed.InstanceId, weapon.Id));
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
