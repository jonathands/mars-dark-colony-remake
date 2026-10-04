using DarkColony.Engine.World;

namespace DarkColony.Engine.Movement;

/// <summary>
/// Separates authoritative occupancy from sub-cell visual playback. Path and
/// command state will be added as their native contracts are implemented.
/// </summary>
public sealed class MovementState
{
    public MovementState(CellCoordinate occupiedCell)
    {
        OccupiedCell = occupiedCell;
        ReservedDestination = occupiedCell;
        VisualPosition = FixedPointPosition.AtCellCenter(occupiedCell);
    }

    public CellCoordinate OccupiedCell { get; private set; }
    public CellCoordinate ReservedDestination { get; private set; }
    public FixedPointPosition VisualPosition { get; private set; }

    public void ReserveDestination(CellCoordinate destination) => ReservedDestination = destination;

    public void AdvanceVisual(int xDeltaRaw, int zDeltaRaw) =>
        VisualPosition = VisualPosition.AddRaw(xDeltaRaw, zDeltaRaw);

    public void CommitArrival()
    {
        OccupiedCell = ReservedDestination;
    }

    /// <summary>Returns presentation to the authoritative logical cell after a cancelled transition.</summary>
    public void CancelTransition()
    {
        ReservedDestination = OccupiedCell;
        VisualPosition = FixedPointPosition.AtCellCenter(OccupiedCell);
    }

    /// <summary>
    /// Ends a cancelled transition on its reserved destination instead, for
    /// when the vacated source cell has since been claimed by another actor.
    /// </summary>
    public void CompleteTransitionAtDestination()
    {
        OccupiedCell = ReservedDestination;
        VisualPosition = FixedPointPosition.AtCellCenter(OccupiedCell);
    }
}
