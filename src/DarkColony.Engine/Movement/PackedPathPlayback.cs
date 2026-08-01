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
    public CellCoordinate? BlockedCell { get; private set; }

    public PackedPathPlaybackStatus Step()
    {
        if (BlockedCell is not null) return PackedPathPlaybackStatus.Blocked;
        if (transition is not null)
        {
            if (transition.Step()) return PackedPathPlaybackStatus.Interpolating;
            transition = null;
            return nextStep == path.Count ? PackedPathPlaybackStatus.Complete : PackedPathPlaybackStatus.Interpolating;
        }
        if (nextStep == path.Count) return PackedPathPlaybackStatus.Complete;

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
