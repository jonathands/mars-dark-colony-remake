using DarkColony.Engine.World;

namespace DarkColony.Engine.Movement;

public readonly record struct NativeDirectionVector(short X, short Z)
{
    public const int Scale = 2048;

    public static NativeDirectionVector For(PathDirection direction) => direction switch
    {
        PathDirection.NorthWest => new(-1448, -1448),
        PathDirection.North => new(0, -2048),
        PathDirection.NorthEast => new(1448, -1448),
        PathDirection.West => new(-2048, 0),
        PathDirection.East => new(2048, 0),
        PathDirection.SouthWest => new(-1448, 1448),
        PathDirection.South => new(0, 2048),
        PathDirection.SouthEast => new(1448, 1448),
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };
}

/// <summary>Recovered command-5 integer interpolation for one reserved cell step.</summary>
public sealed class NativeCellTransition
{
    private readonly MovementState movement;

    private NativeCellTransition(MovementState movement, int velocityXRaw, int velocityZRaw, int executions)
    {
        this.movement = movement;
        VelocityXRaw = velocityXRaw;
        VelocityZRaw = velocityZRaw;
        RemainingExecutions = executions;
    }

    public int VelocityXRaw { get; }
    public int VelocityZRaw { get; }
    public int RemainingExecutions { get; private set; }
    public bool IsComplete { get; private set; }

    public static NativeCellTransition Begin(MovementState movement, PathDirection direction, int speed)
    {
        ArgumentNullException.ThrowIfNull(movement);
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        var destination = movement.OccupiedCell.Offset(direction.Delta());
        movement.ReserveDestination(destination);
        var target = FixedPointPosition.AtCellCenter(destination);
        var deltaX = target.XRaw - movement.VisualPosition.XRaw;
        var deltaZ = target.ZRaw - movement.VisualPosition.ZRaw;
        var vector = NativeDirectionVector.For(direction);
        var projectedDistance = (vector.X * deltaX + vector.Z * deltaZ) / NativeDirectionVector.Scale;
        var executions = projectedDistance / speed;
        return new NativeCellTransition(
            movement,
            vector.X * speed / NativeDirectionVector.Scale,
            vector.Z * speed / NativeDirectionVector.Scale,
            executions);
    }

    /// <returns>True while command 5 remains queued; false when it removes itself.</returns>
    public bool Step()
    {
        if (IsComplete) return false;
        if (RemainingExecutions == 0)
        {
            movement.CommitArrival();
            IsComplete = true;
            return false;
        }

        movement.AdvanceVisual(VelocityXRaw, VelocityZRaw);
        RemainingExecutions--;
        return true;
    }
}
