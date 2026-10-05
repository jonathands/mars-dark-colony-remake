namespace DarkColony.Engine.Simulation;

/// <summary>
/// The end of a War, Single Player or network (<c>docs/reverse-engineering/war-session.md</c>,
/// "End of the game"). In a War (game type 1 or 2) the gameplay loop
/// (<c>0x40A8E0</c>) ends the game once <see cref="IsWarOver"/> holds; War
/// scenarios have no <c>bail</c> triggers. These are queries: the host
/// decides what to show.
/// </summary>
public sealed partial class ScenarioSimulation
{
    /// <summary>City slot 5 does not keep a player in the game (<c>0x40DE5C</c>: city actor index % 15 == 5).</summary>
    public const int UncountedCitySlot = 5;

    /// <summary>
    /// <c>0x40DE20</c>: a player is in the game while one of its actors is
    /// alive (state 1). City slot 5, deployed mobile towers (entities 41
    /// and 42) and mines (45 and 46) do not count. Units count as well as
    /// buildings, so do the placed units of an empty War position.
    /// </summary>
    public bool IsPlayerInGame(int player) =>
        (uint)player < PlayerCount && actors.Any(actor =>
            !actor.IsDestroyed && actor.Seed.Team == player &&
            EffectiveDefinition(actor).Id is not (41 or 42 or 45 or 46) &&
            !(cityBuildings.TryGetValue((player, UncountedCitySlot), out var slotFive) && slotFive == actor.Seed.InstanceId));

    /// <summary>
    /// <c>0x40DEAC</c>: every player still in the game is allied both ways
    /// (<c>0x41E820</c>) with the first of them. A lone survivor, or no one,
    /// also ends the game.
    /// </summary>
    public bool IsWarOver()
    {
        int? first = null;
        for (var player = 0; player < PlayerCount; player++)
        {
            if (!IsPlayerInGame(player)) continue;
            if (first is not { } lead) first = player;
            else if (!AreAllied(lead, player)) return false;
        }
        return true;
    }
}
