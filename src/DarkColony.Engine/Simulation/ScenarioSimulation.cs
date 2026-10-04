using DarkColony.Engine.Commands;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Missions;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>Authoritative local mission state; presentation only reads it.</summary>
public sealed partial class ScenarioSimulation
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
    /// <summary>Ring count of the steal victim search <c>0x417944</c>.</summary>
    public const int NativeStealSearchRings = 11;
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
    private uint inspireRandomState = 0x494e5350; // "INSP"
    private uint transportRandomState = 0x5452414e; // "TRAN"
    private int nextActorInstanceId;
    private int nextTransportInstanceId = 1;
    private readonly List<SimulatedActor> actors;
    private readonly List<BattlefieldTransportState> battlefieldTransports = [];
    private readonly List<AutonomousGroupRuntime> autonomousGroups = [];
    private ulong simulationTicks;
    // Autonomous schedule masks are recovered, but their call-site sequence
    // is still kept independently deterministic.
    private uint autonomousRandomState = 0x4d435254;
    // dc.exe increments 0x479204 (initialized to zero) then reads one dword
    // from the initialized 256-entry stream at 0x478e04.
    private int nativeRandomIndex;
    private NativeRandomTable nativeRandomTable = NativeRandomTable.Synthetic;
    // Reused for every route segment; it keeps per-search scratch buffers.
    private DiagnosticLocalPathfinder? localPathfinder;
    // Native target-search rings (0x434090); null disables automatic target
    // selection, as in checks built without the executable.
    private NativeTargetRings? targetRings;
    // Teams whose SCN %AI profile is nonzero (native player flag +0xbbc).
    private HashSet<int> computerTeams = [];
    // Per-tick team visibility snapshots used by the target selector.
    // City building actor per (team, slot), created from %AISlots/%City.
    private readonly Dictionary<(int Team, int Slot), int> cityBuildings = [];
    // True when the SCN declares cities (%AISlots); P7 income then requires a
    // live slot-0 headquarters, as player +0xBD4 gates it natively.
    private bool citiesDeclared;
    // City origin (%AISlots line 2) per team that has a city.
    private readonly Dictionary<int, CellCoordinate> cityOrigins = [];
    // Player troop queues, ordered by (team, queue).
    private readonly SortedDictionary<(int Team, int Queue), CityProductionQueue> productionQueues = [];
    private TroopBuildTimings? buildTimings;
    // Sum of the SCN critter groups' desired populations (0x43FE6C).
    private int critterReserve;
    // Mission runtime (trigger.c): compiled script, trigger table, statistics.
    private MissionScript? missionScript;
    private readonly MissionTrigger?[] missionTriggers = new MissionTrigger?[MissionScript.TriggerSlots];
    private readonly byte[] missionLives = new byte[MissionScript.TriggerSlots];
    private readonly int[,] playerStats = new int[8, PlayerStatCount];
    private readonly int[,,] typeStats = new int[8, TypeStatEntities, 4];
    // Player +0x19B4: passive P7 per 16 ticks, set to 3 by the SCN loader and by exomoney.
    private readonly int[] passiveRates = [3, 3, 3, 3, 3, 3, 3, 3];
    private readonly List<MissionMessageEvent> pendingMissionMessages = [];
    private readonly List<MissionUnmodeledActionEvent> pendingUnmodeledMissionActions = [];

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
    public IReadOnlyList<IdleAcquisitionEvent> LastIdleAcquisitions { get; private set; } = [];
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

    /// <summary>Completed authoritative steps since construction.</summary>
    public ulong TickCount => simulationTicks;

    /// <summary>Creates a simulation with every installed rule table, as the game host does.</summary>
    public static ScenarioSimulation Create(ScenarioDefinition scenario, PathRegionMap path, SimulationRules rules,
        MissionScript? missionScript = null, TerrainMap? terrain = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return Create(scenario, rules.Entities, path, rules.Footprints,
            missionScript: missionScript,
            terrain: terrain,
            visionTrees: rules.VisionTrees,
            weaponCatalog: rules.Weapons,
            damageMatrix: rules.DamageMatrix,
            dependencyCatalog: rules.Dependencies,
            areaEffects: rules.AreaEffects,
            randomTable: rules.RandomTable,
            targetRings: rules.TargetRings,
            buildTimings: rules.BuildTimings);
    }

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
        AreaEffectCatalog? areaEffects = null,
        NativeRandomTable? randomTable = null,
        NativeTargetRings? targetRings = null,
        TroopBuildTimings? buildTimings = null,
        MissionScript? missionScript = null,
        TerrainMap? terrain = null,
        NativeVisionTrees? visionTrees = null)
    {
        var teamRaces = scenario.Teams.Where(team => team.Race is not null).ToDictionary(team => team.TeamId, team => team.Race!.Value);
        var seeds = scenario.Placements.Where(placement => placement.Team != -1).Select((placement, index) =>
        {
            var cell = new CellCoordinate(placement.X, placement.Z);
            return new WorldEntity(index + 1, NativePlacementEntity(placement, teamRaces, catalog), placement.Team, cell,
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

        // Cities precede critter groups so nature spawns see their footprints.
        var cityBuildings = SeedCityBuildings(scenario, catalog, footprints, ground, seeds);
        var autonomous = AutonomousSpawnSeeder.Seed(
            scenario.AutonomousSpawnGroups, catalog, path, ground, alternate, seeds.Count + 1);
        seeds.AddRange(autonomous.Entities);
        var actors = seeds.Select(seed => new SimulatedActor(seed, catalog[seed.EntityId])).ToArray();
        foreach (var city in cityBuildings)
        {
            // City actors keep the slot's exact native position and %City health.
            var actor = actors[city.InstanceId - 1];
            actor.Movement.AdvanceVisual(city.Position.XRaw - actor.Movement.VisualPosition.XRaw,
                city.Position.ZRaw - actor.Movement.VisualPosition.ZRaw);
            actor.Health = city.Health;
        }
        var resources = scenario.Teams.Where(team => team.Enabled)
            .ToDictionary(team => team.TeamId, team => Math.Max(0, team.StartingResource ?? 0));
        var races = scenario.Teams.Where(team => team.Enabled)
            .ToDictionary(team => team.TeamId, team => team.Race);
        var simulation = new ScenarioSimulation(path, catalog, actors, ground, alternate, mine, weaponCatalog, damageMatrix, areaEffects, resources, races, dependencyCatalog,
            petraFlowRules ?? PetraFlowRules.ProvisionalDefault,
            dayNight ?? (scenario.DayNight.IsNativeValid ? DayNightCycle.FromNativeScenario(scenario.DayNight) : new DayNightCycle()), footprints,
            teamRelations ?? TeamRelationMatrix.CreateDefault());
        if (!simulation.petraFlowRules.IsValid) throw new ArgumentOutOfRangeException(nameof(petraFlowRules));
        simulation.nativeRandomTable = randomTable ?? NativeRandomTable.Synthetic;
        simulation.targetRings = targetRings;
        simulation.visionTrees = visionTrees ?? NativeVisionTrees.Flat;
        simulation.LoadTerrainSight(terrain);
        simulation.computerTeams = scenario.Teams.Where(team => team.Enabled && team.AiProfile > 0).Select(team => team.TeamId).ToHashSet();
        simulation.citiesDeclared = scenario.Teams.Any(team => team.CityOrigin is not null);
        foreach (var city in cityBuildings) simulation.cityBuildings[(city.Team, city.Slot)] = city.InstanceId;
        simulation.buildTimings = buildTimings;
        simulation.critterReserve = scenario.AutonomousSpawnGroups.Sum(group => group.DesiredPopulation);
        // Session start 0x40123C/0x401848: players 1-6 stat 0 hold the lobby
        // options; this build never changes their defaults (vent rate and
        // vent money multipliers 4 << 6 = 256, the rest 0).
        simulation.playerStats[1, 0] = 256;
        simulation.playerStats[2, 0] = 256;
        // 0x41BDFD counts each team's starting money as earned P7 (stat 1).
        foreach (var (team, resource) in resources) simulation.RecordP7Earned(team, resource);
        simulation.InitializeMission(missionScript);
        foreach (var team in scenario.Teams.Where(team => team.Enabled && team.TeamId is >= 0 and < 8 && team.HasCity))
        {
            simulation.cityOrigins[team.TeamId] = team.CityOrigin!.Value;
            for (var queue = 0; queue < BuildingFootprintCatalog.ProductionQueueCount; queue++)
                simulation.productionQueues[(team.TeamId, queue)] = new CityProductionQueue(team.TeamId, queue);
        }
        simulation.SeedScenarioBuildingDependencies();
        foreach (var (team, slot) in simulation.cityBuildings.Keys.ToArray()) simulation.SyncCitySlotItems(team, slot);
        simulation.PetraVents = scenario.Vents.Select((vent, index) => new PetraVent(index, new CellCoordinate(vent.X, vent.Z),
            PetraFlowRules.NativeSigned8_8Multiply(vent.InitialState, simulation.PlayerStatistic(1, 0)),
            PetraFlowRules.NativeSigned8_8Multiply(vent.InitialReservoir, simulation.PlayerStatistic(2, 0)))).ToArray();
        simulation.autonomousGroups.AddRange(scenario.AutonomousSpawnGroups.Select(group => new AutonomousGroupRuntime(
            group, autonomous.EntitiesByGroup.GetValueOrDefault(group.GroupId, []).Select(entity => entity.InstanceId))));
        return simulation;
    }

    /// <summary>
    /// The SCN loader (<c>0x41C4E3</c>) swaps a player placement whose entity
    /// race (gamestat value 1, runtime <c>+4</c>) differs from the player's race
    /// (<c>+0xBB8</c>) for the entity's counterpart (value 31, <c>+0x114</c>),
    /// when it has one.
    /// </summary>
    private static int NativePlacementEntity(ScenarioPlacement placement, IReadOnlyDictionary<int, int> teamRaces, EntityCatalog catalog)
    {
        if (placement.Team is < 0 or >= 8 || !teamRaces.TryGetValue(placement.Team, out var race) ||
            (uint)placement.EntityId >= (uint)catalog.Entities.Count) return placement.EntityId;
        var definition = catalog[placement.EntityId];
        return definition.Faction != race && definition.FactionCounterpartEntityId != -1
            ? definition.FactionCounterpartEntityId
            : placement.EntityId;
    }

    public SimulatedActor? Actor(int instanceId) => actorsById.GetValueOrDefault(instanceId);

    /// <summary>
    /// Construction calls this after a delivered building becomes live. It is
    /// separate from <see cref="PurchaseIntent"/> so a paid pedestal order does
    /// not unlock its dependents early.
    /// </summary>
    public bool MarkDependencyBuildingCompleted(int teamId, int dependencyItemId) =>
        teamEconomies.TryGetValue(teamId, out var economy) && economy.MarkCompleted(dependencyCatalog, dependencyItemId);

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

    /// <summary>
    /// One world update in the order of <c>0x4196F4</c>. Its caller
    /// (<c>0x41E1F9</c>) increments the update counter <c>world + 0x94C</c> first,
    /// and the update increments the clock <c>+0x52C</c> before anything reads
    /// it, so both equal <see cref="TickCount"/> + 1 during the step. The
    /// order is: statistics recount, troop cap, day/night (the phase counter
    /// <c>+0x530</c>), critter groups and norm triggers every 8 updates, passive
    /// income every 16, then the actors (vents among them) and projectiles.
    /// </summary>
    public void Step(IEnumerable<ScheduledWorldCommand> commands)
    {
        var update = WorldUpdateCounter;
        RecountMissionStatistics();
        TroopCap = ComputeTroopCap();
        pendingMissionMessages.Clear();
        pendingUnmodeledMissionActions.Clear();
        // 0x41988C stamps visibility while the clock is still zero.
        EnsureVision();
        LastDayNightChanges = DayNight.Step() ? [new DayNightChangedEvent(DayNight.Phase)] : [];
        // 0x419A30: after the day/night update, every 16 updates.
        if ((update & 15) == 0) RefreshVision();
        var nativeIncomeCadence = petraFlowRules.TicksPerPulse == PetraFlowRules.NativeHarvesterPulseTicks;
        var syntheticPulse = !nativeIncomeCadence && SyntheticPetraPulse();
        LastAutonomousWanders = (update & 7) == 0 ? UpdateAutonomousActors() : [];
        RunNormTriggers(pendingMissionMessages, pendingUnmodeledMissionActions);
        var passiveIncome = ApplyPassiveIncome(nativeIncomeCadence ? (update & 15) == 0 : syntheticPulse);
        var events = new TickEvents(UpdateInspireState());
        UpdateAbilityCharge();
        events.MineDeployments.AddRange(UpdateMineDeploymentState());
        events.StealDeployments.AddRange(UpdateStealStances());
        UpdateBattlefieldTransports(events.BattlefieldTransports);
        DispatchCommands(commands, events);
        LastMoveOutcomes = events.MoveOutcomes;
        LastAttackOrders = events.Attacks;
        LastGroundSpecialAttacks = events.GroundSpecialAttacks;
        LastHeals = events.Heals;
        LastInspires = events.Inspires;
        LastAttackMoveOrders = events.AttackMoves;
        LastPurchaseReservations = events.Purchases;
        LastHarvesterDeployments = events.HarvesterDeployments;
        LastMineDeployments = events.MineDeployments;
        LastTowerDeployments = events.TowerDeployments;
        LastStealDeployments = events.StealDeployments;
        LastBuildingPlacements = events.BuildingPlacements;
        LastUnitProductions = events.UnitProductions;
        LastResearchCompletions = events.ResearchCompletions;

        UpdateActors(events);
        UpdateHarvesterDeploymentOrders(events.HarvesterDeployments);
        // The vent producer (0x4139D7) runs in the vents' own actor updates,
        // after their attach handshake, gated on the phase counter +0x530.
        var ventIncome = ApplyVentIncome(nativeIncomeCadence ? (DayNight.PhaseTicks & 15) == 0 : syntheticPulse);
        FireMines(events);
        FireAttackers(events);
        UpdateProjectiles(events);
        LastDestroyedActors = events.Destroyed;
        LastWeaponFires = events.Fired;
        LastProjectileImpacts = events.Impacts;
        LastBattlefieldTransports = events.BattlefieldTransports;
        LastAttackMoveAcquisitions = events.AttackMoveAcquisitions;
        LastIdleAcquisitions = events.IdleAcquisitions;
        LastMissionMessages = pendingMissionMessages.ToArray();
        LastUnmodeledMissionActions = pendingUnmodeledMissionActions.ToArray();
        LastP7Income = [.. passiveIncome, .. ventIncome];
        simulationTicks++;
    }

    /// <summary>Native <c>world + 0x94C</c> (and <c>+0x52C</c>) during the current or next step.</summary>
    private ulong WorldUpdateCounter => simulationTicks + 1;

    /// <summary>Event lists filled during one <see cref="Step"/> and then published.</summary>
    private sealed class TickEvents(List<InspireEvent> inspires)
    {
        public List<InspireEvent> Inspires { get; } = inspires;
        public List<MoveCommandOutcome> MoveOutcomes { get; } = [];
        public List<DestroyedActorEvent> Destroyed { get; } = [];
        public List<WeaponFireEvent> Fired { get; } = [];
        public List<ProjectileImpactEvent> Impacts { get; } = [];
        public List<BattlefieldTransportEvent> BattlefieldTransports { get; } = [];
        public List<HealEvent> Heals { get; } = [];
        public List<AttackOrderEvent> Attacks { get; } = [];
        public List<GroundSpecialAttackEvent> GroundSpecialAttacks { get; } = [];
        public List<AttackMoveOrderEvent> AttackMoves { get; } = [];
        public List<AttackMoveAcquisitionEvent> AttackMoveAcquisitions { get; } = [];
        public List<IdleAcquisitionEvent> IdleAcquisitions { get; } = [];
        public List<PurchaseReservedEvent> Purchases { get; } = [];
        public List<HarvesterDeploymentEvent> HarvesterDeployments { get; } = [];
        public List<MineDeploymentEvent> MineDeployments { get; } = [];
        public List<TowerDeploymentEvent> TowerDeployments { get; } = [];
        public List<StealDeploymentEvent> StealDeployments { get; } = [];
        public List<BuildingPlacedEvent> BuildingPlacements { get; } = [];
        public List<UnitProducedEvent> UnitProductions { get; } = [];
        public List<ResearchCompletedEvent> ResearchCompletions { get; } = [];
    }

    /// <summary>Applies this tick's player and system commands in sequence order.</summary>
    private void DispatchCommands(IEnumerable<ScheduledWorldCommand> commands, TickEvents events)
    {
        var outcomes = events.MoveOutcomes;
        var heals = events.Heals;
        var attacks = events.Attacks;
        var groundSpecialAttacks = events.GroundSpecialAttacks;
        var attackMoves = events.AttackMoves;
        var purchases = events.Purchases;
        var harvesterDeployments = events.HarvesterDeployments;
        var mineDeployments = events.MineDeployments;
        var towerDeployments = events.TowerDeployments;
        var stealDeployments = events.StealDeployments;
        var buildingPlacements = events.BuildingPlacements;
        var unitProductions = events.UnitProductions;
        var researchCompletions = events.ResearchCompletions;
        var inspires = events.Inspires;
        foreach (var scheduled in commands.OrderBy(command => command.Sequence))
        {
            if (scheduled.Command is PurchaseIntent purchase)
            {
                var eligibility = !teamEconomies.TryGetValue(purchase.TeamId, out var economy)
                    ? PurchaseEligibility.UnknownItem
                    : !UsesPortConstructionAdapters() && !HasCity(purchase.TeamId)
                        ? PurchaseEligibility.NoCity
                        : economy.TryReserve(dependencyCatalog, purchase.DependencyItemId);
                purchases.Add(new PurchaseReservedEvent(purchase.TeamId, purchase.DependencyItemId, eligibility));
                if (eligibility == PurchaseEligibility.Available && BuildPurchasedCitySlot(purchase) is { } built)
                    buildingPlacements.Add(built);
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

            // A normal move replaces—not appends to—the active command. Its
            // existing packed segment may already have reserved the next
            // cell, so restore that claim before planning from the actor's
            // authoritative source cell. Leaving it active makes the actor
            // visibly complete one old segment before following the new order.
            actor.Playback?.Cancel();
            actor.Playback = null;
            DetachHarvester(actor);
            actor.MineDeployTicksRemaining = 0;
            actor.AttackTargetInstanceId = null;
            actor.AttackMoveDestination = null;
            actor.GroundSpecialAttackTarget = null;
            actor.MoveOrder = new ActiveMoveOrder(move.TargetCell);
            outcomes.Add(StartSegment(actor));
        }
    }

    /// <summary>Per-actor targeting, attack-move acquisition, facing, cooldown, and path playback.</summary>
    private void UpdateActors(TickEvents events)
    {
        var fired = events.Fired;
        var attackMoveAcquisitions = events.AttackMoveAcquisitions;
        var harvesterDeployments = events.HarvesterDeployments;
        UpdateCityProduction(events);
        foreach (var actor in actors.ToArray())
        {
            if (actor.IsDestroyed) continue;
            UpdateIdleCommand(actor, events);
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
                var hostile = targetRings is null ? FindAttackMoveTarget(actor) : FindNativeAttackMoveTarget(actor);
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
            // The idle fidget turns its actor itself, once per update.
            if (actor.IdleFidgetFacing is null && actor.Facing.Current != actor.Facing.Target && EffectiveDefinition(actor).TurnSpeed > 0)
                actor.Facing.Step(EffectiveDefinition(actor).TurnSpeed);
            if (actor.CooldownTicks > 0) actor.CooldownTicks--;
            StopMoveOnContact(actor);
            if (actor.Playback is null && actor.MoveOrder is { BlockedWaiting: true } blocked)
            {
                // The blocked step's type-3 wait (0x4122C8): a health change
                // retries the kept steps in this update; otherwise the counter
                // runs down, and the retry steps in the update after it ends.
                if (actor.Health == blocked.BlockedWaitHealth)
                {
                    if (blocked.BlockedTicksRemaining > 0) blocked.BlockedTicksRemaining--;
                    else ResumeKeptSteps(actor, blocked);
                    continue;
                }
                ResumeKeptSteps(actor, blocked);
            }
            if (actor.Playback is not null)
            {
                var status = actor.Playback.Step();
                if (status == PackedPathPlaybackStatus.Blocked)
                {
                    actor.MoveOrder ??= new ActiveMoveOrder(actor.Movement.OccupiedCell);
                    actor.MoveOrder.LastBlockedCell = actor.Playback.BlockedCell;
                    var remaining = actor.Playback.RemainingSteps();
                    actor.Playback = null;
                    HandleBlockedStep(actor, remaining);
                }
                else if (status == PackedPathPlaybackStatus.ReservedStep)
                {
                    // 0x415E6E: entering a cell runs its trip trigger with this unit.
                    RunTripTrigger(actor, actor.Movement.ReservedDestination);
                }
                else if (actor.Playback.CompletedTransitionLastStep)
                {
                    // Command-5 clears the notified blocker's +0x35 byte as
                    // its interpolation completes.
                    actor.YieldNotificationDirection = null;
                }
                else if (status == PackedPathPlaybackStatus.Complete)
                {
                    actor.Playback = null;
                    if (actor.MoveOrder?.Target == actor.Movement.OccupiedCell &&
                        TryBeginHarvesterAttachmentOnVentArrival(actor, harvesterDeployments))
                        continue;
                    if (actor.MoveOrder?.Target == actor.Movement.OccupiedCell && !actor.MoveOrder.AdvanceWaypoint())
                        actor.MoveOrder = null;
                }
            }

            // The blocked-step wait runs above; this counter is the port's
            // retry after a failed route.
            if (actor.Playback is not null || actor.MoveOrder is null || actor.MoveOrder.BlockedWaiting) continue;
            if (actor.MoveOrder.BlockedTicksRemaining > 0)
            {
                actor.MoveOrder.BlockedTicksRemaining--;
                continue;
            }
            _ = StartSegment(actor);
        }
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

}
