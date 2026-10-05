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
        int verticalVelocityRaw = 0,
        int launchDelayTicks = 0)
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
        // 0x44184D: the constructor stores the delay times four in +0x12.
        LaunchDelaySubsteps = launchDelayTicks * 4;
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
    /// <summary>
    /// Projectile record word <c>+0x12</c>: the substeps it still waits at
    /// its muzzle (state 0) before it flies. A waiting projectile neither
    /// moves, ages, hits nor is drawn (<c>0x439DCB</c>).
    /// </summary>
    public int LaunchDelaySubsteps { get; private set; }
    public bool ReachedAimedPosition => RemainingTicks <= 0;

    /// <summary>
    /// Projectile mode 4 (Napalm, Disease) does not end at its impact: it
    /// turns to state 3 and burns there (<c>0x441C7B</c>). Word <c>+0x18</c>
    /// then counts the substeps since; null while the projectile flies.
    /// </summary>
    public int? BurnSubsteps { get; private set; }
    public bool IsBurning => BurnSubsteps is not null;
    /// <summary>The explosion a burning projectile loops (its impact's draw); -1 for none.</summary>
    public int ExplosionVariant { get; private set; } = -1;

    internal void BeginBurn(FixedPointPosition position, int explosionVariant)
    {
        Position = position;
        BurnSubsteps = 0;
        ExplosionVariant = explosionVariant;
    }

    /// <summary>Returns the burn's substep count, then advances it (<c>0x4423E9</c>).</summary>
    internal int NextBurnSubstep()
    {
        var count = BurnSubsteps ?? throw new InvalidOperationException("The projectile is not burning.");
        BurnSubsteps = count + 1;
        return count;
    }

    /// <summary>
    /// State 0 of the substep (<c>0x44244D</c>): while the counter is not
    /// zero it only counts down; at zero the projectile flies in the same
    /// substep. Returns whether this substep was spent waiting.
    /// </summary>
    public bool WaitForLaunch()
    {
        if (LaunchDelaySubsteps == 0) return false;
        LaunchDelaySubsteps--;
        return true;
    }
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
