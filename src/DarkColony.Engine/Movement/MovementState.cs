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
}
