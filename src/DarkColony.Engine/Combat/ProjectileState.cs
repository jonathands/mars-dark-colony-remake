using DarkColony.Engine.World;

namespace DarkColony.Engine.Combat;

/// <summary>
/// Simulation-owned counterpart to the original 40-byte projectile record.
/// Position and velocity use the native 8.8 representation and advance once
/// per authoritative simulation tick.
/// </summary>
public sealed class ProjectileState
{
    private static readonly int[] NativeModeOneArc =
        [25, 150, 280, 390, 480, 550, 600, 630, 640, 630, 600, 550, 480, 390, 280, 150, 25];

    public ProjectileState(
        int instanceId,
        int sourceActorInstanceId,
        int targetActorInstanceId,
        int weaponId,
        int damage,
        FixedPointPosition position,
        int velocityXRaw,
        int velocityZRaw,
        int aimedFlightTicks,
        int? maximumLifetimeTicks = null,
        CellCoordinate? groundTargetCell = null,
        CellCoordinate? timedImpactCell = null,
        int projectileMode = 0,
        int heightRaw = 0,
        int verticalVelocityRaw = 0)
    {
        InstanceId = instanceId;
        SourceActorInstanceId = sourceActorInstanceId;
        TargetActorInstanceId = targetActorInstanceId;
        WeaponId = weaponId;
        Damage = damage;
        Position = position;
        VelocityXRaw = velocityXRaw;
        VelocityZRaw = velocityZRaw;
        TotalTicks = aimedFlightTicks;
        RemainingTicks = aimedFlightTicks;
        MaximumLifetimeTicks = maximumLifetimeTicks ?? aimedFlightTicks;
        GroundTargetCell = groundTargetCell;
        TimedImpactCell = timedImpactCell;
        ProjectileMode = projectileMode;
        HeightRaw = heightRaw;
        VerticalVelocityRaw = verticalVelocityRaw;
    }

    public int InstanceId { get; }
    public int SourceActorInstanceId { get; }
    public int TargetActorInstanceId { get; }
    /// <summary>
    /// Exact cell center supplied by native ground-target packet 0x1b. Null
    /// identifies an ordinary actor-targeted projectile.
    /// </summary>
    public CellCoordinate? GroundTargetCell { get; }
    /// <summary>
    /// Snapshot of the originally aimed cell for a nonzero boom-template
    /// trajectory. Native update counts this flight down and detonates without
    /// probing actor occupancy on the way.
    /// </summary>
    public CellCoordinate? TimedImpactCell { get; }
    public int WeaponId { get; }
    public int ProjectileMode { get; }
    public int Damage { get; }
    public FixedPointPosition Position { get; private set; }
    /// <summary>Native projectile record word <c>+0x04</c>, in signed 8.8 world height.</summary>
    public int HeightRaw { get; private set; }
    /// <summary>Native projectile record word <c>+0x0A</c>, added before a mode-1/4 arc overwrites height.</summary>
    public int VerticalVelocityRaw { get; }
    public int VelocityXRaw { get; }
    public int VelocityZRaw { get; }
    /// <summary>Ticks to the initially aimed position, retained for render playback and ground targeting.</summary>
    public int TotalTicks { get; }
    /// <summary>Maximum age derived by the native weapon loader from range and speed.</summary>
    public int MaximumLifetimeTicks { get; }
    /// <summary>Authoritative simulation steps elapsed since projectile creation.</summary>
    public int ElapsedTicks { get; private set; }
    /// <summary>
    /// Outer 66 ms world ticks survived by this projectile. Native dispatcher
    /// <c>0x44293C</c> performs four physics updates, then advances projectile
    /// animation once at <c>0x44297F</c>.
    /// </summary>
    public int AnimationTicks { get; private set; }
    public int RemainingTicks { get; private set; }
    public bool ReachedAimedPosition => RemainingTicks <= 0;
    public bool ExceededMaximumLifetime => ElapsedTicks > MaximumLifetimeTicks;

    public void Step()
    {
        Position = Position.AddRaw(VelocityXRaw, VelocityZRaw);
        HeightRaw = unchecked((short)(HeightRaw + VerticalVelocityRaw));
        if (TimedImpactCell is not null && ProjectileMode is 1 or 4)
        {
            // 0x442692-0x442730 uses remaining+age as the invariant total,
            // selects one of 17 samples by remaining*16/total, and writes the
            // resulting absolute 8.8 height before decrementing remaining.
            var totalUpdates = RemainingTicks + ElapsedTicks;
            var tableIndex = totalUpdates == 0 ? 0 : RemainingTicks * 16 / totalUpdates;
            if ((uint)tableIndex >= NativeModeOneArc.Length)
                throw new InvalidOperationException($"Native projectile arc index {tableIndex} is outside 0..16.");
            HeightRaw = unchecked((short)(NativeModeOneArc[tableIndex] * totalUpdates >> 6));
        }
        ElapsedTicks++;
        RemainingTicks--;
    }

    public void CompleteWorldTick() => AnimationTicks++;
}
