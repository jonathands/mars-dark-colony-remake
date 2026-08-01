using DarkColony.Engine.World;

namespace DarkColony.Engine.Commands;

/// <summary>External deterministic intent; distinct from the native six-slot actor queue.</summary>
public abstract record WorldCommand;

public sealed record MoveIntent(int EntityInstanceId, CellCoordinate TargetCell, bool AppendWaypoint = false) : WorldCommand;

public readonly record struct ScheduledWorldCommand(ulong Tick, ulong Sequence, WorldCommand Command);
