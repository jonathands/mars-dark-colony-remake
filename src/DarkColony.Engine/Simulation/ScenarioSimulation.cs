using DarkColony.Engine.Commands;
using DarkColony.Engine.Data;
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
    }

    public WorldEntity Seed { get; }
    public EntityDefinition Definition { get; }
    public MovementState Movement { get; }
    public FacingState Facing { get; }
    public PackedPathPlayback? Playback { get; internal set; }
    public ActiveMoveOrder? MoveOrder { get; internal set; }
}

/// <summary>Persistent player intent, segmented by the native 32-step buffer.</summary>
public sealed class ActiveMoveOrder
{
    public ActiveMoveOrder(CellCoordinate target) => Target = target;

    public CellCoordinate Target { get; }
    public int SegmentCount { get; internal set; }
    public int BlockedTicksRemaining { get; internal set; }
    public CellCoordinate? LastBlockedCell { get; internal set; }
}

public sealed record MoveCommandOutcome(
    int EntityInstanceId,
    CellCoordinate Target,
    DiagnosticPathTermination PathTermination,
    int StepCount);

/// <summary>Authoritative local mission state; presentation only reads it.</summary>
public sealed class ScenarioSimulation
{
    private readonly PathRegionMap path;
    private readonly Dictionary<int, SimulatedActor> actorsById;

    private ScenarioSimulation(
        PathRegionMap path,
        IReadOnlyList<SimulatedActor> actors,
        CellOccupancy groundOccupancy,
        CellOccupancy alternateOccupancy)
    {
        this.path = path;
        Actors = actors;
        actorsById = actors.ToDictionary(actor => actor.Seed.InstanceId);
        GroundOccupancy = groundOccupancy;
        AlternateOccupancy = alternateOccupancy;
    }

    public IReadOnlyList<SimulatedActor> Actors { get; }
    public CellOccupancy GroundOccupancy { get; }
    public CellOccupancy AlternateOccupancy { get; }
    public IReadOnlyList<MoveCommandOutcome> LastMoveOutcomes { get; private set; } = [];

    public static ScenarioSimulation Create(
        ScenarioDefinition scenario,
        EntityCatalog catalog,
        PathRegionMap path,
        BuildingFootprintCatalog? footprints = null)
    {
        var seeds = scenario.Placements.Where(placement => placement.Team != -1).Select((placement, index) =>
        {
            var cell = new CellCoordinate(placement.X, placement.Z);
            return new WorldEntity(index + 1, placement.EntityId, placement.Team, cell,
                FixedPointPosition.AtCellCenter(cell), placement.Value, placement.Flag);
        }).ToList();
        var ground = new CellOccupancy();
        var alternate = new CellOccupancy();
        foreach (var seed in seeds)
        {
            if ((uint)seed.EntityId >= (uint)catalog.Entities.Count)
                throw new InvalidDataException($"SCN entity ID {seed.EntityId} is outside gamestat.txt.");
            var footprint = footprints?.OccupiedCells(seed.EntityId, seed.SpawnCell) ?? [];
            var definition = catalog[seed.EntityId];
            if (footprint.Count == 0 && definition.MovementSpeed <= 0) continue;
            var occupancy = footprint.Count != 0 || definition.MovementClass == 0 ? ground : alternate;
            IReadOnlyList<CellCoordinate> cells = footprint.Count == 0 ? [seed.SpawnCell] : footprint;
            occupancy.ReplaceClaims(seed.InstanceId, cells);
        }

        var autonomous = AutonomousSpawnSeeder.Seed(
            scenario.AutonomousSpawnGroups, catalog, path, ground, alternate, seeds.Count + 1);
        seeds.AddRange(autonomous.Entities);
        var actors = seeds.Select(seed => new SimulatedActor(seed, catalog[seed.EntityId])).ToArray();
        return new ScenarioSimulation(path, actors, ground, alternate);
    }

    public SimulatedActor? Actor(int instanceId) => actorsById.GetValueOrDefault(instanceId);

    public void Step(IEnumerable<ScheduledWorldCommand> commands)
    {
        var outcomes = new List<MoveCommandOutcome>();
        foreach (var scheduled in commands.OrderBy(command => command.Sequence))
        {
            if (scheduled.Command is not MoveIntent move || !actorsById.TryGetValue(move.EntityInstanceId, out var actor)) continue;
            if (actor.Definition.MovementSpeed <= 0)
            {
                outcomes.Add(new MoveCommandOutcome(actor.Seed.InstanceId, move.TargetCell, DiagnosticPathTermination.InvalidEndpoint, 0));
                continue;
            }
            actor.MoveOrder = new ActiveMoveOrder(move.TargetCell);
            outcomes.Add(StartSegment(actor));
        }
        LastMoveOutcomes = outcomes;

        foreach (var actor in Actors)
        {
            if (actor.Facing.Current != actor.Facing.Target && actor.Definition.TurnSpeed > 0)
                actor.Facing.Step(actor.Definition.TurnSpeed);
            if (actor.Playback is not null)
            {
                var status = actor.Playback.Step();
                if (status == PackedPathPlaybackStatus.Blocked)
                {
                    actor.MoveOrder ??= new ActiveMoveOrder(actor.Movement.OccupiedCell);
                    actor.MoveOrder.LastBlockedCell = actor.Playback.BlockedCell;
                    actor.MoveOrder.BlockedTicksRemaining = 4;
                    actor.Playback = null;
                }
                else if (status == PackedPathPlaybackStatus.Complete)
                {
                    actor.Playback = null;
                    if (actor.MoveOrder?.Target == actor.Movement.OccupiedCell) actor.MoveOrder = null;
                }
            }

            if (actor.Playback is not null || actor.MoveOrder is null) continue;
            if (actor.MoveOrder.BlockedTicksRemaining > 0)
            {
                actor.MoveOrder.BlockedTicksRemaining--;
                continue;
            }
            _ = StartSegment(actor);
        }
    }

    private MoveCommandOutcome StartSegment(SimulatedActor actor)
    {
        var order = actor.MoveOrder ?? throw new InvalidOperationException("Actor has no move order.");
        if (actor.Movement.OccupiedCell == order.Target)
        {
            actor.MoveOrder = null;
            return new MoveCommandOutcome(actor.Seed.InstanceId, order.Target, DiagnosticPathTermination.ReachedTarget, 0);
        }

        var finder = new DiagnosticLocalPathfinder(path, GroundOccupancy, AlternateOccupancy);
        var local = finder.Find(actor.Movement.OccupiedCell, order.Target, actor.Definition.MovementClass, actor.Seed.InstanceId);
        if (local.Steps.Count == 0)
        {
            // A route failure is not a completed order. Keep it active and
            // retry at the recovered four-execution blocked cadence.
            order.BlockedTicksRemaining = 4;
            return new MoveCommandOutcome(actor.Seed.InstanceId, order.Target, local.Termination, 0);
        }

        var occupancy = actor.Definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        actor.Playback = new PackedPathPlayback(actor.Seed.InstanceId, actor.Definition.MovementSpeed, actor.Movement, local.Steps, occupancy, actor.Facing);
        order.SegmentCount++;
        return new MoveCommandOutcome(actor.Seed.InstanceId, order.Target, local.Termination, local.Steps.Count);
    }
}
