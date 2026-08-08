using DarkColony.Engine.Data;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;

namespace DarkColony.Engine.World;

public sealed record AutonomousSpawnResult(
    IReadOnlyList<WorldEntity> Entities,
    IReadOnlyDictionary<int, int> MissingPopulationByGroup,
    IReadOnlyDictionary<int, IReadOnlyList<WorldEntity>> EntitiesByGroup);

/// <summary>Initial group population recovered from dc.exe 0x43FEAC/0x41B634.</summary>
public static class AutonomousSpawnSeeder
{
    public const int InternalNeutralTeam = 9;

    public static AutonomousSpawnResult Seed(
        IEnumerable<AutonomousSpawnGroup> groups,
        EntityCatalog catalog,
        PathRegionMap path,
        CellOccupancy groundOccupancy,
        CellOccupancy alternateOccupancy,
        int firstInstanceId = 1)
    {
        var validator = new SpawnCellValidator(path, groundOccupancy, alternateOccupancy);
        var entities = new List<WorldEntity>();
        var missing = new Dictionary<int, int>();
        var entitiesByGroup = new Dictionary<int, IReadOnlyList<WorldEntity>>();
        var instanceId = firstInstanceId;
        foreach (var group in groups)
        {
            if ((uint)group.EntityId >= (uint)catalog.Entities.Count)
                throw new InvalidDataException($"Autonomous group {group.GroupId} has invalid entity ID {group.EntityId}.");
            if (group.DesiredPopulation < 0 || group.DesiredPopulation >= 10)
                throw new InvalidDataException($"Autonomous group {group.GroupId} has invalid population {group.DesiredPopulation}.");

            var definition = catalog[group.EntityId];
            var spawned = 0;
            var groupEntities = new List<WorldEntity>();
            for (; spawned < group.DesiredPopulation; spawned++)
            {
                var cell = validator.FindNearestValid(group.Origin, definition.MovementClass);
                if (cell is null) break;
                var entity = new WorldEntity(
                    instanceId++, group.EntityId, InternalNeutralTeam, cell.Value,
                    FixedPointPosition.AtCellCenter(cell.Value), definition.Health, group.ScenarioFlag);
                var occupancy = definition.MovementClass == 0 ? groundOccupancy : alternateOccupancy;
                if (!occupancy.TryClaim(entity.InstanceId, [cell.Value]))
                    throw new InvalidOperationException("Spawn validator returned an occupied cell.");
                entities.Add(entity);
                groupEntities.Add(entity);
            }

            entitiesByGroup[group.GroupId] = groupEntities;

            if (spawned != group.DesiredPopulation) missing[group.GroupId] = group.DesiredPopulation - spawned;
        }

        return new AutonomousSpawnResult(entities, missing, entitiesByGroup);
    }
}
