using DarkColony.Engine.Commands;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.World;

/// <summary>
/// Exercises one race's whole build tree in a War city: every building is
/// bought, every troop trained and every research completed, in prerequisite
/// order. Each trained unit then moves, attacks a hostile unit, uses its
/// special, and dies; so does every other mobile entity of the race, spawned
/// directly. Invariants are checked after every update.
/// </summary>
/// <remarks>
/// A behavior the sweep finds missing but whose native rule is not yet
/// recovered is listed in <see cref="KnownGaps"/> with its reason. The sweep
/// reports it without failing, and fails once it no longer occurs, so the list
/// shrinks as the port catches up.
/// </remarks>
internal sealed class CatalogSweep
{
    private const string MeleeGap =
        "a range-1 weapon never fires: the port fires only inside the strict 8.8 range, while dc.exe fires at any " +
        "hostile its idle ring scan (0x435C14, rings up to the weapon range) finds; see docs/reverse-engineering/combat-range.md";
    private const string GroundAttackGap =
        "capability-3 GROUND ATTACK names weapon 0, which weapstat.txt does not define; its native owner is unverified " +
        "(docs/reverse-engineering/unit-special-commands.md)";

    /// <summary>Gaps the sweep expects, by race (0 Human, 1 Gray) and key.</summary>
    public static readonly IReadOnlyDictionary<(int Race, string Key), string> KnownGaps = new Dictionary<(int, string), string>
    {
        [(0, "melee GRND#79")] = MeleeGap,
        [(0, "melee AIRD#80")] = MeleeGap,
        [(0, "melee DROA#103")] = MeleeGap,
        [(0, "melee SHRI#104")] = MeleeGap,
        [(1, "melee SALY#23")] = MeleeGap,
        [(1, "melee AVII#24")] = MeleeGap,
        [(1, "melee RNAT#25")] = MeleeGap,
        [(1, "melee SPID#26")] = MeleeGap,
        [(1, "melee GRUB#36")] = MeleeGap,
        [(0, "ground attack BARR#3")] = GroundAttackGap,
        [(1, "ground attack ATRIL#11")] = GroundAttackGap,
    };

    public const string Scenario = "mplayer/j4play01";
    public const int Team = 0;
    public const int Enemy = 1;
    private const int MoveDistance = 8;

    private readonly ScenarioSimulation simulation;
    private readonly SimulationRules rules;
    private readonly PathRegionMap path;
    private readonly int race;
    private readonly HashSet<string> invariantFailures = [];
    private readonly HashSet<string> observedGaps = [];
    private readonly Dictionary<int, int> trainedByEntity = [];

    private CatalogSweep(ScenarioSimulation simulation, SimulationRules rules, PathRegionMap path, int race)
    {
        this.simulation = simulation;
        this.rules = rules;
        this.path = path;
        this.race = race;
    }

    public List<string> Failures { get; } = [];
    public List<string> Report { get; } = [];
    public List<string> Gaps { get; } = [];
    public int Buildings { get; private set; }
    public int Troops { get; private set; }
    public int Research { get; private set; }
    public int Units { get; private set; }

    public static CatalogSweep Run(GameInstallation installation, SimulationRules rules, int race)
    {
        var file = installation.DataFile(["scenario", .. Scenario.Split('/')]) + ".scn";
        var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
        var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
        // Nobody plays a profile, so only the sweep's own orders act. The swept
        // team takes the race; the enemy the other one.
        var scenario = ScenarioDefinition.Load(file).WithTeams(team => team with
        {
            AiProfile = 0,
            Race = team.TeamId == Team ? race : team.TeamId == Enemy ? 1 - race : team.Race,
        });
        var sweep = new CatalogSweep(ScenarioSimulation.Create(scenario, path, rules, terrain: map), rules, path, race);
        sweep.Execute();
        return sweep;
    }

    private string RaceName => race == 0 ? "Human" : "Gray";
    private TeamEconomy Economy => simulation.EconomyForTeam(Team)!;

