using System.Net;
using System.Net.Sockets;
using DarkColony.App.Diagnostics;
using DarkColony.App.Ui;
using DarkColony.Engine.Network;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Simulation;

namespace DarkColony.App;

/// <summary>
/// Multi Player War over TCP/IP. ACT AS SERVER hosts a War lobby (the
/// <c>multie</c> screen) and CONNECT TO SERVER joins one (<c>getsvre</c>).
/// When the host starts, every peer runs the same native session start
/// (<see cref="WarSession"/>) from the shared lobby and plays it in lockstep
/// (<see cref="LockstepSession"/>). The original used DirectPlay; this
/// protocol is the port's own (docs/NETWORK_AND_REPLAY.md).
/// </summary>
public sealed partial class MainForm
{
    /// <summary>DirectPlay's TCP/IP port, the default for hosting and joining (<c>--net-port</c> overrides).</summary>
    public const int DefaultNetworkPort = 47624;

    private TcpLockstepTransport? _netTransport;
    private NetworkWarLobby? _netHostLobby;
    private NetworkLobbyState? _netLobbyState;
    private int _netPlayer = -1;
    private string _netNonce = string.Empty;
    private LockstepSession? _lockstep;
    private ulong _lockstepWorldStart;
    private bool _netStopped;
    private string _netAddress = "127.0.0.1";

    public int NetworkPort { get; init; } = DefaultNetworkPort;

    private bool InNetworkLobby => _netTransport is not null && _lockstep is null;
    private bool IsNetworkHost => _netHostLobby is not null;
    private bool IsNetworkGame => _lockstep is not null;

    private string NetworkName => string.IsNullOrWhiteSpace(_leaderName) ? "Player" : _leaderName;

    private void HostNetworkGame()
    {
        LeaveNetwork(quiet: true);
        EnsureSinglePlayerMaps();
        if (_singlePlayerMaps.Count == 0)
        {
            _status = "No complete War maps were found in scenario\\mplayer.";
            return;
        }
        try
        {
            var listener = new TcpListener(IPAddress.Any, NetworkPort);
            listener.Start();
            _netTransport = TcpLockstepTransport.Listen(listener);
        }
        catch (SocketException error)
        {
            _status = $"Cannot host on port {NetworkPort}: {error.Message}";
            return;
        }
        _netPlayer = 0;
        _netHostLobby = new NetworkWarLobby(_singlePlayerMaps[_singlePlayerMapIndex].Stem, NetworkName, _grayRace ? 1 : 0, CurrentWarSettings());
        _netLobbyState = _netHostLobby.State;
        ApplyLobbyStateToUi(_netLobbyState);
        _status = $"Hosting on port {NetworkPort}: waiting for players.";
        RuntimeLog.Info(_status);
        ShowScreen(MenuScreenId.SinglePlayer);
    }

