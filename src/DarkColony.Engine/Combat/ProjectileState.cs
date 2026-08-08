using DarkColony.Engine.World;

namespace DarkColony.Engine.Combat;

/// <summary>
/// Simulation-owned projectile scaffold. The original allocation record and
/// weapon velocity field are confirmed; the exact lifetime update remains to
/// be recovered, so the linear duration is explicitly provisional.
/// </summary>
public sealed class ProjectileState
{
    public ProjectileState(
        int instanceId,
        int sourceActorInstanceId,
        int targetActorInstanceId,
        int weaponId,
        int damage,
        FixedPointPosition position,
        int velocityXRaw,
        int velocityZRaw,
        int remainingTicks)
    {
        InstanceId = instanceId;
        SourceActorInstanceId = sourceActorInstanceId;
        TargetActorInstanceId = targetActorInstanceId;
        WeaponId = weaponId;
        Damage = damage;
        Position = position;
        VelocityXRaw = velocityXRaw;
        VelocityZRaw = velocityZRaw;
        TotalTicks = remainingTicks;
        RemainingTicks = remainingTicks;
    }

    public int InstanceId { get; }
    public int SourceActorInstanceId { get; }
    public int TargetActorInstanceId { get; }
    public int WeaponId { get; }
    public int Damage { get; }
    public FixedPointPosition Position { get; private set; }
    public int VelocityXRaw { get; }
    public int VelocityZRaw { get; }
    /// <summary>Initial provisional flight duration, retained for render playback.</summary>
    public int TotalTicks { get; }
    /// <summary>Authoritative simulation steps elapsed since projectile creation.</summary>
    public int ElapsedTicks { get; private set; }
    public int RemainingTicks { get; private set; }

    public bool Step()
    {
        Position = Position.AddRaw(VelocityXRaw, VelocityZRaw);
        ElapsedTicks++;
        RemainingTicks--;
        return RemainingTicks <= 0;
    }
}
