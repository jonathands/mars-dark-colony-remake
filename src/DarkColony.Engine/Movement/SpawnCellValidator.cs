using DarkColony.Engine.World;

namespace DarkColony.Engine.Movement;

/// <summary>
/// Executable-confirmed autonomous spawn placement. This is deliberately not
/// the complete local-path neighbor predicate.
/// </summary>
public sealed class SpawnCellValidator
{
    private readonly PathRegionMap path;
    private readonly CellOccupancy groundOccupancy;
    private readonly CellOccupancy alternateOccupancy;

    public SpawnCellValidator(PathRegionMap path, CellOccupancy groundOccupancy, CellOccupancy alternateOccupancy)
    {
        this.path = path;
        this.groundOccupancy = groundOccupancy;
        this.alternateOccupancy = alternateOccupancy;
    }

    public bool IsValid(CellCoordinate cell, int movementClass)
    {
        if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height) return false;
        return movementClass == 0
            ? path.RegionAt(cell) != 0 && !groundOccupancy.IsOccupied(cell)
            : !alternateOccupancy.IsOccupied(cell);
    }

    public CellCoordinate? FindNearestValid(CellCoordinate origin, int movementClass)
    {
        var maximumRadius = Math.Max(path.Width, path.Height);
        for (var radius = 0; radius <= maximumRadius; radius++)
        for (var x = origin.X - radius; x <= origin.X + radius; x++)
        for (var z = origin.Z - radius; z <= origin.Z + radius; z++)
        {
            var candidate = new CellCoordinate(x, z);
            if (IsValid(candidate, movementClass)) return candidate;
        }

        return null;
    }
}
