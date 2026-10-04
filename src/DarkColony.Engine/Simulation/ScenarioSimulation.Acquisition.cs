using DarkColony.Engine.Data;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// Automatic target selection: the idle command (type 3, <c>0x4148B0</c>),
/// move modes 1 and 2 of the path-step handler (<c>0x4157EC</c>), and the
/// shared ring selector <c>0x435570</c>. Disabled when no ring table was
/// supplied (engine checks built without the executable).
/// </summary>
public sealed partial class ScenarioSimulation
{
    /// <summary>Idle wait after an empty scan (<c>0x414CA0</c>), for the first three misses.</summary>
    public const int NativeIdleShortWaitTicks = 0x0f;
    /// <summary>Idle wait after the third consecutive empty scan (<c>0x414CA7</c>).</summary>
    public const int NativeIdleLongWaitTicks = 0x2d;
    /// <summary>Second-scan radius, in rings, for human-player ground units that were just damaged.</summary>
    public const int NativeIdleDamagedRadius = 9;
    /// <summary>Second-scan radius, in rings, for undamaged human-player ground units.</summary>
    public const int NativeIdleCalmRadius = 4;

    private void UpdateIdleCommand(SimulatedActor actor, TickEvents events)
    {
        if (targetRings is null) return;
        if (!IsIdle(actor))
        {
            // Approach, yield, and acquired-attack orders are pushed above the
            // idle record, which resumes when they end; any other order
            // replaces the command stack.
            if (!IsIdleIssuedOrder(actor)) actor.IdleCommandActive = false;
            return;
        }
        actor.IdleIssuedMove = null;
        actor.IdleIssuedAttackTarget = null;
        if (!actor.IdleCommandActive)
        {
            // Idle state 1 (0x412654) pushes command 3 with no target, the
            // current health, and a zero miss count.
            actor.IdleCommandActive = true;
            actor.IdleHealthSnapshot = actor.Health;
            actor.IdleMissCount = 0;
            actor.IdleWaiting = false;
            actor.IdleWaitTicks = 0;
            actor.IdleFidgetFacing = null;
        }
        if (actor.IdleWaiting)
        {
            // 0x4122C8: a health change ends the wait and the records below run
            // in this update; otherwise a zero counter ends it, and they run in
            // the next one.
            if (actor.Health != actor.IdleWaitHealth) actor.IdleWaiting = false;
            else if (actor.IdleWaitTicks == 0)
            {
                actor.IdleWaiting = false;
                return;
            }
            else
            {
                actor.IdleWaitTicks--;
                return;
            }
        }
        var definition = EffectiveDefinition(actor);
        if (actor.IdleFidgetFacing is { } fidget)
        {
            // 0x412358: turn toward the fidget bearing (0x4120FC, turn rate =
            // gamestat value 2). The step that arrives pops the record, and the
            // idle record runs in the same update.
            actor.Facing.Face(fidget);
            if (actor.Facing.Current != fidget && (definition.TurnSpeed <= 0 || actor.Facing.Step(definition.TurnSpeed))) return;
            actor.IdleFidgetFacing = null;
        }
        if (!TryGetWeapon(actor, out var weapon))
        {
            // Unarmed path 0x4149C0: a moving actor honors a yield notification
            // first. Vents, mines, and stealing stances never move; their own
            // handlers, like the healers' rest, are not part of this command.
            if (actor.YieldNotificationDirection is not null && definition.MovementSpeed > 0)
                YieldToBlockedAlly(actor, hostileInRange: false);
            return;
        }

        var target = ScanForTarget(actor, weapon, weapon.Range);
        // 0x414A99: a pending yield notification takes precedence over the
        // first scan's hit, which only selects the yield order.
        if (actor.YieldNotificationDirection is not null && definition.MovementSpeed > 0)
        {
            YieldToBlockedAlly(actor, hostileInRange: target is not null);
            return;
        }
        if (target is not null)
        {
            actor.IdleMissCount = 0;
            actor.AttackTargetInstanceId = target.Seed.InstanceId;
            actor.IdleIssuedAttackTarget = target.Seed.InstanceId;
            events.IdleAcquisitions.Add(new IdleAcquisitionEvent(actor.Seed.InstanceId, target.Seed.InstanceId, Approach: false));
            return;
        }

        var damaged = actor.Health < actor.IdleHealthSnapshot;
        if (damaged) actor.IdleMissCount = 0;
        actor.IdleHealthSnapshot = actor.Health;
        if (definition.MovementSpeed > 0 && SecondScanRadius(actor, definition, damaged) is { } radius &&
            ScanForTarget(actor, weapon, radius) is { } distant &&
            FindAttackApproachCell(actor, distant, weapon) is { } approachCell)
        {
            // 0x414BF8 stores the hostile's position and pushes a move in mode
            // 2, which stops as soon as anything enters weapon range. The
            // port's local search has no partial routes to an occupied cell,
            // so the move targets the same free approach cell that attack
            // pursuit uses (port adapter).
            // The handler returns 0 after the push (0x414C20), so the move's
            // first step waits for the next update; the actor update below
            // only builds its route this update.
            actor.IdleMissCount = 0;
            actor.MoveOrder = new ActiveMoveOrder(approachCell) { StopOnContact = true };
            actor.IdleIssuedMove = actor.MoveOrder;
            events.IdleAcquisitions.Add(new IdleAcquisitionEvent(actor.Seed.InstanceId, distant.Seed.InstanceId, Approach: true));
            return;
        }

        // Idle tail 0x414C29: one draw from the shared stream; a zero low
        // nibble draws a fidget bearing unless entity value 20 is set. The
        // fidget record goes below the wait, so it runs once the wait ends.
        if ((NextNativeRandom() & 0xf) == 0 && !definition.SuppressesIdleFidget)
            actor.IdleFidgetFacing = (byte)NextNativeRandom();
        if (actor.IdleMissCount < 3)
        {
            actor.IdleMissCount++;
            actor.IdleWaitTicks = NativeIdleShortWaitTicks;
        }
        else
        {
            actor.IdleWaitTicks = NativeIdleLongWaitTicks;
        }
        actor.IdleWaiting = true;
        actor.IdleWaitHealth = actor.Health;
    }

