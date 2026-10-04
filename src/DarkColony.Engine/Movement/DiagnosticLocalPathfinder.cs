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

    // Per-search scratch reused across searches. A cell's cost and predecessor
    // are meaningful only while its stamp equals the current search's stamp,
    // which replaces clearing per-search dictionaries.
    private readonly int[] costs;
    private readonly int[] predecessorCells;
    private readonly PathDirection[] predecessorDirections;
    private readonly int[] stamps;
    private readonly bool[] enterable;
    private readonly int[] enterableStamps;
    private int stamp;
    private readonly bool[] allowedRegions = new bool[256];
    private readonly PriorityQueue<(CellCoordinate Cell, long Sequence), (int Cost, long TieBreak)> frontier = new();

    public DiagnosticLocalPathfinder(PathRegionMap path, CellOccupancy groundOccupancy, CellOccupancy alternateOccupancy)
    {
        this.path = path;
        this.groundOccupancy = groundOccupancy;
        this.alternateOccupancy = alternateOccupancy;
        var cellCount = path.Width * path.Height;
        costs = new int[cellCount];
        predecessorCells = new int[cellCount];
        predecessorDirections = new PathDirection[cellCount];
        stamps = new int[cellCount];
        enterable = new bool[cellCount];
        enterableStamps = new int[cellCount];
    }

    public DiagnosticLocalPath Find(CellCoordinate start, CellCoordinate target, int movementClass, int movingInstanceId)
    {
        if (!InBounds(start) || !InBounds(target)) return Empty(DiagnosticPathTermination.InvalidEndpoint);
        var coarse = path.BuildCoarseRoute(path.RegionAt(start), path.RegionAt(target));
        if (movementClass == 0 && coarse.Termination != CoarseRouteTermination.ReachedTarget)
            return Empty(DiagnosticPathTermination.NoRoute);
        var restrictRegions = movementClass == 0;
        if (restrictRegions)
        {
            Array.Clear(allowedRegions);
            foreach (var region in coarse.Regions) allowedRegions[region] = true;
        }

        BeginSearch();
        // The queue sees exactly the enqueue/dequeue sequence of a fresh queue,
        // so equal-priority ties still resolve identically.
        frontier.Clear();
        long sequence = 0;
        var startIndex = IndexOf(start);
        var targetIndex = IndexOf(target);
        // A cell is recorded only after passing CanEnter, so an unenterable
        // target (for example, one occupied by the unit being attacked) can
        // never be reached: answer without flooding the whole region.
        if (targetIndex != startIndex && !CanEnter(target, movementClass, movingInstanceId, restrictRegions))
            return Empty(DiagnosticPathTermination.NoRoute);
        Record(startIndex, 0, startIndex, default);
        frontier.Enqueue((start, sequence++), (0, 0));
        while (frontier.Count != 0 && !IsRecorded(targetIndex))
        {
            frontier.TryDequeue(out var entry, out var priority);
            var current = entry.Cell;
            var currentIndex = IndexOf(current);
            var currentCost = costs[currentIndex];
            // A cell re-enqueued at a lower cost leaves its older entry behind.
            // The cheaper entry always dequeues first and expands every
            // neighbor; expanding the older one again cannot record anything,
            // so skipping it is exact, not an approximation.
            if (priority.Cost > currentCost) continue;
            // 0x443312 constructs all nine target-relative candidates in
            // table order.  Equal-priority native bucket entries are linked
            // at the head, hence the descending table index tie break.
            foreach (var candidate in OrderedCandidates(current, target))
            {
                var direction = candidate.Direction;
                var delta = direction.Delta();
                var next = new CellCoordinate(current.X + delta.X, current.Z + delta.Z);
                if (!CanEnter(next, movementClass, movingInstanceId, restrictRegions)) continue;
                var nextCost = checked(currentCost + candidate.Cost);
                var nextIndex = IndexOf(next);
                if (IsRecorded(nextIndex) && costs[nextIndex] <= nextCost) continue;
                Record(nextIndex, nextCost, currentIndex, direction);
                frontier.Enqueue((next, sequence++), (nextCost, -candidate.TableIndex));
            }
        }

        if (!IsRecorded(targetIndex)) return Empty(DiagnosticPathTermination.NoRoute);
        var reversed = new List<(CellCoordinate Cell, PathDirection Direction)>();
        for (var current = targetIndex; current != startIndex; current = predecessorCells[current])
            reversed.Add((CellAt(current), predecessorDirections[current]));
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

    private bool CanEnter(CellCoordinate cell, int movementClass, int movingInstanceId, bool restrictRegions)
    {
        if (!InBounds(cell)) return false;
        // Occupancy and regions are fixed for the duration of one search, so
        // each cell's answer is computed once even though up to eight
        // neighbors ask for it.
        var index = IndexOf(cell);
        if (enterableStamps[index] == stamp) return enterable[index];
        enterableStamps[index] = stamp;
        return enterable[index] = ComputeCanEnter(cell, movementClass, movingInstanceId, restrictRegions);
    }

    private bool ComputeCanEnter(CellCoordinate cell, int movementClass, int movingInstanceId, bool restrictRegions)
    {
        var occupancy = movementClass == 0 ? groundOccupancy : alternateOccupancy;
        if (occupancy.TryGetOwner(cell, out var owner) && owner != movingInstanceId) return false;
        if (!restrictRegions) return true;
        var region = path.RegionAt(cell);
        return region != 0 && allowedRegions[region];
    }

    private bool InBounds(CellCoordinate cell) => (uint)cell.X < (uint)path.Width && (uint)cell.Z < (uint)path.Height;

    private int IndexOf(CellCoordinate cell) => cell.Z * path.Width + cell.X;

    private CellCoordinate CellAt(int index) => new(index % path.Width, index / path.Width);

    private bool IsRecorded(int index) => stamps[index] == stamp;

    private void Record(int index, int cost, int predecessorIndex, PathDirection direction)
    {
        stamps[index] = stamp;
        costs[index] = cost;
        predecessorCells[index] = predecessorIndex;
        predecessorDirections[index] = direction;
    }

    private void BeginSearch()
    {
        if (++stamp != int.MaxValue) return;
        Array.Clear(stamps);
        Array.Clear(enterableStamps);
        stamp = 1;
    }

    private static (PathDirection Direction, int Cost, int TableIndex)[] OrderedCandidates(CellCoordinate current, CellCoordinate target)
    {
        var targetColumn = Math.Sign(target.X - current.X) + 1;
        var targetRow = Math.Sign(target.Z - current.Z) + 1;
        // 0x4432DE builds the table row as (target-X sign * 3) +
        // target-Z sign, rather than conventional row-major Z/X indexing.
        return OrderedCandidateTable[targetColumn * 3 + targetRow];
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

    // Each table row in visiting order: ascending priority, ties by
    // descending table index. Declared after the two tables it reads, since
    // static initializers run in textual order.
    private static readonly (PathDirection Direction, int Cost, int TableIndex)[][] OrderedCandidateTable =
        NativePriorityTable.Select(priorities => NativeCandidateDirections
            .Select((direction, index) => (direction, priorities[index], index))
            .Where(candidate => candidate.direction is not null)
            .Select(candidate => (candidate.direction!.Value, candidate.Item2, candidate.index))
            .OrderBy(candidate => candidate.Item2)
            .ThenByDescending(candidate => candidate.index)
            .ToArray())
        .ToArray();

    private static DiagnosticLocalPath Empty(DiagnosticPathTermination termination) => new(new PackedLocalPath(), [], termination);
}
