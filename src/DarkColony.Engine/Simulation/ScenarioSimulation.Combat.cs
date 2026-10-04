using DarkColony.Engine.Commands;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>Attack orders, pursuit, projectiles, damage, and actor destruction.</summary>
public sealed partial class ScenarioSimulation
{
    private void ApplyAreaDamage(ProjectileState projectile, WeaponDefinition weapon, CellCoordinate center,
        AreaEffectTemplate effect, ICollection<DestroyedActorEvent> destroyed)
    {
        if (!actorsById.TryGetValue(projectile.SourceActorInstanceId, out var source)) return;
        var radius = effect.PatternSize / 2;
        foreach (var actor in Actors.Where(actor => !actor.IsDestroyed).OrderBy(actor => actor.Seed.InstanceId))
        {
            var dx = actor.Movement.OccupiedCell.X - center.X;
            var dz = actor.Movement.OccupiedCell.Z - center.Z;
            if (Math.Abs(dx) > radius || Math.Abs(dz) > radius) continue;
            var percent = effect.DamagePattern[dz + radius][dx + radius];
            if (percent <= 0) continue;
            // The boom loader stores each weight as (percent << 8) / 100
            // (0x43B55A). 0x4420F4 compares exact team bytes, not the alliance
            // matrix: same-team splash scales the weight by 0x40 / 0x100.
            var weight = (percent << 8) / 100;
            if (actor.Seed.Team == source.Seed.Team) weight = (weight * 0x40) >> 8;
            if (weight <= 0) continue;
            var damage = damageMatrix is null
                ? weapon.Damage * weight >> 8
                : damageMatrix.CalculateNativeDamage(weapon.Damage, weapon.WeaponClass, EffectiveDefinition(actor).ArmorClass,
                    weight, ArmorFactor(actor));
            ApplyDamage(actor, damage, destroyed, source.Seed.Team);
        }
    }

    /// <summary>
    /// Takes an actor off the map without a kill. A carried-off actor enters
    /// the dying state with counter 1 (<c>0x416220</c>), which skips the death
    /// animation's draw; other removals leave the update list at once.
    /// </summary>
    private void RemoveActorFromWorld(SimulatedActor actor, bool carriedOff)
    {
        actor.Health = 0;
        if (carriedOff) actor.DeathTicks = 1;
        // Removing a transported or abducted actor must release any pending
        // Petra-7 vent handshake immediately; otherwise the vent remains
        // falsely occupied until the next sixteen-step income pass.
        DetachHarvester(actor);
        actor.Playback?.Cancel();
        actor.Playback = null;
        actor.FinishingStep?.Cancel();
        actor.FinishingStep = null;
        actor.MoveOrder = null;
        actor.AttackTargetInstanceId = null;
        actor.AttackMoveDestination = null;
        actor.GroundSpecialAttackTarget = null;
        GroundOccupancy.Release(actor.Seed.InstanceId);
        AlternateOccupancy.Release(actor.Seed.InstanceId);
        MineOccupancy.Release(actor.Seed.InstanceId);
        OnCityBuildingDestroyed(actor);
        foreach (var other in Actors.Where(other => other.AttackTargetInstanceId == actor.Seed.InstanceId))
        {
            // A target can be destroyed by a different attacker while this
            // actor has already reserved a pursuit cell.  Target loss clears
            // that approach immediately: direct attack stops, while
            // attack-move re-enters acquisition/pathing toward its retained
            // destination on the following deterministic update.
            other.AttackTargetInstanceId = null;
            StopAfterCurrentStep(other);
            other.MoveOrder = null;
        }
    }

