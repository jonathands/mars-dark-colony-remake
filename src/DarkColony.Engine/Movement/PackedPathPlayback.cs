using DarkColony.Engine.World;

namespace DarkColony.Engine.Movement;

public enum PackedPathPlaybackStatus
{
    ReservedStep,
    Interpolating,
    Complete,
    Blocked,
}

/// <summary>Recovered command-6/command-5 boundary for one actor.</summary>
public sealed class PackedPathPlayback
{
    private readonly int entityInstanceId;
    private readonly int speed;
    private readonly PackedLocalPath path;
    private readonly CellOccupancy occupancy;
    private NativeCellTransition? transition;
    private int nextStep;
    private int? stopAfter;

    public PackedPathPlayback(
        int entityInstanceId,
        int speed,
        MovementState movement,
        PackedLocalPath path,
        CellOccupancy occupancy,
        FacingState? facing = null)
    {
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        this.entityInstanceId = entityInstanceId;
        this.speed = speed;
        Movement = movement;
        this.path = path;
        this.occupancy = occupancy;
        Facing = facing ?? new FacingState();
    }

    public MovementState Movement { get; }
    public FacingState Facing { get; }
    public int NextStep => nextStep;
    /// <summary>True between cell transitions, where the native path-step handler runs.</summary>
    public bool IsBetweenSteps => transition is null;
    public CellCoordinate? BlockedCell { get; private set; }
    /// <summary>True only on the execution which finishes a command-5 cell transition.</summary>
    public bool CompletedTransitionLastStep { get; private set; }

    /// <summary>
    /// Cancels a reserved in-flight step by restoring its source claim. Only
    /// world removal uses it: an order lets the step finish instead
    /// (<see cref="FinishCurrentStepOnly"/>), because the native step command
    /// never checks for a pending order.
    /// </summary>
    /// <remarks>
    /// The source cell is released when a step starts, so another actor may
    /// have entered it before the cancel. The step then completes on the
    /// destination this actor still owns rather than overlapping that actor
    /// (provisional port policy, not a recovered native rule).
    /// </remarks>
    public void Cancel()
    {
        if (transition is null) return;
        transition = null;
        if (occupancy.TryMove(entityInstanceId, Movement.ReservedDestination, Movement.OccupiedCell))
        {
            Movement.CancelTransition();
            return;
        }
        if (occupancy.TryGetOwner(Movement.ReservedDestination, out var owner) && owner == entityInstanceId)
        {
            Movement.CompleteTransitionAtDestination();
            return;
        }
        throw new InvalidOperationException("Cancelled movement owns neither its source nor its destination cell.");
    }

    /// <summary>The steps not yet taken, starting with a blocked one, and the cell each enters.</summary>
    public IReadOnlyList<(PathDirection Direction, CellCoordinate Cell)> RemainingSteps()
    {
        var steps = new List<(PathDirection, CellCoordinate)>();
        var cell = Movement.OccupiedCell;
        for (var index = nextStep; index < path.Count; index++)
        {
            cell = cell.Offset(path[index].Delta());
            steps.Add((path[index], cell));
        }
        return steps;
    }

    /// <summary>
    /// Keeps only the cell transition in flight: the playback completes when
    /// it ends. Returns false when no transition is in flight.
    /// </summary>
    public bool FinishCurrentStepOnly()
    {
        if (transition is null) return false;
        stopAfter = nextStep;
        return true;
    }

    public PackedPathPlaybackStatus Step()
    {
        CompletedTransitionLastStep = false;
        var end = stopAfter ?? path.Count;
        if (BlockedCell is not null) return PackedPathPlaybackStatus.Blocked;
        if (transition is not null)
        {
            if (transition.Step()) return PackedPathPlaybackStatus.Interpolating;
            transition = null;
            CompletedTransitionLastStep = true;
            return nextStep == end ? PackedPathPlaybackStatus.Complete : PackedPathPlaybackStatus.Interpolating;
        }
        if (nextStep == end) return PackedPathPlaybackStatus.Complete;

        var direction = path[nextStep];
        var destination = Movement.OccupiedCell.Offset(direction.Delta());
        if (!occupancy.TryMove(entityInstanceId, Movement.OccupiedCell, destination))
        {
            BlockedCell = destination;
            return PackedPathPlaybackStatus.Blocked;
        }

        transition = NativeCellTransition.Begin(Movement, direction, speed);
        Facing.Face(direction);
        nextStep++;
        return PackedPathPlaybackStatus.ReservedStep;
    }
}
