using DarkColony.Engine.Scenario;

namespace DarkColony.Engine.World;

public sealed record WorldEntity(
    int InstanceId,
    int EntityId,
    int Team,
    CellCoordinate SpawnCell,
    FixedPointPosition Position,
    int ScenarioValue,
    int ScenarioFlag);

/// <summary>
/// Deterministic world seed from an SCN. Special vents stay separate because
/// their five-field records do not share ordinary actor semantics.
/// </summary>
public sealed class ScenarioWorld
{
    private ScenarioWorld(
        IReadOnlyList<WorldEntity> entities,
        IReadOnlyList<ScenarioVent> vents,
        CellOccupancy staticOccupancy)
    {
        Entities = entities;
        Vents = vents;
        StaticOccupancy = staticOccupancy;
    }

    public IReadOnlyList<WorldEntity> Entities { get; }
    public IReadOnlyList<ScenarioVent> Vents { get; }
    public CellOccupancy StaticOccupancy { get; }

    public static ScenarioWorld Create(ScenarioDefinition scenario, BuildingFootprintCatalog footprints)
    {
        var entities = scenario.Placements.Select((placement, index) =>
        {
            var cell = new CellCoordinate(placement.X, placement.Z);
            return new WorldEntity(
                index + 1,
                placement.EntityId,
                placement.Team,
                cell,
                FixedPointPosition.AtCellCenter(cell),
                placement.Value,
                placement.Flag);
        }).ToArray();

        var occupancy = new CellOccupancy();
        foreach (var entity in entities)
        {
            var cells = footprints.OccupiedCells(entity.EntityId, entity.SpawnCell);
            if (cells.Count == 0) continue;
            if (!occupancy.TryClaim(entity.InstanceId, cells))
                throw new InvalidDataException($"SCN building footprint overlaps at entity instance {entity.InstanceId}.");
        }

        return new ScenarioWorld(entities, scenario.Vents.ToArray(), occupancy);
    }
}
