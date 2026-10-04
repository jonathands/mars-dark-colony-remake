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
            actor.IdleCommandActive = false;
            return;
        }
        if (!actor.IdleCommandActive)
        {
            // Idle state 1 (0x412654) pushes command 3 with no target, the
            // current health, and a zero miss count.
            actor.IdleCommandActive = true;
            actor.IdleHealthSnapshot = actor.Health;
            actor.IdleMissCount = 0;
            actor.IdleWaitTicks = 0;
        }
        if (actor.IdleWaitTicks > 0)
        {
            actor.IdleWaitTicks--;
            return;
        }
        // Unarmed forms (vents, harvesters, healers, stealing stances) run
        // their own native idle handlers.
        if (!TryGetWeapon(actor, out var weapon)) return;

        var target = ScanForTarget(actor, weapon, weapon.Range);
        if (target is not null)
        {
            actor.IdleMissCount = 0;
            actor.AttackTargetInstanceId = target.Seed.InstanceId;
            events.IdleAcquisitions.Add(new IdleAcquisitionEvent(actor.Seed.InstanceId, target.Seed.InstanceId, Approach: false));
            return;
        }

        var damaged = actor.Health < actor.IdleHealthSnapshot;
        if (damaged) actor.IdleMissCount = 0;
        actor.IdleHealthSnapshot = actor.Health;
        var definition = EffectiveDefinition(actor);
        if (definition.MovementSpeed > 0 && SecondScanRadius(actor, definition, damaged) is { } radius &&
            ScanForTarget(actor, weapon, radius) is { } distant &&
            FindAttackApproachCell(actor, distant, weapon) is { } approachCell)
        {
            // 0x414BF8 stores the hostile's position and pushes a move in mode
            // 2, which stops as soon as anything enters weapon range. The
            // port's local search has no partial routes to an occupied cell,
            // so the move targets the same free approach cell that attack
            // pursuit uses (port adapter).
            actor.IdleMissCount = 0;
            actor.MoveOrder = new ActiveMoveOrder(approachCell) { StopOnContact = true };
            _ = StartSegment(actor);
            events.IdleAcquisitions.Add(new IdleAcquisitionEvent(actor.Seed.InstanceId, distant.Seed.InstanceId, Approach: true));
            return;
        }

        // Idle tail 0x414C29: one draw from the shared stream; a zero low
        // nibble draws again for a fidget command unless entity value 20 is
        // set. That command's visible effect is not recovered yet, so only the
        // stream consumption is reproduced.
        if ((NextNativeRandom() & 0xf) == 0 && !definition.SuppressesIdleFidget) _ = NextNativeRandom();
        if (actor.IdleMissCount < 3)
        {
            actor.IdleMissCount++;
            actor.IdleWaitTicks = NativeIdleShortWaitTicks;
        }
        else
        {
            actor.IdleWaitTicks = NativeIdleLongWaitTicks;
        }
    }

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
    /// An actor whose native command stack is down to the idle command. Only
    /// player teams 0-7 take part: the internal critter team 9 runs the same
    /// handler natively, but its relation-matrix row is not recovered, so
    /// enabling it would invent critter aggression.
    /// </summary>
    private bool IsIdle(SimulatedActor actor) =>
        !actor.IsDestroyed && actor.Seed.Team is >= 0 and < 8 &&
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
        // Hostile mines need a per-team "revealed" bit that is not modeled yet,
        // so only same-team mines pass this gate (and the relation test then
        // rejects them).
        if (definition.UsesNativeMineLayer && candidate.Seed.Team != scanner.Seed.Team) return false;
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

    /// <summary>
    /// The selector only considers cells its team currently sees (team 9 uses
    /// the union of teams 0-7). Visibility is snapshotted per team once per
    /// tick with the same rule as <see cref="IsCellVisibleToTeam"/>.
    /// </summary>
    private bool IsCellVisibleForScan(int team, CellCoordinate cell)
    {
        if (team >= 8)
        {
            for (var player = 0; player < 8; player++)
                if (IsCellVisibleForScan(player, cell)) return true;
            return false;
        }
        if (!scanVisibility.TryGetValue(team, out var visible))
        {
            visible = new bool[path.Width * path.Height];
            foreach (var observer in actors)
            {
                if (observer.IsDestroyed || observer.Seed.Team != team) continue;
                var range = ObservationRange(observer);
                if (range < 0) continue;
                var center = observer.Movement.OccupiedCell;
                for (var z = Math.Max(0, center.Z - range); z <= Math.Min(path.Height - 1, center.Z + range); z++)
                for (var x = Math.Max(0, center.X - range); x <= Math.Min(path.Width - 1, center.X + range); x++)
                {
                    long dx = x - center.X;
                    long dz = z - center.Z;
                    if (dx * dx + dz * dz <= (long)range * range) visible[z * path.Width + x] = true;
                }
            }
            scanVisibility[team] = visible;
        }
        return visible[cell.Z * path.Width + cell.X];
    }
}