    private SimulatedActor? FindProjectileCollision(ProjectileState projectile)
    {
        if (!actorsById.TryGetValue(projectile.SourceActorInstanceId, out var source)) return null;
        var cell = projectile.Position.Cell;
        foreach (var occupancy in new[] { GroundOccupancy, AlternateOccupancy, MineOccupancy })
        {
            if (!occupancy.TryGetOwner(cell, out var candidateId) || candidateId == projectile.SourceActorInstanceId ||
                !actorsById.TryGetValue(candidateId, out var candidate) || candidate.IsDestroyed) continue;
            if (TeamRelations.IsHostile(source.Seed.Team, candidate.Seed.Team)) return candidate;
        }
        return null;
    }

    private void ApplyDamage(SimulatedActor target, int damage, ICollection<DestroyedActorEvent> destroyed, int? attackerTeam)
    {
        target.Health = Math.Max(0, target.Health - damage);
        if (target.Health != 0) return;
        RecordMissionKill(target, attackerTeam);
        Destroy(target, destroyed);
    }

    /// <summary>
    /// Resolves the weapon/armor matrix at impact time.  The native projectile
    /// stores the weapon's raw damage; an intervening hostile can therefore
    /// receive a different scaled amount than the actor that was originally
    /// selected when the shot was fired.
    /// </summary>
    private int ResolveProjectileDamage(ProjectileState projectile, SimulatedActor target)
    {
        if (weaponCatalog?.TryGet(projectile.WeaponId, out var weapon) != true)
            return Math.Max(0, projectile.Damage);
        if (damageMatrix is null) return Math.Max(0, projectile.Damage);
        // 0x4427AA reads the shooter: an inspired one hits with its commander's
        // factor (runtime +0xFC, gamestat value 26 in 8.8), and a Human
        // shooter at night or a Gray shooter by day deals three quarters.
        var multiplier = 0x100;
        var threeQuarters = false;
        if (actorsById.TryGetValue(projectile.SourceActorInstanceId, out var shooter))
        {
            if (shooter.InspirationTicksRemaining > 0 && shooter.InspirationSourceActorInstanceId is { } commanderId &&
                actorsById.TryGetValue(commanderId, out var commander))
                multiplier = (EffectiveDefinition(commander).Values[25] << 8) / 100;
            var race = EffectiveDefinition(shooter).Faction;
            threeQuarters = race == 0 && DayNight.Phase == DayNightPhase.Night ||
                            race == 1 && DayNight.Phase == DayNightPhase.Day;
        }
        return Math.Max(0, damageMatrix.CalculateNativeDamage(projectile.Damage, weapon.WeaponClass,
            EffectiveDefinition(target).ArmorClass, multiplier, ArmorFactor(target), threeQuarters));
    }

    /// <summary>
    /// The target's armor factor for its team's armor level (entity runtime
    /// <c>+0x24 + level * 4</c>, loader <c>0x43BD46</c>): 0x100 at level 0, then
    /// <c>25600 / armor</c> with gamestat values 9 and 10 as the level 1 and
    /// 2 armor.
    /// </summary>
    private int ArmorFactor(SimulatedActor target)
    {
        var definition = EffectiveDefinition(target);
        var armor = ArmorUpgradeLevel(target) switch
        {
            1 => definition.Values[8],
            2 => definition.Values[9],
            _ => 100,
        };
        return armor > 0 ? 25600 / armor : 0x100;
    }

    internal void Destroy(SimulatedActor actor, ICollection<DestroyedActorEvent> destroyed)
    {
        destroyed.Add(new DestroyedActorEvent(actor.Seed.InstanceId, actor.Seed.EntityId, actor.Movement.VisualPosition));
        actor.Health = 0;
        BeginDeath(actor);
        // The native actor destructor removes a live EXPL/SLUG source from its
        // vent record as part of world removal, rather than waiting for the
        // next producer pulse to notice a stale pointer.
        DetachHarvester(actor);
        actor.Playback?.Cancel();
        actor.Playback = null;
        actor.FinishingStep?.Cancel();
        actor.FinishingStep = null;
        actor.MoveOrder = null;
        actor.AttackTargetInstanceId = null;
        actor.AttackMoveDestination = null;
        GroundOccupancy.Release(actor.Seed.InstanceId);
        AlternateOccupancy.Release(actor.Seed.InstanceId);
        MineOccupancy.Release(actor.Seed.InstanceId);
        OnCityBuildingDestroyed(actor);
        foreach (var other in Actors.Where(other => other.AttackTargetInstanceId == actor.Seed.InstanceId))
        {
            // Combat destruction can occur after other actors have already
            // reserved their approach cells this update. Clear those stale
            // pursuit segments immediately; an attack-move actor retains only
            // its destination and reacquires/routes next update.
            other.AttackTargetInstanceId = null;
            StopAfterCurrentStep(other);
            other.MoveOrder = null;
        }
    }

