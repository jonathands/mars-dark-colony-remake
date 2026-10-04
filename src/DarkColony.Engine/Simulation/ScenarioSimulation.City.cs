using DarkColony.Engine.Commands;
using DarkColony.Engine.Data;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// Player cities: the fixed building slots around each team's city origin
/// (the pedestal), created by the SCN loader <c>0x41B920</c> through
/// <c>0x444F14</c>, and the headquarters gate on Petra-7 income.
/// </summary>
public sealed partial class ScenarioSimulation
{
    /// <summary>Default passive income per player per 16 ticks (loader <c>0x41C3F0</c> sets player +0x19B4 = 3).</summary>
    public const int NativePassiveP7Rate = 3;

    private sealed record CityBuildingSeed(int Team, int Slot, int InstanceId, FixedPointPosition Position, int Health);

    /// <summary>
    /// Adds an actor for every %City slot 0-4 with a nonzero level: entity
    /// from the build table for (race, level - 1, slot), placed at the slot's
    /// fixed offset from the city origin and claiming the slot's footprint.
    /// The loader allocates these as actors 0-119 before SCN placements, which
    /// it then writes into the ground grid without a conflict test (<c>0x41AF14</c>),
    /// so a placement inside a footprint takes that cell. The port appends the
    /// buildings after placements to keep placement instance IDs stable and
    /// claims only the footprint cells no placement took.
    /// </summary>
    private static List<CityBuildingSeed> SeedCityBuildings(
        ScenarioDefinition scenario,
        EntityCatalog catalog,
        BuildingFootprintCatalog? footprints,
        CellOccupancy ground,
        List<WorldEntity> seeds)
    {
        var cities = new List<CityBuildingSeed>();
        if (footprints is null) return cities;
        foreach (var team in scenario.Teams.Where(team => team.Enabled && team.TeamId is >= 0 and < 8 && team.HasCity).OrderBy(team => team.TeamId))
        {
            if (team.Race is not { } race) continue;
            var origin = team.CityOrigin!.Value;
            for (var slot = 0; slot < team.CitySlots.Count; slot++)
            {
                var (level, health) = team.CitySlots[slot];
                if (level <= 0 || !footprints.TryResolveBuildingEntity(race, level - 1, slot, out var entityId) ||
                    (uint)entityId >= (uint)catalog.Entities.Count) continue;
                var instanceId = seeds.Count + 1;
                var position = footprints.CitySlotPosition(origin, slot);
                var buildingHealth = health == -1 ? catalog[entityId].Health : health;
                seeds.Add(new WorldEntity(instanceId, entityId, team.TeamId, position.Cell, position, buildingHealth, 0));
                ground.ReplaceClaims(instanceId, footprints.CitySlotCells(origin, slot).Where(cell => !ground.TryGetOwner(cell, out _)));
                cities.Add(new CityBuildingSeed(team.TeamId, slot, instanceId, position, buildingHealth));
            }
        }
        return cities;
    }

    /// <summary>Whether the team has a city (a nonzero <c>%AISlots</c> origin X).</summary>
    public bool HasCity(int teamId) => cityOrigins.ContainsKey(teamId);

    /// <summary>
    /// True only for scenarios that declare no city at all (synthetic engine
    /// fixtures). They keep two port adapters: buildings dropped anywhere
    /// (<see cref="PlaceBuildingIntent"/>) and troops spawned at once beside
    /// their building. In the original every building is a city slot at the
    /// player's origin, and <c>0x444C80</c> does nothing for a zero origin, so
    /// in shipped scenarios a team without a city builds and trains nothing.
    /// </summary>
    public bool UsesPortConstructionAdapters() => !citiesDeclared;