    /// <summary>
    /// Whether the actor is only busy with an order its own idle command
    /// issued. A player order clears the attack target or replaces the move.
    /// </summary>
    private static bool IsIdleIssuedOrder(SimulatedActor actor) =>
        actor.IdleCommandActive &&
        (actor.AttackTargetInstanceId is { } attackTarget
            ? attackTarget == actor.IdleIssuedAttackTarget
            : actor.MoveOrder is not null && ReferenceEquals(actor.MoveOrder, actor.IdleIssuedMove));

    /// <summary>
    /// 0x414B79: computer players and the internal teams 8/9 look 16 rings
    /// out; human players' ground units look 9 rings out after taking damage
    /// and 4 otherwise; human fliers do not look beyond weapon range.
    /// </summary>
    private int? SecondScanRadius(SimulatedActor actor, EntityDefinition definition, bool damaged)
    {
        if (actor.Seed.Team >= 8 || computerTeams.Contains(actor.Seed.Team)) return NativeTargetRings.MaximumRing;
        if (definition.MovementClass != 0) return null;
        return damaged ? NativeIdleDamagedRadius : NativeIdleCalmRadius;
    }

    /// <summary>
    /// An actor whose native command stack is down to the idle command. Critter
    /// team 9 runs the same handler; the loader makes it cooperative with every
    /// player, so its scans find nothing but still draw from the shared stream.
    /// </summary>
    private bool IsIdle(SimulatedActor actor) =>
        !actor.IsDestroyed && actor.Seed.Team is >= 0 and <= 9 && actor.Seed.Team != 8 &&
        actor.Playback is null && actor.MoveOrder is null &&
        actor.AttackTargetInstanceId is null && actor.AttackMoveDestination is null &&
        actor.GroundSpecialAttackTarget is null && actor.HarvestVentId is null &&
        actor.InspireCastTicksRemaining == 0 && actor.MineDeployTicksRemaining == 0 &&
        !EffectiveDefinition(actor).UsesNativeMineLayer;

    /// <summary>
    /// Move mode 2 (0x415AE5): before each path step, end the move if a hostile
    /// is within weapon range. The actor then returns to its idle command.
    /// </summary>
    private void StopMoveOnContact(SimulatedActor actor)
    {
        if (targetRings is null || actor.MoveOrder is not { StopOnContact: true } ||
            actor.Playback is not { IsBetweenSteps: true } || !TryGetWeapon(actor, out var weapon) ||
            ScanForTarget(actor, weapon, weapon.Range) is null) return;
        actor.Playback.Cancel();
        actor.Playback = null;
        actor.MoveOrder = null;
    }

