using DarkColony.Engine.World;

namespace DarkColony.Engine.Movement;

public enum DiagnosticPathTermination
{
    ReachedTarget,
    SegmentLimit,
    NoRoute,
    InvalidEndpoint,
}

public sealed record DiagnosticLocalPath(
    PackedLocalPath Steps,
    IReadOnlyList<CellCoordinate> Cells,
    DiagnosticPathTermination Termination);

/// <summary>
/// Evidence-bounded local search for visualization. Neighbor order and costs
/// are not claimed to match dc.exe until the native priority buckets are traced.
/// </summary>
public sealed class DiagnosticLocalPathfinder
{
    private readonly PathRegionMap path;
    private readonly CellOccupancy groundOccupancy;
    private readonly CellOccupancy alternateOccupancy;

    public DiagnosticLocalPathfinder(PathRegionMap path, CellOccupancy groundOccupancy, CellOccupancy alternateOccupancy)
    {
        this.path = path;
        this.groundOccupancy = groundOccupancy;
        this.alternateOccupancy = alternateOccupancy;
    }

    public DiagnosticLocalPath Find(CellCoordinate start, CellCoordinate target, int movementClass, int movingInstanceId)
    {
        if (!InBounds(start) || !InBounds(target)) return Empty(DiagnosticPathTermination.InvalidEndpoint);
        var coarse = path.BuildCoarseRoute(path.RegionAt(start), path.RegionAt(target));
        if (movementClass == 0 && coarse.Termination != CoarseRouteTermination.ReachedTarget)
            return Empty(DiagnosticPathTermination.NoRoute);
        var allowedRegions = movementClass == 0 ? coarse.Regions.ToHashSet() : null;

        var frontier = new Queue<CellCoordinate>();
        var predecessor = new Dictionary<CellCoordinate, (CellCoordinate Cell, PathDirection Direction)>();
        frontier.Enqueue(start);
        predecessor[start] = default;
        while (frontier.Count != 0 && !predecessor.ContainsKey(target))
        {
            var current = frontier.Dequeue();
            foreach (var direction in Enum.GetValues<PathDirection>())
            {
                var delta = direction.Delta();
                var next = new CellCoordinate(current.X + delta.X, current.Z + delta.Z);
                if (predecessor.ContainsKey(next) || !CanEnter(next, movementClass, movingInstanceId, allowedRegions)) continue;
                predecessor[next] = (current, direction);
                frontier.Enqueue(next);
            }
        }

        if (!predecessor.ContainsKey(target)) return Empty(DiagnosticPathTermination.NoRoute);
        var reversed = new List<(CellCoordinate Cell, PathDirection Direction)>();
        for (var current = target; current != start;)
        {
            var entry = predecessor[current];
            reversed.Add((current, entry.Direction));
            current = entry.Cell;
        }
        reversed.Reverse();

        var packed = new PackedLocalPath();
        var cells = new List<CellCoordinate> { start };
        foreach (var step in reversed.Take(PackedLocalPath.MaximumSteps))
        {
            packed.Append(step.Direction);
            cells.Add(step.Cell);
        }
        var termination = reversed.Count <= PackedLocalPath.MaximumSteps
            ? DiagnosticPathTermination.ReachedTarget
            : DiagnosticPathTermination.SegmentLimit;
        return new DiagnosticLocalPath(packed, cells, termination);
    }

    private bool CanEnter(CellCoordinate cell, int movementClass, int movingInstanceId, HashSet<byte>? allowedRegions)
    {
        if (!InBounds(cell)) return false;
        var occupancy = movementClass == 0 ? groundOccupancy : alternateOccupancy;
        if (occupancy.TryGetOwner(cell, out var owner) && owner != movingInstanceId) return false;
        return movementClass != 0 || (path.RegionAt(cell) != 0 && allowedRegions!.Contains(path.RegionAt(cell)));
    }

    private bool InBounds(CellCoordinate cell) => (uint)cell.X < (uint)path.Width && (uint)cell.Z < (uint)path.Height;

    private static DiagnosticLocalPath Empty(DiagnosticPathTermination termination) => new(new PackedLocalPath(), [], termination);
}
