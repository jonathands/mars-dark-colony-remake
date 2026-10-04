using DarkColony.Engine.Commands;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

public sealed class SimulatedActor
{
    internal SimulatedActor(WorldEntity seed, EntityDefinition definition)
    {
        Seed = seed;
        Definition = definition;
        Movement = new MovementState(seed.SpawnCell);
        Facing = new FacingState();
        Health = definition.Health;
        MaximumHealth = definition.Health;
        AbilityCharge = NativeInitialAbilityCharge;
    }

    public WorldEntity Seed { get; }
    public EntityDefinition Definition { get; }
    public MovementState Movement { get; }
    public FacingState Facing { get; }
    /// <summary>Authoritative live health; catalog health is the immutable maximum.</summary>
    public int Health { get; internal set; }
    /// <summary>
    /// Current form's health ceiling. Most actors keep their seed definition,
    /// but an in-place deployment must not retain a mobile form's stat cap.
    /// </summary>
    public int MaximumHealth { get; internal set; }
    public bool IsDestroyed => Health <= 0;
    public const int NativeInitialAbilityCharge = 0x40;
    public const int NativeMaximumAbilityCharge = 0xff;
    /// <summary>Native actor byte +0x0a, used as the BEON/ZISP heal charge.</summary>
    public int AbilityCharge { get; internal set; }
    public PackedPathPlayback? Playback { get; internal set; }
    /// <summary>
    /// The cell transition an order interrupted. The step command (type 5,
    /// <c>0x4125BC</c>) never checks for a pending order, so the actor ends
    /// the transition before its new order runs.
    /// </summary>
    public PackedPathPlayback? FinishingStep { get; internal set; }
    public ActiveMoveOrder? MoveOrder { get; internal set; }
    /// <summary>Native actor byte +0x35: an allied mover's blocked direction.</summary>
    public PathDirection? YieldNotificationDirection { get; internal set; }
    /// <summary>
    /// The vent this harvester is travelling to or is currently attached to.
    /// This is authoritative economy state, rather than a presentation-only
    /// Deploy cursor mode.
    /// </summary>
    public int? HarvestVentId { get; internal set; }
    /// <summary>
    /// Data-resolved visual form used while a mobile P7 harvester is deployed.
    /// The seed identity remains the original Exploiter/Slug so economy and
    /// commands do not pretend this is a separately spawned building.
    /// </summary>
    public int? DeployedEntityId { get; internal set; }
    /// <summary>Remaining ticks of the native state-13 change between SARG/PSYC and SARGSTL/PSYCSTL.</summary>
    public int StealTransitionTicksRemaining { get; internal set; }
    /// <summary>Stealing stance: the deployed harvester it drains (its command record word +0, <c>0x417E75</c>).</summary>
    public int? StealVictimInstanceId { get; internal set; }
    /// <summary>Deployed harvester: the stance draining it (its command record word +4, <c>0x417EB8</c>).</summary>
    public int? ThiefInstanceId { get; internal set; }
    /// <summary>
    /// Native byte <c>+0xCA</c>: bit t is set when a mine detector of team t saw
    /// this actor in the mine grid at the last visibility refresh.
    /// </summary>
    public int RevealedTeamMask { get; internal set; }
    public int? AttackTargetInstanceId { get; internal set; }
    /// <summary>Pending one-shot opcode-0x1b/state-18 ground target.</summary>
    public CellCoordinate? GroundSpecialAttackTarget { get; internal set; }
    /// <summary>Player destination retained while attack-move pursues hostiles.</summary>
    public CellCoordinate? AttackMoveDestination { get; internal set; }
    /// <summary>
    /// Native actor byte <c>+0x34</c>: shots fired in the active weapon burst.
    /// It resets when the weapon's decoded burst limit is reached.
    /// </summary>
    public int BurstShotCount { get; internal set; }
    public int CooldownTicks { get; internal set; }
    /// <summary>Remaining ticks in the native state-13 mine deployment.</summary>
    public int MineDeployTicksRemaining { get; internal set; }
    /// <summary>Remaining ticks in the commander's native state-13 cast.</summary>
    public int InspireCastTicksRemaining { get; internal set; }
    /// <summary>
    /// Native actor byte <c>+0xd6</c>. A nonzero value forces exact-center aim;
    /// it is decremented once per 16 world updates.
    /// </summary>
    public int InspirationTicksRemaining { get; internal set; }
    /// <summary>Native actor word <c>+0xd8</c>: the commander supplying Inspire.</summary>
    public int? InspirationSourceActorInstanceId { get; internal set; }
    /// <summary>
    /// True while the actor is in the native idle command (type 3, handler
    /// <c>0x4148B0</c>) and its record below is live.
    /// </summary>
    public bool IdleCommandActive { get; internal set; }
    /// <summary>Idle record word <c>+2</c>: health when the last scan ran, to detect damage.</summary>
    public int IdleHealthSnapshot { get; internal set; }
    /// <summary>Idle record word <c>+4</c>: consecutive empty scans, capped at 3.</summary>
    public int IdleMissCount { get; internal set; }
    /// <summary>Whether the type-3 wait (<c>0x412274</c>) sits above the idle record.</summary>
    public bool IdleWaiting { get; internal set; }
    /// <summary>The wait's counter (15, or 45 after three misses); it ends one update after reaching zero.</summary>
    public int IdleWaitTicks { get; internal set; }
    /// <summary>The wait's word +2: health when it was pushed. Any change ends the wait at once.</summary>
    public int IdleWaitHealth { get; internal set; }
    /// <summary>
    /// The fidget (type 4, <c>0x412338</c>): a random bearing the actor turns to
    /// after the wait, before the idle record scans again.
    /// </summary>
    public byte? IdleFidgetFacing { get; internal set; }
    /// <summary>
    /// The move the idle command pushed itself (approach or yield). The idle
    /// record stays below it on the native command stack.
    /// </summary>
    internal ActiveMoveOrder? IdleIssuedMove { get; set; }
    /// <summary>The target the idle command started attacking; the idle record stays below the attack.</summary>
    internal int? IdleIssuedAttackTarget { get; set; }
}