    private AttackOrderEvent IssueAttackOrder(AttackIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var attacker))
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.SourceMissing);
        if (!actorsById.TryGetValue(intent.TargetEntityInstanceId, out var target))
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.TargetMissing);
        if (attacker.Seed.InstanceId == target.Seed.InstanceId)
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.SameActor);
        if (attacker.IsDestroyed)
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.SourceDestroyed);
        if (target.IsDestroyed)
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.TargetDestroyed);
        if (!TeamRelations.IsHostile(attacker.Seed.Team, target.Seed.Team))
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.NonHostile);
        if (!TryGetWeapon(attacker, out _))
            return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.Unarmed);
        // Direct attack replaces any queued travel. Otherwise an out-of-range
        // target is forced to wait for the previous segment to finish before
        // pursuit begins, despite the player having issued a new order.
        StopAfterCurrentStep(attacker);
        attacker.MoveOrder = null;
        attacker.AttackMoveDestination = null;
        attacker.MineDeployTicksRemaining = 0;
        attacker.AttackTargetInstanceId = target.Seed.InstanceId;
        attacker.GroundSpecialAttackTarget = null;
        return new AttackOrderEvent(intent.EntityInstanceId, intent.TargetEntityInstanceId, AttackOrderOutcome.Acquired);
    }

    private AttackMoveOrderEvent IssueAttackMoveOrder(AttackMoveIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var actor))
            return new AttackMoveOrderEvent(intent.EntityInstanceId, intent.TargetCell, AttackMoveOrderOutcome.SourceMissing);
        if (actor.IsDestroyed)
            return new AttackMoveOrderEvent(intent.EntityInstanceId, intent.TargetCell, AttackMoveOrderOutcome.SourceDestroyed);
        if (EffectiveDefinition(actor).MovementSpeed <= 0 || !TryGetWeapon(actor, out _))
            return new AttackMoveOrderEvent(intent.EntityInstanceId, intent.TargetCell, AttackMoveOrderOutcome.Unarmed);
        if ((uint)intent.TargetCell.X >= (uint)path.Width || (uint)intent.TargetCell.Z >= (uint)path.Height)
            return new AttackMoveOrderEvent(intent.EntityInstanceId, intent.TargetCell, AttackMoveOrderOutcome.InvalidEndpoint);
        StopAfterCurrentStep(actor);
        DetachHarvester(actor);
        actor.MineDeployTicksRemaining = 0;
        actor.AttackTargetInstanceId = null;
        actor.GroundSpecialAttackTarget = null;
        actor.AttackMoveDestination = intent.TargetCell;
        actor.MoveOrder = new ActiveMoveOrder(intent.TargetCell);
        _ = StartSegment(actor);
        return new AttackMoveOrderEvent(intent.EntityInstanceId, intent.TargetCell, AttackMoveOrderOutcome.Accepted);
    }

    private SimulatedActor? FindAttackMoveTarget(SimulatedActor actor)
    {
        // The source supplies distinct day/night observation columns. The
        // original acquisition radius is still untraced, so use that
        // data-backed visibility boundary rather than a presentation constant.
        var radiusRaw = (long)Math.Max(0, ObservationRange(actor)) * FixedPointPosition.One;
        return Actors
            .Where(candidate => !candidate.IsDestroyed && candidate.Seed.InstanceId != actor.Seed.InstanceId)
            .Where(candidate => TeamRelations.IsHostile(actor.Seed.Team, candidate.Seed.Team))
            .Where(candidate => DistanceSquared(actor.Movement.VisualPosition, candidate.Movement.VisualPosition) <= radiusRaw * radiusRaw)
            .OrderBy(candidate => DistanceSquared(actor.Movement.VisualPosition, candidate.Movement.VisualPosition))
            .ThenBy(candidate => candidate.Seed.InstanceId)
            .FirstOrDefault();
    }

    private void PursueAttackTarget(SimulatedActor attacker, SimulatedActor target)
    {
        if (EffectiveDefinition(attacker).MovementSpeed <= 0 || !TryGetWeapon(attacker, out var weapon)) return;
        if (IsAttackTargetInRange(attacker))
        {
            // A previously issued approach can otherwise carry an attacker
            // past a target that has moved into weapon range.
            StopAfterCurrentStep(attacker);
            attacker.MoveOrder = null;
            return;
        }
        // Do not continually replace the same chase segment as logical cells
        // advance ahead of interpolation. Re-evaluate after arrival or if the
        // target moves beyond range again.
        if (attacker.Playback is not null || attacker.MoveOrder is not null) return;
        var approach = FindAttackApproachCell(attacker, target, weapon);
        if (approach is null) return;
        attacker.MoveOrder = new ActiveMoveOrder(approach.Value);
        _ = StartSegment(attacker);
    }

    private CellCoordinate? FindAttackApproachCell(SimulatedActor attacker, SimulatedActor target, WeaponDefinition weapon)
    {
        var occupancy = EffectiveDefinition(attacker).MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        var maxRadius = Math.Max(1, weapon.Range);
        CellCoordinate? best = null;
        var bestDistance = long.MaxValue;
        for (var radius = 1; radius <= maxRadius; radius++)
        for (var z = -radius; z <= radius; z++)
        for (var x = -radius; x <= radius; x++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius) continue;
            var cell = new CellCoordinate(target.Movement.OccupiedCell.X + x, target.Movement.OccupiedCell.Z + z);
            if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height || occupancy.IsOccupied(cell)) continue;
            var dx = (long)x * FixedPointPosition.One;
            var dz = (long)z * FixedPointPosition.One;
            var range = (long)weapon.Range * FixedPointPosition.One;
            if (dx * dx + dz * dz >= range * range) continue;
            var sourceDx = cell.X - attacker.Movement.OccupiedCell.X;
            var sourceDz = cell.Z - attacker.Movement.OccupiedCell.Z;
            var distance = (long)sourceDx * sourceDx + (long)sourceDz * sourceDz;
            if (distance >= bestDistance) continue;
            best = cell;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>
    /// Common fire (<c>0x412DA0</c>) draws from the shared stream in this
    /// order: the fire animation variant (<c>0x412E13</c>), then per projectile
    /// the area aim (<c>0x412ED6</c>) and the projectile constructor's byte
    /// (<c>0x4417B4</c>, stored at projectile <c>+0x1F</c>). One projectile
    /// leaves per frame of the fire animation whose hotspot 7 names an
    /// animation, else one from the actor's center; the shipped animations
    /// have at most one such frame. Not modeled: that frame's muzzle offset
    /// and launch delay (ATRIL, BARR, SCYT FIREB, TURR, and the deployed XENO).
    /// </summary>
    private void SpawnProjectile(SimulatedActor attacker, SimulatedActor target, WeaponDefinition weapon, ICollection<WeaponFireEvent> fired)
    {
        var presentation = NextFirePresentationRoll();
        var source = attacker.Movement.VisualPosition;
        var destination = ApplyNativeAimOffset(attacker, target.Movement.VisualPosition, weapon);
        var dx = (long)destination.XRaw - source.XRaw;
        var dz = (long)destination.ZRaw - source.ZRaw;
        var distance = Math.Max(1L, (long)Math.Sqrt(dx * dx + dz * dz));
        var speed = Math.Max(1, weapon.ProjectileSpeed);
        var velocityX = (int)(dx * speed / distance);
        var velocityZ = (int)(dz * speed / distance);
        var ticks = CalculateTrajectoryUpdates(dx, dz, velocityX, velocityZ);
        CellCoordinate? timedImpactCell = weapon.HasAreaEffect ? destination.Cell : null;
        _ = NextNativeRandom();
        projectiles.Add(new ProjectileState(nextProjectileInstanceId++, attacker.Seed.InstanceId, target.Seed.InstanceId,
            weapon.Id, Math.Max(0, weapon.Damage), source, velocityX, velocityZ, ticks, weapon.ProjectileLifetimeTicks,
            timedImpactCell: timedImpactCell, projectileMode: weapon.ProjectileMode));
        AddPlayerStatistic(attacker.Seed.Team, 8, 1);
        // dc.exe 0x413181 reads the weapon's burst limit (+0x20), increments
        // actor byte +0x34, and substitutes reload (+0x24) only after the
        // final burst shot. Normal shots use the rate field (+0x08). Keep this
        // in authoritative simulation state; a renderer must not decide fire
        // cadence from an animation length.
        ApplyWeaponCooldown(attacker, weapon);
        fired.Add(new WeaponFireEvent(attacker.Seed.InstanceId, weapon.Id, presentation));
    }

    /// <summary>The ground-special shot goes through the same common fire as <see cref="SpawnProjectile"/>.</summary>
    private void SpawnGroundProjectile(SimulatedActor attacker, CellCoordinate target, WeaponDefinition weapon, ICollection<WeaponFireEvent> fired)
    {
        var presentation = NextFirePresentationRoll();
        var source = attacker.Movement.VisualPosition;
        var destination = ApplyNativeAimOffset(attacker, FixedPointPosition.AtCellCenter(target), weapon);
        var dx = (long)destination.XRaw - source.XRaw;
        var dz = (long)destination.ZRaw - source.ZRaw;
        var distance = Math.Max(1L, (long)Math.Sqrt(dx * dx + dz * dz));
        var speed = Math.Max(1, weapon.ProjectileSpeed);
        var velocityX = (int)(dx * speed / distance);
        var velocityZ = (int)(dz * speed / distance);
        var ticks = CalculateTrajectoryUpdates(dx, dz, velocityX, velocityZ);
        _ = NextNativeRandom();
        projectiles.Add(new ProjectileState(nextProjectileInstanceId++, attacker.Seed.InstanceId, -1,
            weapon.Id, Math.Max(0, weapon.Damage), source, velocityX, velocityZ, ticks, weapon.ProjectileLifetimeTicks,
            target, destination.Cell, weapon.ProjectileMode));
        AddPlayerStatistic(attacker.Seed.Team, 8, 1);
        ApplyWeaponCooldown(attacker, weapon);
        fired.Add(new WeaponFireEvent(attacker.Seed.InstanceId, weapon.Id, presentation));
    }

    private byte NextFirePresentationRoll()
    {
        // Native 0x412e13 consumes the game's shared 256-entry random stream,
        // then applies modulo entity.fireVariantCount. This must share its
        // cursor with scatter and blocked-route jitter: separate PRNGs make a
        // correct input sequence diverge as soon as any of those paths runs.
        return (byte)NextNativeRandom();
    }

    private FixedPointPosition ApplyNativeAimOffset(
        SimulatedActor attacker,
        FixedPointPosition target,
        WeaponDefinition weapon)
    {
        if (!weapon.HasAreaEffect || areaEffects?.TryGet(weapon.AreaEffectTemplateId, out var effect) != true)
            return target;
        if (attacker.InspirationTicksRemaining > 0)
            return target;

        var remaining = NextAimRoll();
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            // Loader 0x43b59f stores floor(percent * 256 / 100). Common fire
            // walks the nine entries row-major, subtracting weights until the
            // current entry exceeds the shared-random byte.
            var weight = effect.AimWeights[row][column] * FixedPointPosition.One / 100;
            if (weight > remaining)
                return target.AddRaw((column - 1) * FixedPointPosition.One, (row - 1) * FixedPointPosition.One);
            remaining -= weight;
        }
        // Percent-to-8.8 flooring can leave up to a few unassigned byte
        // values. Native exits with row/column == 3, yielding (+2,+2).
        return target.AddRaw(2 * FixedPointPosition.One, 2 * FixedPointPosition.One);
    }

    private int NextAimRoll()
    {
        // Common fire consumes the next value from the same native table used
        // by presentation selection and path-repair jitter.
        return (byte)NextNativeRandom();
    }

    private static int CalculateTrajectoryUpdates(long dx, long dz, int velocityX, int velocityZ)
    {
        // 0x413004-0x413054 divides the dominant signed delta by its matching
        // velocity component. A nonzero boom template stores this count at
        // projectile +0x18 and decrements it once per projectile substep.
        if (Math.Abs(velocityX) >= Math.Abs(velocityZ))
            return velocityX == 0 ? 1 : Math.Max(1, checked((int)Math.Abs(dx / velocityX)));
        return velocityZ == 0 ? 1 : Math.Max(1, checked((int)Math.Abs(dz / velocityZ)));
    }

    private static void ApplyWeaponCooldown(SimulatedActor attacker, WeaponDefinition weapon)
    {
        attacker.CooldownTicks = Math.Max(1, weapon.RateOfFire);
        if (weapon.BurstShotLimit > 0 && ++attacker.BurstShotCount >= weapon.BurstShotLimit)
        {
            attacker.BurstShotCount = 0;
            attacker.CooldownTicks = Math.Max(1, weapon.BurstReloadTicks);
        }
    }

    /// <summary>Deployed HMINE mines fire at adjacent hostiles.</summary>
    private void FireMines(TickEvents events)
    {
        var fired = events.Fired;
        // HMINE records resolve weapon 38, explicitly named "mine" in the
        // shipped weapon catalog. Common fire 0x41311C removes 300 live health
        // (floor 1) for every trigger, then follows the ordinary weapon path.
        // Weapon 38's own same-team area splash kills the one-health mine on
        // its third trigger. Target-scan cadence remains provisional, but fire
        // cadence is the normal decoded weapon cooldown rather than one shot
        // per world update.
        foreach (var mine in Actors.Where(actor => !actor.IsDestroyed && EffectiveDefinition(actor).Code == "HMINE").OrderBy(actor => actor.Seed.InstanceId))
        {
            if (mine.CooldownTicks > 0 || !TryGetWeapon(mine, out var weapon)) continue;
            var target = FindMineTriggerTarget(mine, weapon);
            if (target is null) continue;
            mine.Health = Math.Max(1, mine.Health - NativeMineFireIntegrityCost);
            SpawnProjectile(mine, target, weapon, fired);
        }
    }

    /// <summary>Actors with an in-range target and finished cooldown fire.</summary>
    private void FireAttackers(TickEvents events)
    {
        var fired = events.Fired;
        foreach (var attacker in Actors)
        {
            if (attacker.FinishingStep is not null ||
                attacker.AttackTargetInstanceId is not { } targetId || attacker.CooldownTicks > 0 ||
                attacker.Facing.Current != attacker.Facing.Target || !actorsById.TryGetValue(targetId, out var target) ||
                !IsAttackTargetInRange(attacker)) continue;
            if (!TryGetWeapon(attacker, out var weapon)) continue;
            SpawnProjectile(attacker, target, weapon, fired);
        }
    }

    /// <summary>Advances projectiles four substeps and resolves impacts.</summary>
    private void UpdateProjectiles(TickEvents events)
    {
        var destroyed = events.Destroyed;
        var impacts = events.Impacts;
        var battlefieldTransports = events.BattlefieldTransports;
        for (var index = projectiles.Count - 1; index >= 0; index--)
        {
            var projectile = projectiles[index];
            WeaponDefinition? weapon = null;
            var weaponClass = -1;
            if (weaponCatalog?.TryGet(projectile.WeaponId, out var resolvedWeapon) == true)
            {
                weapon = resolvedWeapon;
                weaponClass = weapon.WeaponClass;
            }
            var resolved = false;
            for (var substep = 0; substep < NativeProjectileSubstepsPerTick && !resolved; substep++)
            {
                projectile.Step();

                // dc.exe 0x442494 advances the projectile before probing current
                // occupancy. The shot is no longer locked to its originally aimed
                // actor: an intervening hostile can take the hit, while the source
                // and cooperative teams are ignored by 0x4425F6-0x442675.
                // A completed state-18 shot is still reported as a ground impact;
                // occupants in earlier cells can intercept it, but an occupant of
                // the commanded destination does not turn the command back into an
                // actor-targeted order in the port's event model.
                var reachedTimedAim = projectile.TimedImpactCell is { } aimedCell &&
                    (projectile.Position.Cell == aimedCell || projectile.ReachedAimedPosition);
                var collision = projectile.TimedImpactCell is null ? FindProjectileCollision(projectile) : null;
                if (collision is not null)
                {
                    projectiles.RemoveAt(index);
                    if (weaponClass >= 0) impacts.Add(new ProjectileImpactEvent(projectile.SourceActorInstanceId, collision.Seed.InstanceId, projectile.WeaponId, weaponClass, projectile.Position));
                    if (weapon is { HasAreaEffect: true } && areaEffects?.TryGet(weapon.AreaEffectTemplateId, out var collisionEffect) == true)
                        ApplyAreaDamage(projectile, weapon, projectile.Position.Cell, collisionEffect, destroyed);
                    else ApplyDamage(collision, ResolveProjectileDamage(projectile, collision), destroyed,
                        actorsById.TryGetValue(projectile.SourceActorInstanceId, out var shooter) ? shooter.Seed.Team : null);
                    resolved = true;
                    continue;
                }

                // State-18 ground fire supplies an exact cell rather than an actor.
                // Its vertical trajectory is not represented yet, so reaching the
                // aimed X/Z position is the explicit adapter for the native ground
                // collision; ordinary missed shots instead continue until expiry.
                if (projectile.TimedImpactCell is { } timedImpactCell && reachedTimedAim)
                {
                    projectiles.RemoveAt(index);
                    var impactPosition = FixedPointPosition.AtCellCenter(timedImpactCell);
                    var impactTargetId = projectile.GroundTargetCell is null ? projectile.TargetActorInstanceId : -1;
                    if (weaponClass >= 0) impacts.Add(new ProjectileImpactEvent(projectile.SourceActorInstanceId, impactTargetId, projectile.WeaponId, weaponClass, impactPosition));
                    if (weapon is not null && ResolveBattlefieldTransportImpact(projectile, weapon, timedImpactCell, battlefieldTransports))
                    {
                        resolved = true;
                        continue;
                    }
                    if (weapon is { HasAreaEffect: true } && areaEffects?.TryGet(weapon.AreaEffectTemplateId, out var timedEffect) == true)
                        ApplyAreaDamage(projectile, weapon, timedImpactCell, timedEffect, destroyed);
                    resolved = true;
                    continue;
                }

                if (!projectile.ExceededMaximumLifetime) continue;
                projectiles.RemoveAt(index);
                resolved = true;
            }
            if (!resolved) projectile.CompleteWorldTick();
        }
    }
}
