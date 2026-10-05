using DarkColony.Engine.Commands;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

public sealed record MoveCommandOutcome(
    int EntityInstanceId,
    CellCoordinate Target,
    DiagnosticPathTermination PathTermination,
    int StepCount);

public sealed record DestroyedActorEvent(int EntityInstanceId, int EntityId, FixedPointPosition Position);
/// <summary>
/// One authoritative shot. The presentation roll supplies a deterministic
/// choice among the entity's native FIRE/FIREA/B/C pointer array; exact
/// synchronization with dc.exe's shared global random table remains open.
/// </summary>
/// <param name="PresentationVariantRoll">
/// The shared-stream value common fire draws first (<c>0x412E13</c>); the
/// shooter's fire family is this value modulo its family count.
/// </param>
public sealed record WeaponFireEvent(int SourceActorInstanceId, int WeaponId, int PresentationVariantRoll);
/// <summary>Resolved impact identity, retaining the firing actor for owner-keyed sound feedback.</summary>
/// <param name="Position">
/// Where the projectile ends: on the actor a direct hit struck (<c>0x4428A0</c>
/// moves it there), or at the cell an area shot was aimed at.
/// </param>
/// <param name="ExplosionVariant">
/// Which of the weapon's explosions (<see cref="Assets.WeaponExplosionCatalog"/>)
/// the impact plays, drawn from the shared stream; -1 for none.
/// </param>
public sealed record ProjectileImpactEvent(int SourceActorInstanceId, int TargetActorInstanceId, int WeaponId, int WeaponClass, FixedPointPosition Position,
    int ExplosionVariant = -1);
public enum HealOutcome { Healed, SourceMissing, SourceDestroyed, SourceNotHealer, InsufficientCharge, NoEligibleTargets, MatrixUnavailable }
public sealed record HealEvent(int SourceActorInstanceId, int TargetActorInstanceId, int Amount, HealOutcome Outcome);
public enum InspireOutcome { Preparing, Applied, SourceMissing, SourceDestroyed, SourceNotCommander, NoEligibleTargets }
public sealed record InspireEvent(int SourceActorInstanceId, int TargetActorInstanceId, int Countdown, InspireOutcome Outcome);
public enum AttackOrderOutcome { Acquired, SourceMissing, TargetMissing, SameActor, SourceDestroyed, TargetDestroyed, Unarmed, NonHostile }
public sealed record AttackOrderEvent(int SourceActorInstanceId, int TargetActorInstanceId, AttackOrderOutcome Outcome);
public enum GroundSpecialAttackOutcome { Accepted, SourceMissing, SourceDestroyed, Unsupported, ResearchRequired, WeaponUnavailable, InvalidTarget }
public sealed record GroundSpecialAttackEvent(int SourceActorInstanceId, CellCoordinate Target, int? WeaponId, GroundSpecialAttackOutcome Outcome);
/// <summary>
/// A mode-5..10 packet impact. Entity 92 is the Human dropship and entity 93
/// is the Gray saucer; payload IDs are newly inserted friendly actors or
/// hostile actors removed by abduction.
/// </summary>
public sealed record BattlefieldTransportEvent(
    BattlefieldTransportEventKind Kind,
    int TransportInstanceId,
    int SourceActorInstanceId,
    int TransportEntityId,
    CellCoordinate Target,
    IReadOnlyList<int> ReinforcementInstanceIds,
    IReadOnlyList<int> AbductedInstanceIds);
public enum AttackMoveOrderOutcome { Accepted, SourceMissing, SourceDestroyed, Unarmed, InvalidEndpoint }
public sealed record AttackMoveOrderEvent(int SourceActorInstanceId, CellCoordinate Target, AttackMoveOrderOutcome Outcome);
public sealed record AttackMoveAcquisitionEvent(int SourceActorInstanceId, int TargetActorInstanceId);
/// <summary>An idle actor selected a target: in weapon range, or (Approach) farther out to close on.</summary>
public sealed record IdleAcquisitionEvent(int SourceActorInstanceId, int TargetActorInstanceId, bool Approach);
public sealed record PurchaseReservedEvent(int TeamId, int DependencyItemId, PurchaseEligibility Eligibility);
public enum HarvesterDeploymentOutcome { Attached, Retracted, Preparing, EnRoute, SourceInvalid, VentUnavailable, NoApproach, UndeployLocked }
public sealed record HarvesterDeploymentEvent(int EntityInstanceId, int VentId, HarvesterDeploymentOutcome Outcome)
{
    public bool Attached => Outcome == HarvesterDeploymentOutcome.Attached;
}
public enum MineDeploymentOutcome { Preparing, Deployed, SourceInvalid, AlreadyDeployed, EntityUnresolved, Occupied }
public sealed record MineDeploymentEvent(int SourceActorInstanceId, int EntityInstanceId, int EntityId, CellCoordinate Target, MineDeploymentOutcome Outcome);
public enum TowerDeploymentOutcome { Deployed, SourceInvalid, AlreadyDeployed, EntityUnresolved }
public sealed record TowerDeploymentEvent(int EntityInstanceId, int EntityId, TowerDeploymentOutcome Outcome);
/// <summary>
/// Steal Money transitions. <c>Preparing</c>: the state-13 timer started.
/// <c>Deployed</c>: the stance formed and linked a victim. <c>NoVictim</c> /
/// <c>VictimTaken</c>: the stance formed but found no free victim and starts
/// retracting. <c>VictimLost</c>: the victim died or retracted, so the stance
/// starts retracting. <c>Retracted</c>: the unit is mobile again.
/// </summary>
public enum StealDeploymentOutcome
{
    Deployed, Retracted, SourceInvalid, AlreadyDeployed, EntityUnresolved, Preparing, NoVictim, VictimTaken, VictimLost,
}
public sealed record StealDeploymentEvent(int EntityInstanceId, int EntityId, StealDeploymentOutcome Outcome);
public sealed record P7IncomeEvent(int TeamId, int Amount, int? VentId);
public sealed record P7TheftEvent(
    int ThiefInstanceId,
    int VictimHarvesterInstanceId,
    int ThiefTeamId,
    int VictimTeamId,
    int Amount,
    int VentId);
public sealed record DayNightChangedEvent(DayNightPhase Phase);
public enum BuildingDropOutcome { Placed, CatalogUnavailable, UnknownItem, NotBuilding, NotReserved, WrongFaction, EntityUnresolved, InvalidFootprint, OutOfBounds, Occupied, NoCity }
public sealed record BuildingPlacedEvent(int TeamId, int DependencyItemId, int EntityInstanceId, int EntityId, CellCoordinate Origin, BuildingDropOutcome Outcome);
public enum UnitProductionOutcome { Produced, CatalogUnavailable, UnknownItem, NotTroop, NotReserved, SourceInvalid, PrerequisiteMissing, SpawnBlocked, Queued, NoProductionQueue, CapReached, NoCity }
public sealed record UnitProducedEvent(int TeamId, int DependencyItemId, int EntityInstanceId, int EntityId, int SourceBuildingInstanceId, UnitProductionOutcome Outcome);
public enum ResearchOutcome { Completed, CatalogUnavailable, UnknownItem, NotUpgrade, NotReserved, SourceInvalid, PrerequisiteMissing, Refunded }
public sealed record ResearchCompletedEvent(int TeamId, int DependencyItemId, int SourceBuildingInstanceId, ResearchOutcome Outcome);
public sealed record AutonomousWanderEvent(int EntityInstanceId, int GroupId, CellCoordinate Target, bool MovementStarted);
