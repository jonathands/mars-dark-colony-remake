using DarkColony.Engine.Data;
using DarkColony.Engine.Scenario;

namespace DarkColony.Engine.Network;

/// <summary>A player in a network War: index 0 is the host, the others joined in order.</summary>
public sealed record NetworkMember(int Player, string Nonce, string Name);

/// <summary>Everything a network War lobby shows: the map, the options, the eight rows and who is connected.</summary>
public sealed record NetworkLobbyState(
    string Stem,
    SinglePlayerWarSettings Settings,
    IReadOnlyList<WarLobbyRow> Rows,
    IReadOnlyList<NetworkMember> Members);

/// <summary>A client asks to join; it finds its player index in the next state by its nonce.</summary>
public sealed record LobbyJoin(int Player, string Nonce, string Name, int Race) : LockstepMessage(Player);

/// <summary>A player changes its own row's race.</summary>
public sealed record LobbyRace(int Player, int Race) : LockstepMessage(Player);

/// <summary>The host's current lobby.</summary>
public sealed record LobbyUpdate(int Player, NetworkLobbyState State) : LockstepMessage(Player);

/// <summary>The host starts the game with this lobby; every peer runs the same session start from it.</summary>
public sealed record LobbyStart(int Player, NetworkLobbyState State) : LockstepMessage(Player);

/// <summary>
/// The host's side of a network War lobby. Each joining player takes the
/// first row that no human owns, as a human row of its race; a leaving
/// player's row turns to None. Only the host edits the map, the options and the
/// computer rows. Every change produces the state to broadcast.
/// </summary>
public sealed class NetworkWarLobby
{
    private readonly List<NetworkMember> _members;
    private WarLobbyRow[] _rows;

    public NetworkWarLobby(string stem, string hostName, int hostRace, SinglePlayerWarSettings settings)
    {
        Stem = stem;
        Settings = settings;
        _members = [new NetworkMember(0, string.Empty, hostName)];
        _rows = [.. Enumerable.Range(0, WarSession.Rows).Select(row => new WarLobbyRow(WarSeatKind.None, row % 2))];
        _rows[0] = new WarLobbyRow(WarSeatKind.Human, hostRace, 0);
    }

    public string Stem { get; private set; }
    public SinglePlayerWarSettings Settings { get; private set; }
    public IReadOnlyList<NetworkMember> Members => _members;
    public IReadOnlyList<WarLobbyRow> Rows => _rows;
    public NetworkLobbyState State => new(Stem, Settings, [.. _rows], [.. _members]);

    /// <summary>Seats a joining player, or returns null when every row has a human.</summary>
    public NetworkMember? Join(LobbyJoin join)
    {
        ArgumentNullException.ThrowIfNull(join);
        if (_members.Any(member => member.Nonce == join.Nonce)) return _members.First(member => member.Nonce == join.Nonce);
        var row = Array.FindIndex(_rows, row => row.Kind != WarSeatKind.Human);
        if (row < 0) return null;
        var member = new NetworkMember(_members.Max(existing => existing.Player) + 1, join.Nonce, join.Name);
        _members.Add(member);
        _rows[row] = new WarLobbyRow(WarSeatKind.Human, join.Race is 0 or 1 ? join.Race : 0, member.Player);
        return member;
    }

    /// <summary>A disconnected player's row turns to None.</summary>
    public void Leave(int player)
    {
        if (player == 0) return;
        _members.RemoveAll(member => member.Player == player);
        for (var row = 0; row < _rows.Length; row++)
            if (_rows[row] is { Kind: WarSeatKind.Human } human && human.Owner == player) _rows[row] = human with { Kind = WarSeatKind.None, Owner = 0 };
    }

    public void SetRace(int player, int race)
    {
        for (var row = 0; row < _rows.Length; row++)
            if (_rows[row] is { Kind: WarSeatKind.Human } human && human.Owner == player) _rows[row] = human with { Race = race is 0 or 1 ? race : 0 };
    }

    /// <summary>The host cycles a row that no player owns: Computer, Computer+, None.</summary>
    public void CycleComputerRow(int row)
    {
        if (row is < 0 or >= WarSession.Rows || _rows[row].Kind == WarSeatKind.Human) return;
        _rows[row] = _rows[row] with
        {
            Kind = _rows[row].Kind switch
            {
                WarSeatKind.Computer => WarSeatKind.ComputerPlus,
                WarSeatKind.ComputerPlus => WarSeatKind.None,
                _ => WarSeatKind.Computer,
            },
            Owner = 0,
        };
    }

    public void ToggleComputerRace(int row)
    {
        if (row is < 0 or >= WarSession.Rows || _rows[row].Kind == WarSeatKind.Human) return;
        _rows[row] = _rows[row] with { Race = 1 - _rows[row].Race };
    }

    public void SetMap(string stem) => Stem = stem ?? throw new ArgumentNullException(nameof(stem));

    public void SetSettings(SinglePlayerWarSettings settings) => Settings = settings;

    /// <summary>
    /// The launch a peer plays from a started lobby: the native session start
    /// with its own player's row as the local one.
    /// </summary>
    public static bool TryCreateLaunch(NetworkLobbyState state, int player, SinglePlayerWarCatalog catalog, NativeRandomTable random,
        out SinglePlayerWarLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        launch = default!;
        var scenario = catalog.Scenarios.FirstOrDefault(candidate => string.Equals(candidate.Stem, state.Stem, StringComparison.OrdinalIgnoreCase));
        return scenario is not null && scenario.TryCreateSession(state.Rows, player, state.Settings, random, out launch);
    }
}