    /// <summary>
    /// A paid building order of a team with a city is native command 9
    /// (<c>0x41C8D4</c>): the slot's building is (re)created at once with full
    /// health by <c>0x444F14</c>, replacing any earlier variant, and the slot's
    /// troop queue restarts empty. Returns null for teams without a city,
    /// whose orders keep the port's placed-drop adapter.
    /// </summary>
    private BuildingPlacedEvent? BuildPurchasedCitySlot(PurchaseIntent purchase)
    {
        if (!cityOrigins.TryGetValue(purchase.TeamId, out var origin) || footprints is null ||
            dependencyCatalog?.TryGet(purchase.DependencyItemId, out var item) != true || !item.IsBuilding ||
            !teamEconomies.TryGetValue(purchase.TeamId, out var economy)) return null;
        var slot = item.BuildingSlot!.Value;
        if (!footprints.TryResolveBuildingEntity(item.BuildingFaction!.Value, item.BuildingVariant!.Value, slot, out var entityId) ||
            (uint)entityId >= (uint)entityDefinitions.Count)
            return new BuildingPlacedEvent(purchase.TeamId, item.Id, 0, 0, origin, BuildingDropOutcome.EntityUnresolved);
        if (cityBuildings.TryGetValue((purchase.TeamId, slot), out var previousId) && actorsById.TryGetValue(previousId, out var previous))
        {
            // The native actor keeps its index and changes type; the port
            // retires the previous actor without a death.
            GroundOccupancy.Release(previousId);
            AlternateOccupancy.Release(previousId);
            actorsById.Remove(previousId);
            actors.Remove(previous);
        }
        var instanceId = nextActorInstanceId++;
        var position = footprints.CitySlotPosition(origin, slot);
        var seed = new WorldEntity(instanceId, entityId, purchase.TeamId, position.Cell, position, 0, 0);
        var actor = new SimulatedActor(seed, EntityDefinitionFor(entityId));
        actor.Movement.AdvanceVisual(position.XRaw - actor.Movement.VisualPosition.XRaw, position.ZRaw - actor.Movement.VisualPosition.ZRaw);
        actors.Add(actor);
        actorsById.Add(instanceId, actor);
        // 0x444F14 writes the footprint over whatever stands there.
        GroundOccupancy.ReplaceClaims(instanceId, footprints.CitySlotCells(origin, slot));
        cityBuildings[(purchase.TeamId, slot)] = instanceId;
        economy.MarkCompleted(dependencyCatalog, item.Id);
        SyncCitySlotItems(purchase.TeamId, slot);
        if (footprints.SlotProductionQueue(slot) is { } queue && productionQueues.TryGetValue((purchase.TeamId, queue), out var state))
        {
            // A reserved exit keeps its 0x3FE marker: 0x444F14 resets only the queue.
            state.Items.Clear();
            state.Ready = true;
            state.TicksRemaining = 0;
            state.ReservedExit = null;
        }
        return new BuildingPlacedEvent(purchase.TeamId, item.Id, instanceId, entityId, origin, BuildingDropOutcome.Placed);
    }

    /// <summary>
    /// <c>0x438220</c> counts a building prerequisite as met while its city
    /// slot holds a live building (slot health <c>+0xBD4 + slot * 4</c> nonzero)
    /// whose variant (<c>+0xC5C + slot * 4</c>) is at least the item's. The port
    /// keeps the team's completed building items equal to that rule for each
    /// city slot.
    /// </summary>
    private void SyncCitySlotItems(int team, int slot)
    {
        if (dependencyCatalog is null || footprints is null || !teamEconomies.TryGetValue(team, out var economy) ||
            !teamRaces.TryGetValue(team, out var race) || race is null) return;
        int? liveVariant = null;
        if (CityBuilding(team, slot) is { } building)
        {
            for (var variant = 0; variant < 2 && liveVariant is null; variant++)
                if (footprints.TryResolveBuildingEntity(race.Value, variant, slot, out var entityId) && entityId == building.Seed.EntityId)
                    liveVariant = variant;
        }
        foreach (var item in dependencyCatalog.Items.Values.Where(item =>
                     item.IsBuilding && item.BuildingFaction == race && item.BuildingSlot == slot))
        {
            if (liveVariant is { } variant && item.BuildingVariant <= variant)
                economy.SeedCompletedBuilding(dependencyCatalog, item.Id);
            else
                economy.WithdrawCompletedBuilding(dependencyCatalog, item.Id);
        }
    }

    /// <summary>Re-applies the slot rule after a city building is destroyed.</summary>
    private void OnCityBuildingDestroyed(SimulatedActor actor)
    {
        foreach (var ((team, slot), instanceId) in cityBuildings)
        {
            if (instanceId != actor.Seed.InstanceId) continue;
            SyncCitySlotItems(team, slot);
            return;
        }
    }

    /// <summary>The actor in a team's city slot, if that building exists and is alive.</summary>
    public SimulatedActor? CityBuilding(int team, int slot) =>
        cityBuildings.TryGetValue((team, slot), out var instanceId) && actorsById.TryGetValue(instanceId, out var actor) && !actor.IsDestroyed
            ? actor
            : null;

    /// <summary>
    /// Player <c>+0xBD4</c> (slot 0 health) gates both passive income
    /// (<c>0x419B4C</c>) and vent income (<c>0x413B31</c>). Scenarios without
    /// declared cities (engine checks) are not gated.
    /// </summary>
    private bool CanEarnP7(int team) => !citiesDeclared || CityBuilding(team, 0) is not null;
}
