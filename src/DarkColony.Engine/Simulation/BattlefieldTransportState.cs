using DarkColony.Engine.Movement;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// Native entity-92/93 transport lifecycle recovered from dc.exe
/// 0x4182e8, 0x4183b8, 0x418a48, and 0x418f4c.
/// </summary>
public enum BattlefieldTransportPhase
{
    Descending,
    Delivering,
    Ascending,
}

public enum BattlefieldTransportEventKind
{
    Started,
    PayloadResolved,
    Departed,
}

public sealed class BattlefieldTransportState
{
    private readonly List<int> pendingEntityIds;
    private readonly List<int> pendingAbducteeInstanceIds;

    internal BattlefieldTransportState(
        int instanceId,
        int sourceActorInstanceId,
        int transportEntityId,
        int teamId,
        CellCoordinate target,
        FixedPointPosition position,
        int baseHeightRaw,
        byte initialFacing,
        IEnumerable<int> pendingEntityIds,
        IEnumerable<int> pendingAbducteeInstanceIds)
    {
        InstanceId = instanceId;
        SourceActorInstanceId = sourceActorInstanceId;
        TransportEntityId = transportEntityId;
        TeamId = teamId;
        Target = target;
        Position = position;
        BaseHeightRaw = baseHeightRaw;
        HeightRaw = baseHeightRaw + 3 * ScenarioSimulation.NativeTransportFlightTicks * ScenarioSimulation.NativeTransportFlightTicks;
        Facing = new FacingState(initialFacing);
        this.pendingEntityIds = pendingEntityIds.ToList();
        this.pendingAbducteeInstanceIds = pendingAbducteeInstanceIds.ToList();
    }

    public int InstanceId { get; }
    public int SourceActorInstanceId { get; }
    public int TransportEntityId { get; }
    public int TeamId { get; }
    public CellCoordinate Target { get; }
    public FixedPointPosition Position { get; internal set; }
    public int BaseHeightRaw { get; }
    public int HeightRaw { get; internal set; }
    public FacingState Facing { get; }
    public BattlefieldTransportPhase Phase { get; internal set; } = BattlefieldTransportPhase.Descending;
    public int FlightCounter { get; internal set; } = ScenarioSimulation.NativeTransportFlightTicks;
    internal bool DeliveryInitialized { get; set; }
    internal bool PayloadHeaderProcessed { get; set; }
    internal int PayloadIndex { get; set; }
    public FixedPointPosition? PursuitTarget { get; internal set; }
    public int HorizontalExecutionsRemaining { get; internal set; }
    internal int HorizontalVelocityXRaw { get; set; }
    internal int HorizontalVelocityZRaw { get; set; }
    public bool IsTurning { get; internal set; }
    public bool IsPursuing => PursuitTarget is not null;
    internal IReadOnlyList<int> PendingEntityIds => pendingEntityIds;
    internal IReadOnlyList<int> PendingAbducteeInstanceIds => pendingAbducteeInstanceIds;
    internal int PayloadCount => TransportEntityId == 92 ? pendingEntityIds.Count : pendingAbducteeInstanceIds.Count;
}
