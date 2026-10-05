using DarkColony.Engine.Commands;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Movement;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// One of a player's four troop queues (player <c>+0xCA8 + queue * 2</c> holds
/// the length, <c>+0xCB0 + queue * 800</c> the entity types).
/// </summary>
public sealed class CityProductionQueue
{
    internal CityProductionQueue(int teamId, int queue)
    {
        TeamId = teamId;
        Queue = queue;
    }

    public int TeamId { get; }
    public int Queue { get; }
    internal List<(int ItemId, int EntityId)> Items { get; } = [];
    public IReadOnlyList<int> QueuedEntityIds => Items.Select(item => item.EntityId).ToArray();
    /// <summary>Player byte <c>+0xCA0 + queue</c>: false while the front troop is being built.</summary>
    public bool Ready { get; internal set; } = true;
    /// <summary>Ticks until the building's one-shot build animation stops.</summary>
    public int TicksRemaining { get; internal set; }
    /// <summary>The exit cell held for the troop being built (native grid marker <c>0x3FE</c>).</summary>
    public CellCoordinate? ReservedExit { get; internal set; }
}

/// <summary>
/// Troop production by city buildings. An order (command 10, <c>0x41C7F8</c>)
/// appends the troop to its player queue (entity value 21). Each tick the
/// city building whose slot runs that queue (<c>0x41AE30</c>) works its front
/// from its idle command (<c>0x414314</c>).
/// </summary>
public sealed partial class ScenarioSimulation
{
    /// <summary>Native actor slots for everything outside the city ranges (800 - 0x98).</summary>
    public const int NativeDynamicActorSlots = 0x288;
    /// <summary>The SCN loader's upper bound on the troop cap (<c>world + 4 = 0x96</c>).</summary>
    public const int NativeTroopCapLimit = 0x96;
    /// <summary>
    /// The grid marker (<c>0x3FE</c>) a building leaves on a production exit while
    /// a troop is built. Only the troop's arrival replaces it.
    /// </summary>
    internal const int ProductionReservationOwner = -0x3fe;

    public IReadOnlyList<CityProductionQueue> ProductionQueues => productionQueues.Values.ToArray();

    /// <summary>
    /// Units a player may field (<c>world + 0x528</c>), recomputed by
    /// <c>0x41E6AC</c> every update. Start from the 648 dynamic actor slots,
    /// then subtract the critter groups' desired populations, every live actor
    /// of teams 0-8 that have no live city slot 0-4 (team 8 holds the vents),
    /// and 100. Divide the rest among the players with a city, and clamp to 150.
    /// </summary>
    public int TroopCap { get; private set; }

    private int ComputeTroopCap()
    {
        var remaining = NativeDynamicActorSlots - critterReserve;
        var activePlayers = 0;
        var active = new bool[9];
        for (var player = 0; player < 8; player++)
        {
            for (var slot = 0; slot < 5 && !active[player]; slot++) active[player] = CityBuilding(player, slot) is not null;
            if (active[player]) activePlayers++;
        }
        var cityActors = cityBuildings.Values.ToHashSet();
        foreach (var actor in actors)
        {
            if (!IsInWorld(actor) || cityActors.Contains(actor.Seed.InstanceId) || actor.Seed.Team is < 0 or > 8) continue;
            if (!active[actor.Seed.Team]) remaining--;
        }
        // The vents are team-8 actors in the native world.
        remaining -= PetraVents.Count;
        remaining -= 100;
        if (activePlayers > 0) remaining /= activePlayers;
        return Math.Min(remaining, NativeTroopCapLimit);
    }

