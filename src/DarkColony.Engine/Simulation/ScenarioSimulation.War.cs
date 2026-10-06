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
    /// Whether a player is still in the War. The port keeps a player with a
    /// city in the game while one of its city buildings in slots 0-4 stands:
    /// losing every building puts it out, whatever units it has left (the
    /// user's rule, 2026-10-06). An empty War position has a city with no
    /// buildings, so it is out from the start.
    /// <c>0x40DE20</c> differs: there any live actor (state 1) keeps the
    /// player in, except city slot 5, deployed mobile towers (entities 41 and
    /// 42) and mines (45 and 46), so units and an empty position's placed
    /// units count too. A player without a city keeps that rule.
    /// </summary>
    public bool IsPlayerInGame(int player)
    {
        if ((uint)player >= PlayerCount) return false;
        if (cityOrigins.ContainsKey(player))
            return Enumerable.Range(0, UncountedCitySlot).Any(slot => CityBuilding(player, slot) is not null);
        return actors.Any(actor =>
            !actor.IsDestroyed && actor.Seed.Team == player &&
            EffectiveDefinition(actor).Id is not (41 or 42 or 45 or 46));
    }

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