    private void Execute()
    {
        Economy.AddP7(1_000_000);
        var items = rules.Dependencies.Items.Values.Where(item => ItemRace(item) == race).OrderBy(item => item.Id).ToArray();
        Phase("buildings", () => BuildAll(items.Where(item => item.IsBuilding).ToArray()));
        Phase("troops", () => TrainAll(items.Where(item => item.IsTroop).ToArray()));
        Phase("research", () => ResearchAll(items.Where(item => item.IsUpgrade).ToArray()));
        foreach (var entityId in trainedByEntity.Keys.Order().ToArray())
            Phase($"unit {Name(entityId)}", () => ExerciseUnit(entityId, trainedByEntity[entityId]));
        var others = rules.Entities.Entities
            .Where(entity => entity.Faction == race && entity.MovementSpeed > 0 && !trainedByEntity.ContainsKey(entity.Id))
            .Select(entity => entity.Id).ToArray();
        foreach (var entityId in others)
            Phase($"unit {Name(entityId)} (spawned)", () => ExerciseUnit(entityId, Spawn(entityId, Team)?.Seed.InstanceId));
        Phase("city teardown", TearDownCity);
        foreach (var ((gapRace, key), _) in KnownGaps)
            if (gapRace == race && !observedGaps.Contains(key))
                Fail($"known gap '{key}' no longer occurs: remove it from CatalogSweep.KnownGaps and update its document");
    }

