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

/// <summary>Requests a contextual healer action against a cooperative live actor.</summary>
public sealed record HealIntent(int EntityInstanceId, int TargetEntityInstanceId) : WorldCommand;

/// <summary>Moves toward a cell while acquiring visible hostile actors.</summary>
public sealed record AttackMoveIntent(int EntityInstanceId, CellCoordinate TargetCell) : WorldCommand;

/// <summary>Reserves one decoded build-tree item and deducts its P7 cost.</summary>
public sealed record PurchaseIntent(int TeamId, int DependencyItemId) : WorldCommand;

/// <summary>Deploys an Exploiter or Gray Slug onto one decoded Petra-7 vent.</summary>
public sealed record HarvestVentIntent(int EntityInstanceId, int VentId) : WorldCommand;

/// <summary>Deploys a faction-matched mine from a Human Engineer or Gray Sloom.</summary>
public sealed record DeployMineIntent(int EntityInstanceId, CellCoordinate TargetCell) : WorldCommand;

/// <summary>Converts a Human Turret builder or Gray Xenowort into its shipped static tower form.</summary>
public sealed record DeployTowerIntent(int EntityInstanceId) : WorldCommand;

/// <summary>Converts a Cyborg/Psy-raider into its recovered static stealing stance.</summary>
public sealed record DeployStealIntent(int EntityInstanceId) : WorldCommand;

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
