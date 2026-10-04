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
    /// <summary>
    /// An order replaces the command stack, but the step in flight (type 5,
    /// <c>0x4125BC</c>) finishes its cell transition first. The interrupted
    /// move command applies the pending order when it next runs
    /// (<c>0x4158A8</c>). Removing a dead actor still cancels outright.
    /// </summary>
    private static void StopAfterCurrentStep(SimulatedActor actor)
    {
        if (actor.Playback is { } playback && playback.FinishCurrentStepOnly()) actor.FinishingStep = playback;
        actor.Playback = null;
    }

    private MoveCommandOutcome StartSegment(SimulatedActor actor)
    {
        var order = actor.MoveOrder ?? throw new InvalidOperationException("Actor has no move order.");
        if (actor.FinishingStep is not null)
            return new MoveCommandOutcome(actor.Seed.InstanceId, order.Target, DiagnosticPathTermination.StepInFlight, 0);
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

        var finder = localPathfinder ??= new DiagnosticLocalPathfinder(path, GroundOccupancy, AlternateOccupancy);
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

    /// <summary>
    /// Blocked path step (<c>0x415458</c>). The remaining packed steps are
    /// walked for the first cell that is free in the mover's grid:
    /// <list type="bullet">
    /// <item>None free (for example an occupied destination): the move target
    /// is jittered by -1..1 cells in X and then Z from the shared stream
    /// (<c>0x4155D5</c>) and the move restarts; it ends if the jittered target
    /// is the mover's own cell.</item>
    /// <item>A free cell: route to it and keep the old steps after it when the
    /// total fits in 31 (<c>0x41518C</c>). If that route fails, the blocking
    /// ally is told to step aside in the direction of the step reaching the
    /// free cell, and the mover waits four executions.</item>
    /// </list>
    /// The wait is the idle command's type-3 wait (<c>0x4122C8</c>): a health
    /// change ends it in the same update, otherwise it ends one update after
    /// its counter reaches zero. The move then retries the steps it kept,
    /// starting with the blocked one (<see cref="ResumeKeptSteps"/>).
    /// </summary>
    private void HandleBlockedStep(SimulatedActor mover, IReadOnlyList<(PathDirection Direction, CellCoordinate Cell)> remaining)
    {
        var order = mover.MoveOrder!;
        var definition = EffectiveDefinition(mover);
        var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        var freeIndex = -1;
        for (var index = 0; index < remaining.Count; index++)
        {
            if (occupancy.IsOccupied(remaining[index].Cell)) continue;
            freeIndex = index;
            break;
        }

        if (freeIndex < 0)
        {
            var target = order.Target;
            var jittered = new CellCoordinate(
                Math.Clamp(target.X + NextMovementJitter(), 0, path.Width - 1),
                Math.Clamp(target.Z + NextMovementJitter(), 0, path.Height - 1));
            if (jittered == mover.Movement.OccupiedCell)
            {
                if (order.AdvanceWaypoint()) _ = StartSegment(mover);
                else mover.MoveOrder = null;
                return;
            }
            order.JitterTarget(jittered);
            _ = StartSegment(mover);
            return;
        }

        var free = remaining[freeIndex];
        var finder = localPathfinder ??= new DiagnosticLocalPathfinder(path, GroundOccupancy, AlternateOccupancy);
        var route = finder.Find(mover.Movement.OccupiedCell, free.Cell, definition.MovementClass, mover.Seed.InstanceId);
        if (route.Steps.Count != 0)
        {
            var steps = new PackedLocalPath();
            for (var index = 0; index < route.Steps.Count; index++) steps.Append(route.Steps[index]);
            if (route.Steps.Count + remaining.Count - freeIndex - 1 < PackedLocalPath.MaximumSteps)
                for (var index = freeIndex + 1; index < remaining.Count; index++) steps.Append(remaining[index].Direction);
            mover.Playback = new PackedPathPlayback(mover.Seed.InstanceId, definition.MovementSpeed, mover.Movement, steps, occupancy, mover.Facing);
            order.SegmentCount++;
            return;
        }

        if (occupancy.TryGetOwner(remaining[0].Cell, out var blockerId) &&
            actorsById.TryGetValue(blockerId, out var blocker) &&
            TeamRelations.Relation(mover.Seed.Team, blocker.Seed.Team) != 0 &&
            blocker.YieldNotificationDirection is null)
        {
            blocker.YieldNotificationDirection = free.Direction;
        }
        order.BlockedTicksRemaining = 4;
        order.BlockedWaiting = true;
        order.BlockedWaitHealth = mover.Health;
        order.KeptSteps = remaining.Select(step => step.Direction).ToArray();
    }

    /// <summary>The move record retries its kept steps after the blocked-step wait.</summary>
    private void ResumeKeptSteps(SimulatedActor mover, ActiveMoveOrder order)
    {
        order.BlockedWaiting = false;
        order.BlockedTicksRemaining = 0;
        var steps = new PackedLocalPath();
        foreach (var direction in order.KeptSteps.Take(PackedLocalPath.MaximumSteps - 1)) steps.Append(direction);
        order.KeptSteps = [];
        var definition = EffectiveDefinition(mover);
        var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        mover.Playback = new PackedPathPlayback(mover.Seed.InstanceId, definition.MovementSpeed, mover.Movement, steps, occupancy, mover.Facing);
    }

    private int NextMovementJitter() => (int)(NextNativeRandom() % 3) - 1;

    private uint NextNativeRandom()
    {
        nativeRandomIndex = (nativeRandomIndex + 1) & 0xff;
        return nativeRandomTable[nativeRandomIndex];
    }
}