    private void Phase(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            Fail($"{name}: {error.GetType().Name}: {error.Message}");
        }
    }

    private void Fail(string message) => Failures.Add($"{RaceName} {message}");

    /// <summary>A failure that is fine while <see cref="KnownGaps"/> lists it.</summary>
    private void Gap(string key, string detail)
    {
        if (!KnownGaps.TryGetValue((race, key), out var reason))
        {
            Fail(detail);
            return;
        }
        observedGaps.Add(key);
        Gaps.Add($"{RaceName} {key}: {reason}");
    }

    // ---- build tree --------------------------------------------------------

    private void BuildAll(IReadOnlyList<DependencyDefinition> buildings)
    {
        foreach (var item in InPrerequisiteOrder(buildings))
        {
            var slot = item.BuildingSlot!.Value;
            if (!rules.Footprints.TryResolveBuildingEntity(race, item.BuildingVariant!.Value, slot, out var entityId))
            {
                Fail($"building item {item.Id}: no entity for slot {slot} variant {item.BuildingVariant}");
                continue;
            }
            var eligibility = Economy.Evaluate(rules.Dependencies, item.Id);
            if (eligibility == PurchaseEligibility.AlreadyCompleted)
            {
                if (simulation.CityBuilding(Team, slot)?.Seed.EntityId != entityId)
                    Fail($"building item {item.Id} counts as built but slot {slot} holds no {Name(entityId)}");
                Report.Add($"{RaceName} building {item.Id} {Name(entityId)}: already standing in slot {slot}");
                Buildings++;
                continue;
            }
            if (eligibility != PurchaseEligibility.Available)
            {
                Fail($"building item {item.Id} {Name(entityId)}: {eligibility}");
                continue;
            }
            Step(new PurchaseIntent(Team, item.Id));
            var placed = simulation.LastBuildingPlacements.SingleOrDefault(drop => drop.DependencyItemId == item.Id);
            var building = simulation.CityBuilding(Team, slot);
            if (placed?.Outcome != BuildingDropOutcome.Placed || building?.Seed.EntityId != entityId)
            {
                Fail($"building item {item.Id} {Name(entityId)}: {placed?.Outcome.ToString() ?? "no drop"}, slot {slot} holds {(building is null ? "nothing" : Name(building.Seed.EntityId))}");
                continue;
            }
            if (!Economy.CompletedItems.Contains(item.Id)) Fail($"building item {item.Id} {Name(entityId)} is not counted as built");
            if (building.Health != building.MaximumHealth) Fail($"building item {item.Id} {Name(entityId)} rose with {building.Health}/{building.MaximumHealth} health");
            StepIdle(5);
            Report.Add($"{RaceName} building {item.Id} {Name(entityId)}: built in slot {slot}");
            Buildings++;
        }
    }

    private void TrainAll(IReadOnlyList<DependencyDefinition> troops)
    {
        var headquarters = simulation.CityBuilding(Team, 0)!;
        foreach (var item in InPrerequisiteOrder(troops))
        {
            var entityId = item.TroopEntityId!.Value;
            var eligibility = Economy.Evaluate(rules.Dependencies, item.Id);
            if (eligibility != PurchaseEligibility.Available)
            {
                Fail($"troop item {item.Id} {Name(entityId)}: {eligibility}");
                continue;
            }
            Step(new PurchaseIntent(Team, item.Id), new ProduceUnitIntent(Team, item.Id, headquarters.Seed.InstanceId));
            var order = simulation.LastUnitProductions.SingleOrDefault(production => production.DependencyItemId == item.Id);
            if (order?.Outcome != UnitProductionOutcome.Queued)
            {
                Fail($"troop item {item.Id} {Name(entityId)}: order {order?.Outcome.ToString() ?? "missing"}");
                continue;
            }
            UnitProducedEvent? produced = null;
            var ticks = StepUntil(() => (produced = simulation.LastUnitProductions.FirstOrDefault(production =>
                production.DependencyItemId == item.Id && production.Outcome == UnitProductionOutcome.Produced)) is not null, 600);
            if (produced is null)
            {
                Fail($"troop item {item.Id} {Name(entityId)}: not produced within 600 updates");
                continue;
            }
            var troop = simulation.Actor(produced.EntityInstanceId);
            if (troop is null || troop.Seed.EntityId != entityId || troop.Seed.Team != Team || troop.IsDestroyed)
            {
                Fail($"troop item {item.Id} {Name(entityId)}: produced actor {produced.EntityInstanceId} is wrong");
                continue;
            }
            trainedByEntity[entityId] = troop.Seed.InstanceId;
            Report.Add($"{RaceName} troop {item.Id} {Name(entityId)}: trained in {ticks} updates");
            Troops++;
        }
    }

    private void ResearchAll(IReadOnlyList<DependencyDefinition> upgrades)
    {
        foreach (var item in InPrerequisiteOrder(upgrades))
        {
            var target = item.UpgradeEntityId!.Value;
            var label = $"research item {item.Id} ({item.ResearchEffect} {item.UpgradeLevel} for {Name(target)})";
            var eligibility = Economy.Evaluate(rules.Dependencies, item.Id);
            if (eligibility != PurchaseEligibility.Available)
            {
                Fail($"{label}: {eligibility}");
                continue;
            }
            // Like the research tab, which lists an upgrade on every structure that offers it.
            var source = Enumerable.Range(0, 5).Select(slot => simulation.CityBuilding(Team, slot))
                .FirstOrDefault(building => building is not null && simulation.StructureOffersResearch(building, item.Id));
            if (source is null)
            {
                Fail($"{label}: no structure offers it");
                continue;
            }
            Step(new PurchaseIntent(Team, item.Id), new ResearchIntent(Team, item.Id, source.Seed.InstanceId));
            var completed = simulation.LastResearchCompletions.SingleOrDefault(research => research.DependencyItemId == item.Id);
            if (completed?.Outcome != ResearchOutcome.Completed)
            {
                Fail($"{label}: {completed?.Outcome.ToString() ?? "no result"}");
                continue;
            }
            if (item.IsStatUpgrade && trainedByEntity.TryGetValue(target, out var unitId) && simulation.Actor(unitId) is { } unit)
            {
                var level = item.ResearchEffect == ResearchEffectKind.WeaponLevel
                    ? simulation.WeaponUpgradeLevel(unit) : simulation.ArmorUpgradeLevel(unit);
                if (level < item.UpgradeLevel) Fail($"{label}: the unit's level is still {level}");
            }
            Report.Add($"{RaceName} {label}: completed");
            Research++;
        }
    }

    /// <summary>Items whose prerequisites are inside the list come after them; the rest keep id order.</summary>
    private static IEnumerable<DependencyDefinition> InPrerequisiteOrder(IReadOnlyList<DependencyDefinition> items)
    {
        var pending = items.ToList();
        var done = new HashSet<int>();
        while (pending.Count != 0)
        {
            var next = pending.FirstOrDefault(item => item.PrerequisiteItemIds.All(prerequisite =>
                done.Contains(prerequisite) || items.All(other => other.Id != prerequisite))) ?? pending[0];
            pending.Remove(next);
            done.Add(next.Id);
            yield return next;
        }
    }

    // ---- units -------------------------------------------------------------

    private void ExerciseUnit(int entityId, int? instanceId)
    {
        var name = Name(entityId);
        if (instanceId is null || simulation.Actor(instanceId.Value) is not { } unit)
        {
            Fail($"unit {name}: could not be placed");
            return;
        }
        Units++;
        var notes = new List<string>();
        Move(unit, name, notes);
        unit = Alive(unit, entityId);
        Attack(unit, name, notes);
        unit = Alive(unit, entityId);
        Special(unit, name, notes);
        unit = Alive(unit, entityId);
        Die(unit, name, notes);
        Report.Add($"{RaceName} unit {name}: {string.Join(", ", notes)}");
    }

    /// <summary>The same unit, or a fresh one of its type when it died along the way.</summary>
    private SimulatedActor Alive(SimulatedActor unit, int entityId) =>
        !unit.IsDestroyed && simulation.Actor(unit.Seed.InstanceId) is not null && unit.DeployedEntityId is null
            ? unit
            : Spawn(entityId, Team) ?? unit;

    private void Move(SimulatedActor unit, string name, List<string> notes)
    {
        if (unit.Definition.MovementSpeed <= 0)
        {
            notes.Add("static");
            return;
        }
        var start = unit.Movement.OccupiedCell;
        if (ReachableCell(start, MoveDistance, unit.Definition.MovementClass) is not { } target)
        {
            Fail($"unit {name}: no reachable cell {MoveDistance} away from {start}");
            return;
        }
        Step(new MoveIntent(unit.Seed.InstanceId, target));
        var ticks = StepUntil(() => unit.MoveOrder is null && unit.Playback is null, 600);
        var reached = Distance(unit.Movement.OccupiedCell, target);
        if (ticks < 0 || reached > 1)
            Fail($"unit {name}: moving {start} -> {target} stopped at {unit.Movement.OccupiedCell}{(ticks < 0 ? " still moving after 600 updates" : "")}");
        else
            notes.Add($"moved {Distance(start, target)} cells in {ticks} updates");
    }

    private void Attack(SimulatedActor unit, string name, List<string> notes)
    {
        var victimType = race == 0 ? GrayTrooper : HumanMarine;
        if (FreeCellNear(unit.Movement.OccupiedCell, 3, 0) is not { } cell ||
            Spawn(victimType, Enemy, cell) is not { } victim)
        {
            Fail($"unit {name}: no room for an enemy to attack");
            return;
        }
        Step(new AttackIntent(unit.Seed.InstanceId, victim.Seed.InstanceId));
        var order = simulation.LastAttackOrders.SingleOrDefault(attack => attack.SourceActorInstanceId == unit.Seed.InstanceId);
        var armed = simulation.EffectiveWeaponFor(unit) is not null;
        if (simulation.EffectiveWeaponFor(unit) is { Range: <= 0 } rangeless)
        {
            notes.Add($"weapon {rangeless.Id} has range {rangeless.Range}, no ordinary attack");
        }
        else if (!armed)
        {
            if (order?.Outcome != AttackOrderOutcome.Unarmed) Fail($"unit {name} has no weapon but its attack order was {order?.Outcome.ToString() ?? "missing"}");
            else notes.Add("unarmed");
        }
        else if (order?.Outcome != AttackOrderOutcome.Acquired)
        {
            Fail($"unit {name}: attack order {order?.Outcome.ToString() ?? "missing"}");
        }
        else
        {
            var fires = 0;
            var impacts = 0;
            var ticks = StepUntil(() =>
            {
                fires += simulation.LastWeaponFires.Count(fire => fire.SourceActorInstanceId == unit.Seed.InstanceId);
                impacts += simulation.LastProjectileImpacts.Count(impact => impact.SourceActorInstanceId == unit.Seed.InstanceId);
                return victim.Health < victim.MaximumHealth;
            }, 600);
            var weapon = simulation.EffectiveWeaponFor(unit)!;
            var detail = $"unit {name}: never damaged the {Name(victimType)} at {victim.Movement.OccupiedCell} (from {unit.Movement.OccupiedCell}); " +
                $"weapon {weapon.Id} class {weapon.WeaponClass} damage {weapon.Damage} range {weapon.Range}, {fires} shots, {impacts} impacts, " +
                $"target {unit.AttackTargetInstanceId?.ToString() ?? "none"}, victim health {victim.Health}/{victim.MaximumHealth}";
            if (ticks < 0 && weapon.Range == 1) Gap($"melee {name}", detail);
            else if (ticks < 0) Fail(detail);
            else notes.Add($"hit after {ticks} updates");
        }
        Remove(victim);
    }

    private void Special(SimulatedActor unit, string name, List<string> notes)
    {
        var id = unit.Seed.InstanceId;
        var definition = simulation.EffectiveDefinition(unit);
        switch (definition.Code)
        {
            case "ENGI" or "SLOM":
                Step(new DeployMineIntent(id));
                Expect(name, "mine", notes, () => simulation.LastMineDeployments.Any(mine => mine.SourceActorInstanceId == id && mine.Outcome == MineDeploymentOutcome.Deployed), 300);
                break;
            case "TURR" or "XENO":
                Step(new DeployTowerIntent(id));
                Expect(name, "tower", notes, () => unit.DeployedEntityId is not null, 300);
                break;
            case "SARG" or "PSYC":
                Step(new DeployStealIntent(id));
                if (Expect(name, "steal stance", notes, () => unit.DeployedEntityId is not null || simulation.LastStealDeployments.Any(steal =>
                        steal.EntityInstanceId == id && steal.Outcome is not StealDeploymentOutcome.Preparing), 300) && unit.DeployedEntityId is not null)
                {
                    Step(new RetractStealIntent(id));
                    Expect(name, "steal retract", notes, () => unit.DeployedEntityId is null, 300);
                }
                GroundSpecial(unit, name, notes);
                break;
            case "BEON" or "ZISP":
                if (FreeCellNear(unit.Movement.OccupiedCell, 1, 0) is { } cell &&
                    Spawn(race == 0 ? HumanMarine : GrayTrooper, Team, cell) is { } patient)
                {
                    patient.Health = Math.Max(1, patient.MaximumHealth / 2);
                    StepUntil(() => unit.AbilityCharge >= SimulatedActor.NativeMaximumAbilityCharge, 2_000);
                    Step(new HealAreaIntent(id));
                    Expect(name, "heal", notes, () => patient.Health > patient.MaximumHealth / 2, 300);
                    Remove(patient);
                }
                break;
            case "EXPL" or "SLUG":
                var vent = simulation.PetraVents
                    .Where(candidate => candidate.HarvesterInstanceId is null && candidate.PendingHarvesterInstanceId is null)
                    .OrderBy(candidate => Distance(candidate.Position, unit.Movement.OccupiedCell)).ThenBy(candidate => candidate.Id)
                    .FirstOrDefault();
                if (vent is null)
                {
                    Fail($"unit {name}: no free vent to harvest");
                    break;
                }
                Step(new HarvestVentIntent(id, vent.Id));
                if (Expect(name, "harvest", notes, () => unit.DeployedEntityId is not null, 3_000))
                {
                    Step(new RetractHarvesterIntent(id));
                    Expect(name, "harvest retract", notes, () => unit.DeployedEntityId is null, 300);
                }
                break;
            default:
                if (definition.Id is >= 69 and <= 76)
                {
                    Step(new InspireTroopsIntent(id));
                    Expect(name, "inspire", notes, () => simulation.LastInspires.Any(inspire => inspire.SourceActorInstanceId == id &&
                        inspire.Outcome is InspireOutcome.Applied or InspireOutcome.NoEligibleTargets), 300);
                }
                else if (definition.HasGroundSpecialAttack)
                {
                    GroundSpecial(unit, name, notes);
                }
                break;
        }
    }

    /// <summary>The value-29 ground attack (Napalm, Disease, Ground Attack) at a free cell four away; every research is done by now.</summary>
    private void GroundSpecial(SimulatedActor unit, string name, List<string> notes)
    {
        var id = unit.Seed.InstanceId;
        var definition = simulation.EffectiveDefinition(unit);
        var target = FreeCellNear(unit.Movement.OccupiedCell, 4, 0) ?? unit.Movement.OccupiedCell;
        Step(new GroundSpecialAttackIntent(id, target));
        var special = simulation.LastGroundSpecialAttacks.SingleOrDefault(attack => attack.SourceActorInstanceId == id);
        var detail = $"unit {name}: ground special weapon {definition.GroundSpecialWeaponId} {special?.Outcome.ToString() ?? "missing"}";
        // A unit already facing its target fires in the update that takes the order.
        if (special?.Outcome == GroundSpecialAttackOutcome.Accepted && simulation.LastWeaponFires.Any(fire => fire.SourceActorInstanceId == id))
            notes.Add($"ground special weapon {definition.GroundSpecialWeaponId} at once");
        else if (special?.Outcome == GroundSpecialAttackOutcome.Accepted)
            Expect(name, $"ground special weapon {definition.GroundSpecialWeaponId}", notes, () => simulation.LastWeaponFires.Any(fire => fire.SourceActorInstanceId == id), 300);
        else if (special?.Outcome == GroundSpecialAttackOutcome.WeaponUnavailable && definition.GroundSpecialWeaponId == 0)
            Gap($"ground attack {name}", detail);
        else
            Fail(detail);
    }

    private bool Expect(string name, string what, List<string> notes, Func<bool> done, int limit)
    {
        var ticks = StepUntil(done, limit);
        if (ticks < 0)
        {
            Fail($"unit {name}: {what} did not happen within {limit} updates");
            return false;
        }
        notes.Add($"{what} after {ticks} updates");
        return true;
    }

    private void Die(SimulatedActor unit, string name, List<string> notes)
    {
        var id = unit.Seed.InstanceId;
        var events = new List<DestroyedActorEvent>();
        simulation.Destroy(unit, events);
        if (events.Count != 1) Fail($"unit {name}: dying reported {events.Count} events");
        var ticks = StepUntil(() => simulation.Actor(id) is null || !OwnsAnyCell(id), 400);
        if (ticks < 0) Fail($"unit {name}: its body still holds a cell 400 updates after dying");
        else notes.Add("died");
    }

    private void TearDownCity()
    {
        for (var slot = 4; slot >= 0; slot--)
        {
            if (simulation.CityBuilding(Team, slot) is not { } building) continue;
            var events = new List<DestroyedActorEvent>();
            simulation.Destroy(building, events);
            StepIdle(5);
            var item = rules.Dependencies.Items.Values.Where(item => item.IsBuilding && item.BuildingFaction == race && item.BuildingSlot == slot)
                .Where(item => rules.Footprints.TryResolveBuildingEntity(race, item.BuildingVariant!.Value, slot, out var entityId) && entityId == building.Seed.EntityId)
                .Select(item => (int?)item.Id).FirstOrDefault();
            if (item is { } itemId && Economy.CompletedItems.Contains(itemId))
                Fail($"city teardown: slot {slot}'s item {itemId} still counts as built after its building died");
        }
        StepIdle(200);
        var anyItem = rules.Dependencies.Items.Values.First(item => ItemRace(item) == race && item.IsTroop);
        var eligibility = Economy.Evaluate(rules.Dependencies, anyItem.Id);
        if (eligibility == PurchaseEligibility.Available) Fail($"city teardown: a team without buildings may still buy item {anyItem.Id}");
        Report.Add($"{RaceName} city teardown: troop item {anyItem.Id} is {eligibility}");
    }

    // ---- helpers -----------------------------------------------------------

    private const int HumanMarine = 0;
    private const int GrayTrooper = 8;

    private int? ItemRace(DependencyDefinition item)
    {
        if (item.IsBuilding) return item.BuildingFaction;
        var entityId = item.IsTroop ? item.TroopEntityId : item.UpgradeEntityId;
        return entityId is { } id && (uint)id < (uint)rules.Entities.Entities.Count ? rules.Entities.Entities[id].Faction : null;
    }

    private string Name(int entityId) => (uint)entityId < (uint)rules.Entities.Entities.Count
        ? $"{rules.Entities.Entities[entityId].Code}#{entityId}" : $"entity {entityId}";

    private SimulatedActor? Spawn(int entityId, int team, CellCoordinate? near = null)
    {
        var origin = near ?? FreeCellNear(simulation.CityBuilding(Team, 0)?.Movement.OccupiedCell ?? new CellCoordinate(10, 55), 6, 0)
            ?? new CellCoordinate(10, 50);
        return simulation.SpawnNativeUnit(entityId, team, origin);
    }

    private void Remove(SimulatedActor actor)
    {
        if (actor.IsDestroyed) return;
        simulation.Destroy(actor, new List<DestroyedActorEvent>());
        StepIdle(1);
    }

    /// <summary>The first free cell on the square rings from <paramref name="distance"/> outward.</summary>
    private CellCoordinate? FreeCellNear(CellCoordinate origin, int distance, int movementClass, Func<CellCoordinate, bool>? accept = null)
    {
        for (var ring = distance; ring <= distance + 6; ring++)
        for (var dz = -ring; dz <= ring; dz++)
        for (var dx = -ring; dx <= ring; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
            var candidate = new CellCoordinate(origin.X + dx, origin.Z + dz);
            if (IsFree(candidate, movementClass) && (accept is null || accept(candidate))) return candidate;
        }
        return null;
    }

    /// <summary>A free cell about <paramref name="distance"/> away that a walk over free passable cells reaches.</summary>
    private CellCoordinate? ReachableCell(CellCoordinate origin, int distance, int movementClass)
    {
        if (movementClass != 0) return FreeCellNear(origin, distance, movementClass);
        var reached = new HashSet<CellCoordinate> { origin };
        var frontier = new Queue<CellCoordinate>(reached);
        while (frontier.Count != 0)
        {
            var cell = frontier.Dequeue();
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var next = new CellCoordinate(cell.X + dx, cell.Z + dz);
                if (Distance(next, origin) > distance + 6 || !IsFree(next, 0) || !reached.Add(next)) continue;
                frontier.Enqueue(next);
            }
        }
        return FreeCellNear(origin, distance, 0, reached.Contains);
    }

    private bool IsFree(CellCoordinate cell, int movementClass)
    {
        if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height) return false;
        if (movementClass == 0 && path.RegionAt(cell) == 0) return false;
        return !(movementClass == 0 ? simulation.GroundOccupancy : simulation.AlternateOccupancy).IsOccupied(cell);
    }

    private bool OwnsAnyCell(int instanceId)
    {
        for (var z = 0; z < path.Height; z++)
        for (var x = 0; x < path.Width; x++)
        {
            var cell = new CellCoordinate(x, z);
            if (simulation.GroundOccupancy.TryGetOwner(cell, out var owner) && owner == instanceId) return true;
            if (simulation.AlternateOccupancy.TryGetOwner(cell, out owner) && owner == instanceId) return true;
        }
        return false;
    }

    private static int Distance(CellCoordinate left, CellCoordinate right) =>
        Math.Max(Math.Abs(left.X - right.X), Math.Abs(left.Z - right.Z));

    private void Step(params WorldCommand[] commands)
    {
        var tick = simulation.TickCount;
        simulation.Step(commands.Select((command, index) => new ScheduledWorldCommand(tick, (ulong)index, command)).ToArray());
        CheckInvariants();
    }

    private void StepIdle(int ticks)
    {
        for (var tick = 0; tick < ticks; tick++) Step();
    }

    /// <summary>Updates until the condition holds; the number of updates taken, or -1.</summary>
    private int StepUntil(Func<bool> done, int limit)
    {
        for (var tick = 1; tick <= limit; tick++)
        {
            Step();
            if (done()) return tick;
        }
        return -1;
    }

    private void CheckInvariants()
    {
        var ids = new HashSet<int>();
        foreach (var actor in simulation.Actors)
        {
            var name = Name(actor.Seed.EntityId);
            if (!ids.Add(actor.Seed.InstanceId)) Invariant($"actor id {actor.Seed.InstanceId} appears twice");
            if (actor.Health > actor.MaximumHealth) Invariant($"{name} has {actor.Health}/{actor.MaximumHealth} health");
            var cell = actor.Movement.OccupiedCell;
            if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height) Invariant($"{name} stands outside the map at {cell}");
            // A live mobile unit holds its own cell, or the next one while it steps.
            var definition = simulation.EffectiveDefinition(actor);
            if (!actor.IsDestroyed && !actor.IsDying && definition.MovementSpeed > 0)
            {
                var grid = definition.MovementClass == 0 ? simulation.GroundOccupancy : simulation.AlternateOccupancy;
                var holds = (grid.TryGetOwner(cell, out var owner) && owner == actor.Seed.InstanceId) ||
                            (grid.TryGetOwner(actor.Movement.ReservedDestination, out owner) && owner == actor.Seed.InstanceId);
                if (!holds) Invariant($"{name} {actor.Seed.InstanceId} does not hold its cell {cell}");
            }
        }
        for (var team = 0; team < 8; team++)
            if (simulation.EconomyForTeam(team) is { P7: < 0 }) Invariant($"team {team} has negative P7");
    }

    private void Invariant(string message)
    {
        if (invariantFailures.Add(message)) Fail($"invariant at update {simulation.TickCount}: {message}");
    }
}