/// <summary>Persistent player intent, segmented by the native 32-step buffer.</summary>
public sealed class ActiveMoveOrder
{
    // dc.exe keeps a fixed eight-point player waypoint list. Retaining that
    // boundary in the authoritative model makes UI and later replay/network
    // input agree on what a single order can contain.
    public const int MaximumWaypoints = 8;
    public ActiveMoveOrder(CellCoordinate target) => Target = target;

    private readonly Queue<CellCoordinate> waypoints = [];

    /// <summary>The destination currently being segmented into packed local paths.</summary>
    public CellCoordinate Target { get; private set; }
    public int PendingWaypointCount => waypoints.Count;
    /// <summary>
    /// Remaining player destinations in execution order. This is a read-only
    /// projection for HUD/replay inspection; queue mutation remains internal
    /// to deterministic command processing.
    /// </summary>
    public IReadOnlyList<CellCoordinate> PendingWaypoints => waypoints.ToArray();
    public int SegmentCount { get; internal set; }
    public int BlockedTicksRemaining { get; internal set; }
    /// <summary>
    /// The blocked step's wait (<c>0x4157C6</c>: a type-3 wait of 4) is
    /// running; the move then retries <see cref="KeptSteps"/>.
    /// </summary>
    public bool BlockedWaiting { get; internal set; }
    /// <summary>The wait's health word: a change ends it in the same update.</summary>
    public int BlockedWaitHealth { get; internal set; }
    /// <summary>The path steps the move record keeps through the wait, starting with the blocked one.</summary>
    public IReadOnlyList<PathDirection> KeptSteps { get; internal set; } = [];
    public CellCoordinate? LastBlockedCell { get; internal set; }
    /// <summary>
    /// Native move mode 2, issued by the idle handler to close on a distant
    /// hostile: each path step first scans weapon range and the move ends as
    /// soon as a target is found.
    /// </summary>
    public bool StopOnContact { get; internal init; }

    public bool TryAppendWaypoint(CellCoordinate target)
    {
        // The active destination is the predecessor while the queued list is
        // empty. Treat it exactly like the tail of a non-empty queue so a
        // Shift-click on the current marker cannot insert a zero-length first
        // waypoint ahead of the player's next real destination.
        var previous = waypoints.Count == 0 ? Target : waypoints.Last();
        if (waypoints.Count >= MaximumWaypoints || previous == target) return false;
        waypoints.Enqueue(target);
        return true;
    }

    public bool AdvanceWaypoint()
    {
        if (!waypoints.TryDequeue(out var next)) return false;
        Target = next;
        SegmentCount = 0;
        BlockedTicksRemaining = 0;
        BlockedWaiting = false;
        KeptSteps = [];
        LastBlockedCell = null;
        return true;
    }

    internal void JitterTarget(CellCoordinate target)
    {
        Target = target;
        SegmentCount = 0;
        BlockedTicksRemaining = 0;
        BlockedWaiting = false;
        KeptSteps = [];
    }
}
