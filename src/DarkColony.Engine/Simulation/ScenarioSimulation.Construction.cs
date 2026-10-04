using DarkColony.Engine.Commands;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>Building drops, troop production, research, and authored scenario buildings.</summary>
public sealed partial class ScenarioSimulation
{
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
        // A team with a city orders into its player queue like the native
        // command 10; the immediate spawn below is a port adapter for teams
        // without one.
        if (cityOrigins.ContainsKey(intent.TeamId)) return EnqueueTroop(intent, entityId, economy);
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
}
