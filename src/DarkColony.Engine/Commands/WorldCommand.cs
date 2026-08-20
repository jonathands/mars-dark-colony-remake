using DarkColony.Engine.World;

namespace DarkColony.Engine.Commands;

/// <summary>External deterministic intent; distinct from the native six-slot actor queue.</summary>
public abstract record WorldCommand;

public sealed record MoveIntent(int EntityInstanceId, CellCoordinate TargetCell, bool AppendWaypoint = false) : WorldCommand;

/// <summary>Cancels the current local movement order for one actor.</summary>
public sealed record StopIntent(int EntityInstanceId) : WorldCommand;

/// <summary>
/// Acquires an explicit actor target. It is deliberately distinct from firing:
/// projectile construction, range, scatter, and damage execute in Combat.
/// </summary>
public sealed record AttackIntent(int EntityInstanceId, int TargetEntityInstanceId) : WorldCommand;

/// <summary>
/// Fires the entity's native value-29 secondary weapon at a map coordinate.
/// This corresponds to packet opcode 0x1b and actor state 18.
/// </summary>
public sealed record GroundSpecialAttackIntent(int EntityInstanceId, CellCoordinate TargetCell) : WorldCommand;

/// <summary>Executes the recovered BEON/ZISP same-team area-heal scan.</summary>
public sealed record HealAreaIntent(int EntityInstanceId) : WorldCommand;

/// <summary>
/// Starts a commander's recovered state-13 Inspire action. The effect is
/// applied after the native 0x32-tick actor-state delay, not on button press.
/// </summary>
public sealed record InspireTroopsIntent(int EntityInstanceId) : WorldCommand;

/// <summary>Moves toward a cell while acquiring visible hostile actors.</summary>
public sealed record AttackMoveIntent(int EntityInstanceId, CellCoordinate TargetCell) : WorldCommand;

/// <summary>Reserves one decoded build-tree item and deducts its P7 cost.</summary>
public sealed record PurchaseIntent(int TeamId, int DependencyItemId) : WorldCommand;

/// <summary>Targets an entity-40 vent for the recovered EXPL/SLUG exact-cell attachment path.</summary>
public sealed record HarvestVentIntent(int EntityInstanceId, int VentId) : WorldCommand;

/// <summary>Returns an attached EDPLY/SDPL actor to its original mobile harvester form.</summary>
public sealed record RetractHarvesterIntent(int EntityInstanceId) : WorldCommand;

/// <summary>Converts a Human Engineer or Gray Sloom in place into its faction-matched mine form.</summary>
public sealed record DeployMineIntent(int EntityInstanceId) : WorldCommand;

/// <summary>Converts a Human Turret builder or Gray Xenowort into its shipped static tower form.</summary>
public sealed record DeployTowerIntent(int EntityInstanceId) : WorldCommand;

/// <summary>Converts a Cyborg/Psy-raider into its recovered static stealing stance.</summary>
public sealed record DeployStealIntent(int EntityInstanceId) : WorldCommand;

/// <summary>Returns a deployed SARGSTL/PSYCSTL actor to its original mobile form.</summary>
public sealed record RetractStealIntent(int EntityInstanceId) : WorldCommand;

/// <summary>
/// Drops a previously paid building onto the requested map origin. Construction
/// owns validation, footprint reservation, and the later tech-tree completion.
/// </summary>
public sealed record PlaceBuildingIntent(int TeamId, int DependencyItemId, CellCoordinate Origin) : WorldCommand;

/// <summary>
/// Creates a paid troop beside one completed prerequisite structure.
/// The source structure is a temporary port-side spawn anchor: recovered
/// <c>dc.exe</c> troop dependency/order flow creates command-10 with player,
/// troop type, and count, but has not established a building-instance payload.
/// </summary>
public sealed record ProduceUnitIntent(int TeamId, int DependencyItemId, int SourceBuildingInstanceId) : WorldCommand;

/// <summary>Completes a paid upgrade from one of its required research structures.</summary>
public sealed record ResearchIntent(int TeamId, int DependencyItemId, int SourceBuildingInstanceId) : WorldCommand;

public readonly record struct ScheduledWorldCommand(ulong Tick, ulong Sequence, WorldCommand Command);
