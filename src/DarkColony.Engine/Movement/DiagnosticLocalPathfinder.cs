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
/// Recovered local search.  The executable assigns every neighbour a bucket
/// priority from the target-relative 9x9 table at 0x47A9B4, then maintains
/// linked priority buckets at map+0x86ca4.  This is deliberately not a modern
/// Euclidean/A* cost: the values below are the decoded native bucket costs.
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

        var frontier = new PriorityQueue<(CellCoordinate Cell, long Sequence), (int Cost, long TieBreak)>();
        var predecessor = new Dictionary<CellCoordinate, (CellCoordinate Cell, PathDirection Direction)>();
        var costs = new Dictionary<CellCoordinate, int> { [start] = 0 };
        long sequence = 0;
        frontier.Enqueue((start, sequence++), (0, 0));
        predecessor[start] = default;
        while (frontier.Count != 0 && !predecessor.ContainsKey(target))
        {
            var entry = frontier.Dequeue();
            var current = entry.Cell;
            var currentCost = costs[current];
            // 0x443312 constructs all nine target-relative candidates in
            // table order.  Equal-priority native bucket entries are linked
            // at the head, hence the descending table index tie break.
            foreach (var candidate in OrderedCandidates(current, target))
            {
                var direction = candidate.Direction;
                var delta = direction.Delta();
                var next = new CellCoordinate(current.X + delta.X, current.Z + delta.Z);
                if (!CanEnter(next, movementClass, movingInstanceId, allowedRegions)) continue;
                var nextCost = checked(currentCost + candidate.Cost);
                if (costs.TryGetValue(next, out var existing) && existing <= nextCost) continue;
                costs[next] = nextCost;
                predecessor[next] = (current, direction);
                frontier.Enqueue((next, sequence++), (nextCost, -candidate.TableIndex));
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

    private static IEnumerable<(PathDirection Direction, int Cost, int TableIndex)> OrderedCandidates(CellCoordinate current, CellCoordinate target)
    {
        var targetColumn = Math.Sign(target.X - current.X) + 1;
        var targetRow = Math.Sign(target.Z - current.Z) + 1;
        // 0x4432DE builds the table row as (target-X sign * 3) +
        // target-Z sign, rather than conventional row-major Z/X indexing.
        var priorities = NativePriorityTable[targetColumn * 3 + targetRow];
        return NativeCandidateDirections
            .Select((direction, index) => (direction, priorities[index], index))
            .Where(candidate => candidate.direction is not null)
            .Select(candidate => (candidate.direction!.Value, candidate.Item2, candidate.index))
            .OrderBy(candidate => candidate.Item2)
            .ThenByDescending(candidate => candidate.index);
    }

    // Candidate columns are the padded-cell layout used by 0x443298:
    // NW,W,SW,N,C,S,NE,E,SE.  Rows are target X/Z signs (-,0,+).
    private static readonly PathDirection?[] NativeCandidateDirections =
    [
        PathDirection.NorthWest, PathDirection.West, PathDirection.SouthWest,
        PathDirection.North, null, PathDirection.South,
        PathDirection.NorthEast, PathDirection.East, PathDirection.SouthEast,
    ];

    private static readonly int[][] NativePriorityTable =
    [
        [1, 10, 20, 10, 90, 50, 20, 50, 70],
        [10, 1, 10, 20, 90, 20, 70, 50, 70],
        [20, 10, 1, 50, 90, 10, 70, 50, 20],
        [10, 20, 50, 1, 90, 70, 10, 20, 50],
        [80, 80, 80, 80, 90, 80, 80, 80, 80],
        [50, 20, 10, 70, 90, 1, 50, 20, 10],
        [20, 50, 70, 10, 90, 50, 1, 10, 20],
        [70, 50, 70, 20, 90, 20, 10, 1, 10],
        [70, 50, 20, 50, 90, 10, 20, 10, 1],
    ];

    private static DiagnosticLocalPath Empty(DiagnosticPathTermination termination) => new(new PackedLocalPath(), [], termination);
}
