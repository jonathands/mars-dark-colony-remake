using DarkColony.Engine.Data;
using DarkColony.Engine.Simulation;

namespace DarkColony.Engine.Scenario;

/// <summary>The War lobby's player types, as the session start reads them (<c>0x48A038</c>).</summary>
public enum WarSeatKind
{
    Computer = 0,
    ComputerPlus = 1,
    Human = 2,
    None = 3,
}

/// <summary>A War lobby row: who plays it, its race (0 Human, 1 Gray), and for human rows which network player owns it.</summary>
public sealed record WarLobbyRow(WarSeatKind Kind, int Race, int Owner = 0);

/// <summary>The team a lobby row was given, with what plays it.</summary>
public sealed record WarSeat(int TeamId, int Row, WarSeatKind Kind, int Race, int Owner);

/// <summary>
/// The War session start (<c>0x4014FB</c>-<c>0x401779</c>), which both Single
/// Player and Multi Player War run once the lobby confirms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shuffle.</b> The occupied rows (any type but None) are listed in row
/// order, and empty entries are -1. A Fisher-Yates pass over the map's first
/// N entries spreads them across the team positions. N is the player count in
/// the map name (<c>0x4942A5</c>, as in <c>d2play01</c>). The pass draws from
/// the shared random stream after seeding it with the session seed
/// <c>0x49469C</c>; nothing in this build writes that seed, so it is 0. Entry
/// c swaps with <c>c + r % (N - c)</c>. Team position c is player c, which is
/// scenario team c. The scenario loader (<c>0x41B933</c>) reseeds the stream
/// with the same 0 before anything else draws.
/// </para>
/// <para>
/// <b>Per position.</b> A seat's row type sets the player kind <c>+0xBBC</c>:
/// <list type="bullet">
/// <item><description>Human: 0.</description></item>
/// <item><description>Computer and Computer+: 3, the Krusty planner. Computer+ also sets
/// <c>+0x19B8</c> to 0x200, but <c>0x401799</c> overwrites that field for every player.</description></item>
/// <item><description>Empty positions: 4, a profile whose controller (<c>0x47B34C</c>) has only stub methods, so its units stand idle.</description></item>
/// </list>
/// The row's race becomes <c>+0xBB8</c>, so the SCN loader swaps placements to
/// that race. Only occupied positions are in the session (<c>+0x1524 + team</c>).
/// For any other team the loader zeroes every city slot's level and health
/// (<c>0x41C155</c>), so no building rises.
/// </para>
/// </remarks>
public static class WarSession
{
    public const int Rows = 8;

    /// <summary>The player count in a War map's name (<c>d2play01</c> holds 2).</summary>
    public static int PlayerPositions(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        return stem.Length > 1 && char.IsAsciiDigit(stem[1]) ? Math.Clamp(stem[1] - '0', 1, Rows) : Rows;
    }

    /// <summary>The team position each occupied row gets (indexed by team; null where nobody plays).</summary>
    public static IReadOnlyList<WarSeat?> Assign(string stem, IReadOnlyList<WarLobbyRow> rows, NativeRandomTable random)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(random);
        var order = Enumerable.Repeat(-1, Rows).ToArray();
        var occupied = 0;
        for (var row = 0; row < Math.Min(Rows, rows.Count); row++)
            if (rows[row].Kind != WarSeatKind.None) order[occupied++] = row;

        var positions = PlayerPositions(stem);
        var cursor = 0;
        for (var position = 0; position < positions; position++)
        {
            cursor = (cursor + 1) & (NativeRandomTable.Length - 1);
            var swap = position + (int)(random[cursor] % (uint)(positions - position));
            (order[position], order[swap]) = (order[swap], order[position]);
        }

        var seats = new WarSeat?[Rows];
        for (var position = 0; position < Rows; position++)
        {
            if (order[position] < 0) continue;
            var row = rows[order[position]];
            seats[position] = new WarSeat(position, order[position], row.Kind, row.Race, row.Owner);
        }
        return seats;
    }

    /// <summary>
    /// The scenario as the session leaves it. Seated teams take their row's
    /// race; computers get the Krusty profile and humans none. Human seats get
    /// the lobby's commander rank. Every team outside the session loses its
    /// city slots and plays no profile.
    /// </summary>
    public static ScenarioDefinition Apply(ScenarioDefinition scenario, IReadOnlyList<WarSeat?> seats, int commanderRank)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(seats);
        var applied = scenario;
        foreach (var seat in seats)
            if (seat is { Kind: WarSeatKind.Human })
                applied = applied.WithSelectedWarRoster(seat.TeamId, seat.Race, commanderRank);
        return applied.WithTeams(team =>
        {
            var seat = team.TeamId is >= 0 and < Rows ? seats[team.TeamId] : null;
            if (seat is null)
                return team with
                {
                    AiProfile = 0,
                    CitySlots = [.. team.CitySlots.Select(_ => new ScenarioCitySlot(0, 0))],
                };
            return team with
            {
                Race = seat.Race,
                AiProfile = seat.Kind == WarSeatKind.Human ? 0 : ScenarioSimulation.KrustyAiProfile,
            };
        });
    }
}

/// <summary>
/// The War lobby's options as the session start leaves them in players 1-6
/// stat 0 (<c>0x40183B</c>), read by the vent loader and by the War maps'
/// triggers. The lobby's state at <c>0x48A010 + 0xA670</c> is the same
/// memory as the globals <c>0x494680</c>-<c>0x494694</c>.
/// </summary>
/// <param name="FlowStep">P7 Flow, 1-20 steps of 25% (4 is 100%).</param>
/// <param name="QuantityStep">P7 Quantity, 1-20 steps of 25%.</param>
public sealed record WarSessionOptions(int FlowStep, int QuantityStep, bool EruptingVents, bool RenewableVents, int StorageCells, int Artifacts)
{
    public static WarSessionOptions From(SinglePlayerWarSettings settings) => new(
        settings.P7FlowPercent / 25, settings.P7QuantityPercent / 25, settings.EruptingVents, settings.RenewableVents,
        settings.StorageCells, settings.Artifacts);

    /// <summary>
    /// Players 1-6 stat 0. A War (game type 2, <c>0x4014B9</c>) shifts flow and
    /// quantity left by 6, so 100% is 256, the 8.8 one that the vent loader
    /// multiplies by.
    /// </summary>
    public IReadOnlyList<int> SessionStatistics =>
        [FlowStep << 6, QuantityStep << 6, EruptingVents ? 1 : 0, RenewableVents ? 1 : 0, StorageCells, Artifacts];
}