    private void ConnectToNetworkGame()
    {
        LeaveNetwork(quiet: true);
        var text = _netAddress.Trim();
        var port = NetworkPort;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var typedPort))
        {
            port = typedPort;
            text = text[..colon];
        }
        try
        {
            var address = IPAddress.TryParse(text, out var parsed)
                ? parsed
                : Dns.GetHostAddresses(text).First(candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
            _netTransport = TcpLockstepTransport.Join(new IPEndPoint(address, port), TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is SocketException or TimeoutException or AggregateException or InvalidOperationException or ArgumentException)
        {
            _status = $"Cannot connect to {text}:{port}: {error.GetBaseException().Message}";
            return;
        }
        _netNonce = Guid.NewGuid().ToString("N");
        _netTransport.Send(new LobbyJoin(-1, _netNonce, NetworkName, _grayRace ? 1 : 0));
        _status = $"Connected to {text}:{port}; joining the lobby.";
        RuntimeLog.Info(_status);
        ShowScreen(MenuScreenId.SinglePlayer);
    }

    /// <summary>Closes any network session (lobby or game).</summary>
    private void LeaveNetwork(bool quiet = false)
    {
        if (_netTransport is null && _lockstep is null) return;
        if (_lockstep is { } session && _scenarioSimulation is { } simulation)
            RuntimeLog.Info($"Network game closed at update {simulation.TickCount}: {session.ConfirmedDigests} digests confirmed{(session.Desync is null ? string.Empty : ", desync")}.");
        _netTransport?.Dispose();
        _netTransport = null;
        _netHostLobby = null;
        _netLobbyState = null;
        _lockstep = null;
        _netPlayer = -1;
        _netStopped = false;
        if (!quiet) RuntimeLog.Info("Network session closed.");
    }

    private SinglePlayerWarSettings CurrentWarSettings() => new(
        _warStorageCells, _warArtifacts, _warEruptingVents, _warRenewableVents,
        _warP7QuantityMultiplier, _warP7FlowMultiplier, _warCommanderRank);

    /// <summary>Runs once per frame while a network lobby is open.</summary>
    private void PumpNetworkLobby()
    {
        if (_netTransport is not { } transport || _lockstep is not null) return;
        if (_netHostLobby is { } lobby)
        {
            var changed = false;
            while (transport.TryReceive(out var message))
            {
                switch (message)
                {
                    case LobbyJoin join:
                        if (lobby.Join(join) is { } member)
                        {
                            _status = $"{member.Name} joined as player {member.Player + 1}.";
                            RuntimeLog.Info(_status);
                        }
                        changed = true;
                        break;
                    case LobbyRace race:
                        lobby.SetRace(race.Player, race.Race);
                        changed = true;
                        break;
                }
            }
            while (transport.TryTakeLostPlayer(out var lost))
            {
                if (lost <= 0) continue;
                lobby.Leave(lost);
                _status = $"Player {lost + 1} left.";
                changed = true;
            }
            // The host's map list and option controls are the lobby's.
            EnsureSinglePlayerMaps();
            var stem = _singlePlayerMaps[Math.Clamp(_singlePlayerMapIndex, 0, _singlePlayerMaps.Count - 1)].Stem;
            if (stem != lobby.Stem || CurrentWarSettings() != lobby.Settings)
            {
                lobby.SetMap(stem);
                lobby.SetSettings(CurrentWarSettings());
                changed = true;
            }
            if (changed) PublishHostLobby();
            return;
        }

        while (transport.TryReceive(out var message))
        {
            switch (message)
            {
                case LobbyUpdate update:
                    AcceptLobbyState(update.State);
                    break;
                case LobbyStart start:
                    AcceptLobbyState(start.State);
                    StartNetworkWar(start.State);
                    return;
            }
        }
        if (transport.TryTakeLostPlayer(out _))
        {
            LeaveNetwork();
            _status = "Connection lost: the host left.";
            ShowScreen(MenuScreenId.NetworkOptions);
        }
    }

    private void PublishHostLobby()
    {
        if (_netHostLobby is not { } lobby || _netTransport is null) return;
        _netLobbyState = lobby.State;
        ApplyLobbyStateToUi(_netLobbyState);
        _netTransport.Send(new LobbyUpdate(0, _netLobbyState));
    }

    private void AcceptLobbyState(NetworkLobbyState state)
    {
        _netLobbyState = state;
        if (_netPlayer < 0 && state.Members.FirstOrDefault(member => member.Nonce == _netNonce) is { } self)
        {
            _netPlayer = self.Player;
            // The host learns which connection carries this player from its first message.
            _netTransport?.Send(new LobbyRace(_netPlayer, _grayRace ? 1 : 0));
            _status = $"Joined as player {_netPlayer + 1}; waiting for the host.";
        }
        ApplyLobbyStateToUi(state);
    }

    /// <summary>Shows a lobby state through the War lobby's own fields.</summary>
    private void ApplyLobbyStateToUi(NetworkLobbyState state)
    {
        EnsureSinglePlayerMaps();
        var index = _singlePlayerMaps.ToList().FindIndex(map => string.Equals(map.Stem, state.Stem, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _singlePlayerMapIndex = index;
        (_warStorageCells, _warArtifacts, _warEruptingVents, _warRenewableVents, _warP7QuantityMultiplier, _warP7FlowMultiplier, _warCommanderRank) =
            (state.Settings.StorageCells, state.Settings.Artifacts, state.Settings.EruptingVents, state.Settings.RenewableVents,
                state.Settings.P7QuantityPercent, state.Settings.P7FlowPercent, state.Settings.CommanderRank);
        for (var row = 0; row < Math.Min(_warLobbyPlayers.Length, state.Rows.Count); row++)
        {
            var source = state.Rows[row];
            var player = _warLobbyPlayers[row];
            player.Type = source.Kind switch
            {
                WarSeatKind.Computer => WarLobbyPlayerType.Ai,
                WarSeatKind.ComputerPlus => WarLobbyPlayerType.AiPlus,
                WarSeatKind.Human => WarLobbyPlayerType.Human,
                _ => WarLobbyPlayerType.None,
            };
            player.Gray = source.Race == 1;
            player.Name = source.Kind == WarSeatKind.Human
                ? state.Members.FirstOrDefault(member => member.Player == source.Owner)?.Name ?? string.Empty
                : string.Empty;
        }
    }

    /// <summary>
    /// Row clicks in a network lobby. Each player toggles its own race. The
    /// host also cycles computer rows (Computer, Computer+, None) and their
    /// races. The map and options stay with the host.
    /// </summary>
    private bool HandleNetworkLobbyClick(Point point)
    {
        if (_netLobbyState is not { } state) return true;
        for (var row = 0; row < Math.Min(8, state.Rows.Count); row++)
        {
            var y = 21 + row * 19;
            var source = state.Rows[row];
            var own = source.Kind == WarSeatKind.Human && source.Owner == _netPlayer;
            if (new Rectangle(141, y, 89, 22).Contains(point))
            {
                if (own)
                {
                    _grayRace = source.Race == 0;
                    if (_netHostLobby is { } hostLobby)
                    {
                        hostLobby.SetRace(0, _grayRace ? 1 : 0);
                        PublishHostLobby();
                    }
                    else _netTransport?.Send(new LobbyRace(_netPlayer, _grayRace ? 1 : 0));
                }
                else if (_netHostLobby is { } hostLobby && source.Kind != WarSeatKind.Human)
                {
                    hostLobby.ToggleComputerRace(row);
                    PublishHostLobby();
                }
                return true;
            }
            if (new Rectangle(99, y, 36, 16).Contains(point))
            {
                if (_netHostLobby is { } hostLobby && source.Kind != WarSeatKind.Human)
                {
                    hostLobby.CycleComputerRow(row);
                    PublishHostLobby();
                }
                return true;
            }
        }
        // Below the rows: the host's map list and options; nothing for a client.
        return !IsNetworkHost || point.Y < 21 + 8 * 19;
    }

    private void PressNetworkReady()
    {
        if (_netHostLobby is not { } lobby || _netTransport is null)
        {
            _status = "Waiting for the host to start.";
            return;
        }
        if (lobby.Members.Count < 2)
        {
            _status = "Waiting for another player to connect.";
            return;
        }
        _netTransport.StopAccepting();
        var state = lobby.State;
        _netTransport.Send(new LobbyStart(0, state));
        StartNetworkWar(state);
    }

    private void StartNetworkWar(NetworkLobbyState state)
    {
        if (_netTransport is null || _simulationRules is null && _installation is null) return;
        EnsureSinglePlayerMaps();
        var rules = _simulationRules ??= SimulationRules.Load(_installation!);
        var scenario = _singlePlayerMaps.FirstOrDefault(map => string.Equals(map.Stem, state.Stem, StringComparison.OrdinalIgnoreCase));
        if (scenario is null || !scenario.TryCreateSession(state.Rows, _netPlayer, state.Settings, rules.RandomTable, out var launch))
        {
            _status = $"Cannot start {state.Stem}: this player has no team in the session.";
            RuntimeLog.Info(_status);
            LeaveNetwork();
            ShowScreen(MenuScreenId.NetworkOptions);
            return;
        }
        _lockstep = new LockstepSession(_netPlayer, [.. state.Members.Select(member => member.Player)], _netTransport);
        _netStopped = false;
        _selectedScenario = new ScenarioChoice("mplayer", launch.Stem, WarLaunch: launch);
        _localPlayerTeam = launch.LocalTeamId;
        _grayRace = launch.Race == 1;
        _status = $"Multi Player War: {launch.Stem.ToUpperInvariant()} as team {_localPlayerTeam + 1}, {state.Members.Count} players.";
        RuntimeLog.Info($"{_status} Seats: {string.Join(", ", launch.Seats!.Where(seat => seat is not null).Select(seat => $"team {seat!.TeamId + 1} {seat.Kind}{(seat.Kind == WarSeatKind.Human ? $" p{seat.Owner + 1}" : string.Empty)}"))}.");
        ShowScreen(MenuScreenId.Gameplay);
        _lockstepWorldStart = _world.TickCount;
    }

    /// <summary>
    /// One clock step of a network game. The player's orders go into this
    /// peer's next turn. The simulation then runs every update that is both
    /// due by the clock and complete (all turns in), at most 8 at once to catch up.
    /// </summary>
    private void StepNetworkGame()
    {
        if (_lockstep is not { } session || _scenarioSimulation is not { } simulation || _netStopped) return;
        CapturePreviousActorRenderPositions();
        _world.Step();
        foreach (var scheduled in _world.LastCommands) session.Queue(scheduled.Command);
        var due = _world.TickCount - _lockstepWorldStart;
        for (var catchUp = 0; catchUp < 8 && simulation.TickCount < due; catchUp++)
        {
            if (session.TryAdvance(simulation, (current, commands) => _journal.Step(current, commands)) is null) break;
            CaptureSimulationFeedback();
            if (simulation.TickCount % 300 == 0)
                RuntimeLog.Info($"Network: update {simulation.TickCount}, {session.ConfirmedDigests} digests confirmed, digest {SimulationDigest.Hash(simulation)[..16]}.");
        }
        if (session.Desync is { } desync)
        {
            _netStopped = true;
            _status = $"Desync at update {desync.Tick} with player {desync.Player + 1}; the game stops.";
            RuntimeLog.Info(_status);
        }
        else if (_netTransport is { } transport && transport.TryTakeLostPlayer(out var lost))
        {
            _netStopped = true;
            _status = lost > 0 ? $"Connection lost: player {lost + 1} left. Press Esc for the menu." : "Connection lost. Press Esc for the menu.";
            RuntimeLog.Info(_status);
        }
    }

    private void DrawNetworkConnect(Graphics graphics)
    {
        // getsvre: the "IP ADDRESS" label (in_text 5, right-aligned in 15 cells
        // at 24,71) and the address field (in_text 3, 35 cells at 174,62).
        var font = LoadMenuFont();
        var cell = font.Sprite.Frames[0].Width + 1;
        const string label = "IP ADDRESS";
        DrawCellText(graphics, label, 24 + (15 - label.Length) * cell, 71, font, colour: 4, palette: "server");
        var shown = _netAddress.Length > 34 ? _netAddress[^34..] : _netAddress;
        DrawCellText(graphics, shown + "_", 174, 62, font, colour: 4, palette: "server");
    }

    /// <summary>Typing into the address field.</summary>
    private bool HandleNetworkConnectKey(char character)
    {
        if (_screen != MenuScreenId.NetworkConnect) return false;
        if (character == '\b')
        {
            if (_netAddress.Length > 0) _netAddress = _netAddress[..^1];
        }
        else if (character == '\r') ConnectToNetworkGame();
        else if ((char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '-') && _netAddress.Length < 64) _netAddress += character;
        else return false;
        _surface.Invalidate();
        return true;
    }
}
