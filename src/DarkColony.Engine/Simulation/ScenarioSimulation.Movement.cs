using DarkColony.Engine.Commands;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>Local route segments, blocked-route repair, and the shared native random cursor.</summary>
public sealed partial class ScenarioSimulation
{
    private MoveCommandOutcome StartSegment(SimulatedActor actor)
    {
        var order = actor.MoveOrder ?? throw new InvalidOperationException("Actor has no move order.");
        if (actor.Movement.OccupiedCell == order.Target)
        {
            // A queued duplicate of the active target is a legal input edge:
            // the player can append while the first segment is still in
            // flight. Do not discard the remainder of the queue merely
            // because this zero-length segment needs no packed playback.
            if (order.AdvanceWaypoint()) return StartSegment(actor);
            actor.MoveOrder = null;
            return new MoveCommandOutcome(actor.Seed.InstanceId, order.Target, DiagnosticPathTermination.ReachedTarget, 0);
        }

        var finder = new DiagnosticLocalPathfinder(path, GroundOccupancy, AlternateOccupancy);
        var definition = EffectiveDefinition(actor);
        var local = finder.Find(actor.Movement.OccupiedCell, order.Target, definition.MovementClass, actor.Seed.InstanceId);
        if (local.Steps.Count == 0)
        {
            // A route failure is not a completed order. Keep it active and
            // retry at the recovered four-execution blocked cadence.
            order.BlockedTicksRemaining = 4;
            return new MoveCommandOutcome(actor.Seed.InstanceId, order.Target, local.Termination, 0);
        }

        var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        actor.Playback = new PackedPathPlayback(actor.Seed.InstanceId, definition.MovementSpeed, actor.Movement, local.Steps, occupancy, actor.Facing);
        order.SegmentCount++;
        return new MoveCommandOutcome(actor.Seed.InstanceId, order.Target, local.Termination, local.Steps.Count);
    }

    private void NotifyAndJitterBlockedActor(SimulatedActor mover, CellCoordinate? blockedCell)
    {
        if (mover.MoveOrder is null) return;
        var definition = EffectiveDefinition(mover);
        var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        if (blockedCell is { } cell && occupancy.TryGetOwner(cell, out var blockerId) &&
            actorsById.TryGetValue(blockerId, out var blocker) &&
            TeamRelations.Relation(mover.Seed.Team, blocker.Seed.Team) != 0 &&
            blocker.YieldNotificationDirection is null)
        {
            blocker.YieldNotificationDirection = DirectionBetween(mover.Movement.OccupiedCell, cell);
        }

        var target = mover.MoveOrder.Target;
        var jittered = new CellCoordinate(
            Math.Clamp(target.X + NextMovementJitter(), 0, path.Width - 1),
            Math.Clamp(target.Z + NextMovementJitter(), 0, path.Height - 1));
        if (jittered != mover.Movement.OccupiedCell)
        {
            mover.MoveOrder.JitterTarget(jittered);
            var retry = StartSegment(mover);
            if (retry.StepCount != 0) return;
        }
        mover.MoveOrder.BlockedTicksRemaining = 4;
    }

    private int NextMovementJitter() => (int)(NextNativeRandom() % 3) - 1;

    private uint NextNativeRandom()
    {
        nativeRandomIndex = (nativeRandomIndex + 1) & 0xff;
        return nativeRandomTable[nativeRandomIndex];
    }

    private static PathDirection DirectionBetween(CellCoordinate source, CellCoordinate target)
    {
        var dx = Math.Sign(target.X - source.X);
        var dz = Math.Sign(target.Z - source.Z);
        return (dx, dz) switch
        {
            (-1, -1) => PathDirection.NorthWest,
            (0, -1) => PathDirection.North,
            (1, -1) => PathDirection.NorthEast,
            (-1, 0) => PathDirection.West,
            (1, 0) => PathDirection.East,
            (-1, 1) => PathDirection.SouthWest,
            (0, 1) => PathDirection.South,
            (1, 1) => PathDirection.SouthEast,
            _ => throw new InvalidOperationException("Blocked-cell direction cannot be zero."),
        };
    }
}