    private UnitProducedEvent EnqueueTroop(ProduceUnitIntent intent, int entityId, TeamEconomy economy)
    {
        var queue = EntityDefinitionFor(entityId).ProductionQueue;
        if (!productionQueues.TryGetValue((intent.TeamId, queue), out var state))
            return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.SourceBuildingInstanceId, UnitProductionOutcome.NoProductionQueue);
        if (!economy.ConsumeReservation(dependencyCatalog, intent.DependencyItemId))
            return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.SourceBuildingInstanceId, UnitProductionOutcome.NotReserved);
        state.Items.Add((intent.DependencyItemId, entityId));
        return new UnitProducedEvent(intent.TeamId, intent.DependencyItemId, 0, entityId, intent.SourceBuildingInstanceId, UnitProductionOutcome.Queued);
    }

    /// <summary>
    /// <c>0x414314</c> for every live city building with a queue, in native
    /// actor order (city buildings are actors 0-119, before all others).
    /// A ready queue checks the front troop's exit: an occupant is told to
    /// step aside, a free exit is reserved while the building plays the
    /// troop's build animation, and the troop appears when it stops (or at
    /// once if the troop has none). The troop cap (<c>world + 0x528</c>) and
    /// its refund are not modeled; the native cooldown byte
    /// <c>+0xCA4 + queue</c> is never set nonzero.
    /// </summary>
    private void UpdateCityProduction(TickEvents events)
    {
        if (footprints is null) return;
        foreach (var ((team, slot), instanceId) in cityBuildings.OrderBy(pair => pair.Key.Team).ThenBy(pair => pair.Key.Slot))
        {
            if (footprints.SlotProductionQueue(slot) is not { } queue ||
                !productionQueues.TryGetValue((team, queue), out var state) ||
                !actorsById.TryGetValue(instanceId, out var building) || building.IsDestroyed ||
                // Command 19 sits above the idle command until the delivery ends.
                deliveries.ContainsKey(instanceId)) continue;
            if (!state.Ready)
            {
                if (--state.TicksRemaining > 0) continue;
                SpawnQueuedTroop(state, state.ReservedExit!.Value, building, events);
                state.ReservedExit = null;
                state.Ready = true;
                continue;
            }
            if (state.Items.Count == 0) continue;
            var entityId = state.Items[0].EntityId;
            var definition = EntityDefinitionFor(entityId);
            var exit = footprints.ProductionExit(cityOrigins[team], queue, definition.ProductionExitVariant);
            if ((uint)exit.X >= (uint)path.Width || (uint)exit.Z >= (uint)path.Height) continue;
            var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
            if (occupancy.TryGetOwner(exit, out var occupantId))
            {
                // 0x41477F clears the occupant's +0x35 byte to direction 0. A
                // leftover 0x3FE marker has no actor and blocks the queue.
                if (actorsById.TryGetValue(occupantId, out var occupant))
                    occupant.YieldNotificationDirection = PathDirection.NorthWest;
                continue;
            }
            if (playerStats[team, 6] >= TroopCap)
            {
                // 0x414314: at the cap the order is dropped and its price refunded.
                var (itemId, _) = state.Items[0];
                state.Items.RemoveAt(0);
                if (dependencyCatalog?.TryGet(itemId, out var item) == true && teamEconomies.TryGetValue(team, out var refunded))
                    refunded.AddP7(item.Cost);
                events.UnitProductions.Add(new UnitProducedEvent(team, itemId, 0, entityId, building.Seed.InstanceId, UnitProductionOutcome.CapReached));
                continue;
            }
            if (buildTimings?.BuildTicks(entityId) is not { } ticks)
            {
                SpawnQueuedTroop(state, exit, building, events);
                continue;
            }
            occupancy.ReplaceClaims(ProductionReservationOwner, [exit]);
            state.ReservedExit = exit;
            state.TicksRemaining = ticks;
            state.Ready = false;
        }
    }

    private void SpawnQueuedTroop(CityProductionQueue state, CellCoordinate exit, SimulatedActor building, TickEvents events)
    {
        var (itemId, entityId) = state.Items[0];
        state.Items.RemoveAt(0);
        var definition = EntityDefinitionFor(entityId);
        var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        var instanceId = nextActorInstanceId++;
        // 0x41AF14 writes the new actor into the grid without testing the cell.
        occupancy.ReplaceClaims(instanceId, [exit]);
        var seed = new WorldEntity(instanceId, entityId, state.TeamId, exit, FixedPointPosition.AtCellCenter(exit), 0, 0);
        var actor = new SimulatedActor(seed, definition);
        actors.Add(actor);
        actorsById.Add(instanceId, actor);
        RegisterCommander(actor);
        events.UnitProductions.Add(new UnitProducedEvent(state.TeamId, itemId, instanceId, entityId, building.Seed.InstanceId, UnitProductionOutcome.Produced));
    }
}
