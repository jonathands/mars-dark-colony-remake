namespace DarkColony.Engine.Simulation;

/// <summary>The phases of a city building's delivery, command 19 (<c>0x4187E4</c>).</summary>
public enum BuildingDeliveryPhase
{
    /// <summary>Phase 0: another delivery of the player is under way (player <c>+0x19AA</c>).</summary>
    Waiting,
    /// <summary>Phase 1: the drop ship or saucer comes down (command 22, 50 updates).</summary>
    Arriving,
    /// <summary>Phase 2: the building's own build animation plays once.</summary>
    Building,
    /// <summary>Phase 3: the ship goes back up (50 updates).</summary>
    Leaving,
}

/// <summary>A delivery's phase and the updates left in it.</summary>
public readonly record struct BuildingDelivery(BuildingDeliveryPhase Phase, int TicksRemaining, int PhaseTicks);

/// <summary>
/// A purchased city building is delivered (<c>docs/reverse-engineering/production-flow.md</c>,
/// "Building delivery"). Command 9's rebuild (<c>0x444F14</c>) calls
/// <c>0x41822C</c>, which marks the slot rising (player <c>+0xC10 + slot</c>)
/// and pushes command 19 on the new building. One delivery per player runs at
/// a time: the ship comes down, the building's <c>&lt;code&gt;BUILD0</c> animation
/// (entity <c>+0x98</c>) plays, the slot stops rising, and the ship leaves.
/// Until the command ends the building does not produce. The state is kept
/// out of the public properties, so the digest of a game without deliveries
/// is unchanged.
/// </summary>
public sealed partial class ScenarioSimulation
{
    /// <summary>Command 22 (<c>0x4183B8</c>) counts a vertical flight down from 50, or up to 50.</summary>
    public const int NativeDeliveryFlightTicks = 50;

    private sealed class DeliveryState(int team, int slot, int animationTicks)
    {
        public int Team { get; } = team;
        public int Slot { get; } = slot;
        public int AnimationTicks { get; } = animationTicks;
        public BuildingDeliveryPhase Phase { get; set; } = BuildingDeliveryPhase.Waiting;
        public int TicksRemaining { get; set; }
        public int PhaseTicks { get; set; }
    }

    private readonly SortedDictionary<int, DeliveryState> deliveries = new();
    // Player +0x19AA: a delivery is under way.
    private readonly HashSet<int> deliveringTeams = [];
    // Player +0xC10 + slot.
    private readonly HashSet<(int Team, int Slot)> risingSlots = [];

    /// <summary>The delivery of a city building, or null when it stands.</summary>
    public BuildingDelivery? Delivery(int instanceId) =>
        deliveries.TryGetValue(instanceId, out var state) ? new BuildingDelivery(state.Phase, state.TicksRemaining, state.PhaseTicks) : null;

    /// <summary>Whether a city slot's new building is still coming down (player <c>+0xC10</c>).</summary>
    public bool IsSlotRising(int team, int slot) => risingSlots.Contains((team, slot));

    /// <summary>
    /// <c>0x41822C</c> after a purchase. The handler's phase 0 (<c>0x418868</c>)
    /// plays the build animation only after the third update and when the
    /// building has one; otherwise phase 5 ends the command at once.
    /// </summary>
    private void StartDelivery(SimulatedActor building, int team, int slot)
    {
        if (TickCount + 1 <= 3 || buildTimings?.BuildTicks(building.Seed.EntityId) is not { } animationTicks) return;
        risingSlots.Add((team, slot));
        deliveries[building.Seed.InstanceId] = new DeliveryState(team, slot, animationTicks);
    }

    /// <summary>
    /// Command 19 for every delivering building, in actor order. A building
    /// killed during its delivery loses the command with the rest of its stack
    /// (<c>0x416308</c>). In the original its player then stays busy and its
    /// slot rising for the rest of the game, since only the command's end
    /// clears them. The port departs from that on purpose (decided in play
    /// testing): the killed delivery releases what it held, so the player's
    /// next delivery runs and the slot can be bought again.
    /// </summary>
    private void UpdateBuildingDeliveries()
    {
        foreach (var (instanceId, state) in deliveries.ToArray())
        {
            if (!actorsById.TryGetValue(instanceId, out var building) || building.IsDestroyed)
            {
                // Only the running delivery holds the player's flag, and the
                // slot rises until the build animation ends; once the ship is
                // leaving, the slot may already hold a new purchase.
                if (state.Phase != BuildingDeliveryPhase.Waiting) deliveringTeams.Remove(state.Team);
                if (state.Phase != BuildingDeliveryPhase.Leaving) risingSlots.Remove((state.Team, state.Slot));
                deliveries.Remove(instanceId);
                continue;
            }
            switch (state.Phase)
            {
                case BuildingDeliveryPhase.Waiting:
                    if (!deliveringTeams.Add(state.Team)) break;
                    Enter(state, BuildingDeliveryPhase.Arriving, NativeDeliveryFlightTicks);
                    break;
                case BuildingDeliveryPhase.Arriving:
                    if (--state.TicksRemaining > 0) break;
                    Enter(state, BuildingDeliveryPhase.Building, state.AnimationTicks);
                    break;
                case BuildingDeliveryPhase.Building:
                    if (--state.TicksRemaining > 0) break;
                    // 0x418997: the animation has stopped; the building stands.
                    risingSlots.Remove((state.Team, state.Slot));
                    Enter(state, BuildingDeliveryPhase.Leaving, NativeDeliveryFlightTicks);
                    break;
                case BuildingDeliveryPhase.Leaving:
                    if (--state.TicksRemaining > 0) break;
                    // 0x418A2D: the player may take the next delivery.
                    deliveringTeams.Remove(state.Team);
                    deliveries.Remove(instanceId);
                    break;
            }
        }

        static void Enter(DeliveryState state, BuildingDeliveryPhase phase, int ticks)
        {
            state.Phase = phase;
            state.TicksRemaining = ticks;
            state.PhaseTicks = ticks;
        }
    }
}
