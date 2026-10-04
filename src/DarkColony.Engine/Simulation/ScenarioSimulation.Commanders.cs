namespace DarkColony.Engine.Simulation;

/// <summary>
/// Each player's four commander slots: player <c>+0xD98</c> counts them and
/// <c>+0xD9C</c> holds their actor indices (word -1 = empty, set by the SCN
/// loader <c>0x41C2BD</c>).
/// </summary>
public sealed partial class ScenarioSimulation
{
    public const int NativeCommanderSlots = 4;
    /// <summary>Ability charge the world update forces on every live commander (<c>0x4197D2</c>).</summary>
    public const int NativeCommanderCharge = 0xe6;
    private const int FirstCommanderEntity = 69;
    private const int LastCommanderEntity = 76;

    private readonly int[,] commanderSlots = NewCommanderSlots();
    private readonly int[] commanderCounts = new int[PlayerCount];

    /// <summary>The actor in a player's commander slot, or null when the slot is empty.</summary>
    public int? CommanderInSlot(int player, int slot) =>
        (uint)player < PlayerCount && (uint)slot < NativeCommanderSlots && commanderSlots[player, slot] >= 0
            ? commanderSlots[player, slot]
            : null;

    private static int[,] NewCommanderSlots()
    {
        var slots = new int[PlayerCount, NativeCommanderSlots];
        for (var player = 0; player < PlayerCount; player++)
            for (var slot = 0; slot < NativeCommanderSlots; slot++) slots[player, slot] = -1;
        return slots;
    }

    /// <summary>
    /// The actor constructor (<c>0x41B223</c>) puts a commander (entities
    /// 69-76) of a player into slot [count] and increments the count, which
    /// never goes down. A fifth commander trips an assertion in the original
    /// and writes past the slots; the port leaves it unregistered.
    /// </summary>
    private void RegisterCommander(SimulatedActor actor)
    {
        var team = actor.Seed.Team;
        if ((uint)team >= PlayerCount || actor.Seed.EntityId is < FirstCommanderEntity or > LastCommanderEntity) return;
        if (commanderCounts[team] >= NativeCommanderSlots) return;
        commanderSlots[team, commanderCounts[team]++] = actor.Seed.InstanceId;
    }

    /// <summary>
    /// Right after the statistics recount (<c>0x4197B4</c>): every live
    /// commander in a slot gets ability charge 230.
    /// </summary>
    private void ChargeCommanders()
    {
        for (var player = 0; player < PlayerCount; player++)
        for (var slot = 0; slot < NativeCommanderSlots; slot++)
            if (commanderSlots[player, slot] >= 0 && actorsById.TryGetValue(commanderSlots[player, slot], out var commander) &&
                !commander.IsDestroyed)
                commander.AbilityCharge = NativeCommanderCharge;
    }

    /// <summary>
    /// After the norm triggers (<c>0x419AB5</c>): a slot whose actor is gone
    /// becomes -1. The original then zeroes statistic 11 of the player whose
    /// index equals the slot index rather than of the slot's owner; the port
    /// keeps that.
    /// </summary>
    private void ClearDeadCommanders()
    {
        for (var player = 0; player < PlayerCount; player++)
        for (var slot = 0; slot < NativeCommanderSlots; slot++)
        {
            var actorId = commanderSlots[player, slot];
            if (actorId < 0 || actorsById.TryGetValue(actorId, out var commander) && !commander.IsDestroyed) continue;
            commanderSlots[player, slot] = -1;
            playerStats[slot, 11] = 0;
        }
    }

    /// <summary>
    /// Mission action <c>abduct s d</c> (<c>0x43E2A0</c>): when player d's
    /// first commander slot holds a live actor, a transport of team s's race
    /// comes for it. Its payload is the abduction header 0xff01 and the actor.
    /// </summary>
    private void StartAbduction(IReadOnlyList<int> values, ICollection<BattlefieldTransportEvent> events)
    {
        var team = values[0];
        if (CommanderInSlot(values[1], 0) is not { } commanderId || !actorsById.TryGetValue(commanderId, out var commander) ||
            commander.IsDestroyed) return;
        var transportEntityId = teamRaces.GetValueOrDefault(team) == 1 ? 93 : 92;
        StartBattlefieldTransport(-1, team, transportEntityId, commander.Movement.OccupiedCell, [], [commanderId], events);
    }
}
