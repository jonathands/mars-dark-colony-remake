using DarkColony.Engine.Movement;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// Stepping aside for a blocked ally. A path step that finds an allied actor
/// in its next cell stores its travel direction in that actor's byte
/// <c>+0x35</c> (<c>0x415795</c>); the blocker's idle command then moves one
/// cell aside through <c>0x412BC8</c> and clears the byte.
/// </summary>
public sealed partial class ScenarioSimulation
{
    // Compass rotation order of the eight direction indices (0x479248) and its
    // inverse (0x479268); PathDirection uses the same indices as 0x479208.
    private static readonly int[] YieldRotationToDirection = [0, 1, 2, 4, 7, 6, 5, 3];
    private static readonly int[] YieldDirectionToRotation = [0, 1, 2, 7, 3, 6, 5, 4];
    // Preference offsets from the blocked mover's direction: sideways first,
    // never straight back toward the mover (0x479288, idle without a target).
    private static readonly int[] YieldAsideOffsets = [2, -2, 1, -1, 3, -3, 0];
    // Straight ahead first (0x4792A4, idle with a hostile in weapon range).
    private static readonly int[] YieldAheadOffsets = [0, 1, -1, 2, -2];

    /// <summary>
    /// Consumes the yield notification (<c>0x412BC8</c>). Mode 1 tries the
    /// sideways order and falls back to a shuffled order drawn from the shared
    /// random stream (<c>0x412820</c>); mode 2 tries the ahead order only
    /// (<c>0x412A50</c>). A chosen cell becomes a plain move pushed by the idle
    /// command. The notification is cleared either way.
    /// </summary>
    private void YieldToBlockedAlly(SimulatedActor actor, bool hostileInRange)
    {
        var notified = (int)actor.YieldNotificationDirection!.Value;
        actor.YieldNotificationDirection = null;
        var origin = actor.Movement.OccupiedCell;
        var movementClass = EffectiveDefinition(actor).MovementClass;
        var direction = hostileInRange
            ? FindYieldDirection(origin, notified, movementClass, YieldAheadOffsets)
            : FindYieldDirection(origin, notified, movementClass, YieldAsideOffsets) ??
              FindShuffledYieldDirection(origin, notified, movementClass);
        if (direction is not { } chosen) return;
        var delta = ((PathDirection)chosen).Delta();
        var order = new ActiveMoveOrder(new CellCoordinate(origin.X + delta.X, origin.Z + delta.Z));
        actor.MoveOrder = order;
        actor.IdleIssuedMove = order;
        _ = StartSegment(actor);
    }

    /// <summary><c>0x4126A8</c> / <c>0x412A50</c>: the first empty, passable neighbor in preference order.</summary>
    private int? FindYieldDirection(CellCoordinate origin, int notified, int movementClass, int[] offsets)
    {
        var occupancy = movementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        foreach (var offset in offsets)
        {
            var direction = YieldRotationToDirection[(YieldDirectionToRotation[notified] + offset) & 7];
            if (IsYieldCell(origin, direction, cell => !occupancy.IsOccupied(cell))) return direction;
        }
        return null;
    }

    /// <summary>
    /// <c>0x412820</c>: shuffles the seven sideways preferences with the shared
    /// stream and also accepts a cell held by a live actor (native state 1).
    /// </summary>
    private int? FindShuffledYieldDirection(CellCoordinate origin, int notified, int movementClass)
    {
        var occupancy = movementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        Span<int> order = [0, 1, 2, 3, 4, 5, 6];
        for (var index = 0; index < order.Length; index++)
        {
            var pick = (int)(NextNativeRandom() % (uint)(order.Length - index)) + index;
            (order[pick], order[index]) = (order[index], order[pick]);
            var direction = YieldRotationToDirection[(YieldDirectionToRotation[notified] + YieldAsideOffsets[order[index]]) & 7];
            if (IsYieldCell(origin, direction, cell =>
                    !occupancy.TryGetOwner(cell, out var ownerId) ||
                    actorsById.TryGetValue(ownerId, out var owner) && !owner.IsDestroyed)) return direction;
        }
        return null;
    }

    /// <summary>
    /// A yield cell is in bounds, on a nonzero PTH region (cell record +0x0C,
    /// filled by <c>0x442B7C</c>), and accepted by <paramref name="free"/>; a
    /// diagonal also needs one passable orthogonal neighbor.
    /// </summary>
    private bool IsYieldCell(CellCoordinate origin, int direction, Func<CellCoordinate, bool> free)
    {
        var delta = ((PathDirection)direction).Delta();
        var cell = new CellCoordinate(origin.X + delta.X, origin.Z + delta.Z);
        if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height) return false;
        if (!free(cell) || path.RegionAt(cell) == 0) return false;
        return delta.X == 0 || delta.Z == 0 ||
               path.RegionAt(new CellCoordinate(cell.X, origin.Z)) != 0 ||
               path.RegionAt(new CellCoordinate(origin.X, cell.Z)) != 0;
    }
}