    /// <summary>
    /// Move mode 1 (0x4159B3): attack-move acquisition, scanning weapon range at
    /// path-step boundaries (and while waiting between segments).
    /// </summary>
    private SimulatedActor? FindNativeAttackMoveTarget(SimulatedActor actor) =>
        (actor.Playback is null || actor.Playback.IsBetweenSteps) && TryGetWeapon(actor, out var weapon)
            ? ScanForTarget(actor, weapon, weapon.Range)
            : null;

    /// <summary>
    /// Ring selector <c>0x435570</c>. Walks rings 0..radius around the actor's
    /// current cell in table order, probing the ground, alternate, and mine
    /// grids of each visible cell, and keeps the highest-scoring eligible
    /// candidate; ties keep the first found.
    /// </summary>
    private SimulatedActor? ScanForTarget(SimulatedActor scanner, WeaponDefinition weapon, int radius)
    {
        var rings = targetRings!.Rings;
        var origin = scanner.Movement.VisualPosition.Cell;
        SimulatedActor? best = null;
        var bestScore = -1;
        for (var ring = 0; ring <= Math.Min(radius, rings.Count - 1); ring++)
        {
            foreach (var offset in rings[ring])
            {
                var cell = new CellCoordinate(origin.X + offset.X, origin.Z + offset.Z);
                if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height) continue;
                if (!IsCellVisibleForScan(scanner.Seed.Team, cell)) continue;
                foreach (var grid in (CellOccupancy[])[GroundOccupancy, AlternateOccupancy, MineOccupancy])
                {
                    if (!grid.TryGetOwner(cell, out var candidateId) || !actorsById.TryGetValue(candidateId, out var candidate)) continue;
                    if (!IsScanCandidate(scanner, weapon, candidate)) continue;
                    var score = ScanScore(scanner, weapon, candidate, cell);
                    if (score <= bestScore) continue;
                    best = candidate;
                    bestScore = score;
                }
            }
        }
        return best;
    }

    private bool IsScanCandidate(SimulatedActor scanner, WeaponDefinition weapon, SimulatedActor candidate)
    {
        if (candidate.IsDestroyed || candidate == scanner) return false;
        var definition = EffectiveDefinition(candidate);
        // 0x435829: another team's mine is a candidate only once a detector of
        // the scanner's team revealed it at the last visibility refresh.
        if (definition.UsesNativeMineLayer && candidate.Seed.Team != scanner.Seed.Team &&
            (scanner.Seed.Team is < 0 or > 7 || (candidate.RevealedTeamMask & (1 << scanner.Seed.Team)) == 0)) return false;
        if (definition.IsNativeUntargetable) return false;
        if (candidate.Seed.Team is < 0 or 8 or 9) return false;
        if (!TeamRelations.IsHostile(scanner.Seed.Team, candidate.Seed.Team)) return false;
        // The weapon must be able to damage the candidate's armor class.
        return damageMatrix is null || damageMatrix[weapon.WeaponClass, definition.ArmorClass] != 0;
    }

    /// <summary>
    /// 0x435A48: base 50, or 150 if the candidate is armed, plus 200 if it is a
    /// flier or building. Area weapons adjust by the 3x3 ground neighborhood
    /// (+10 hostile, -15 otherwise). For other weapons the native arithmetic
    /// multiplies by <c>(max(0x400 - health, 0 if below 0x400) + 0x400) &gt;&gt; 11</c>,
    /// which is zero for any living target, so the first candidate found wins.
    /// </summary>
    private int ScanScore(SimulatedActor scanner, WeaponDefinition weapon, SimulatedActor candidate, CellCoordinate cell)
    {
        var definition = EffectiveDefinition(candidate);
        var score = definition.WeaponSlots[0] != -1 ? 0x96 : 0x32;
        if (definition.MovementClass != 0) score += 0xc8;
        if (!weapon.HasAreaEffect)
        {
            var healthTerm = 0x400 - candidate.Health;
            if (healthTerm < 0x400) healthTerm = 0;
            return score * ((healthTerm + 0x400) >> 11);
        }
        for (var x = cell.X - 1; x <= cell.X + 1; x++)
        for (var z = cell.Z - 1; z <= cell.Z + 1; z++)
        {
            if ((uint)x >= (uint)path.Width || (uint)z >= (uint)path.Height) continue;
            if (!GroundOccupancy.TryGetOwner(new CellCoordinate(x, z), out var occupantId) ||
                !actorsById.TryGetValue(occupantId, out var occupant) || occupant.IsDestroyed) continue;
            score += TeamRelations.IsHostile(scanner.Seed.Team, occupant.Seed.Team) ? 10 : -15;
        }
        return score;
    }
}
