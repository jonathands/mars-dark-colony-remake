using DarkColony.Engine.Commands;
using DarkColony.Engine.Simulation;

namespace DarkColony.Engine.Network;

/// <summary>Where two peers' states first differed.</summary>
public sealed record LockstepDesync(ulong Tick, int Player, string LocalDigest, string RemoteDigest);

/// <summary>
/// Deterministic lockstep for one peer. Every peer runs the same
/// simulation, and a tick runs only once every player's turn for it has
/// arrived. A command queued now goes into this peer's turn for
/// <see cref="InputDelay"/> ticks ahead, so peers stay within that many ticks
/// of each other. Each tick's commands are ordered by player, then by queue
/// order, with sequence numbers every peer derives alike. Every
/// <see cref="DigestInterval"/> ticks the peers exchange state digests; the
/// first mismatch stops the session (<see cref="Desync"/>).
/// </summary>
/// <remarks>
/// The original runs network games over DirectPlay (<c>dplay.c</c>,
/// <c>net.c</c>, <c>sync.c</c>). Its packet table has an opcode that runs N
/// world updates, so it is lockstep too. This session is the port's own
/// protocol and does not interoperate with the original.
/// </remarks>
public sealed class LockstepSession
{
    private readonly ILockstepTransport _transport;
    private readonly int[] _players;
    private readonly Dictionary<ulong, LockstepTurn?[]> _turns = [];
    private readonly Dictionary<ulong, string> _localDigests = [];
    private readonly List<LockstepDigest> _pendingDigests = [];
    private readonly List<WorldCommand> _queued = [];
    private ulong _nextTurnToSend = 1;

    public LockstepSession(int localPlayer, int playerCount, ILockstepTransport transport, int inputDelay = 2, int digestInterval = 16)
        : this(localPlayer, Enumerable.Range(0, Math.Max(0, playerCount)).ToArray(), transport, inputDelay, digestInterval)
    {
    }

    /// <param name="players">The player numbers taking part (they need not be contiguous).</param>
    public LockstepSession(int localPlayer, IReadOnlyCollection<int> players, ILockstepTransport transport, int inputDelay = 2, int digestInterval = 16)
    {
        ArgumentNullException.ThrowIfNull(players);
        _players = [.. players.Distinct().Order()];
        if (_players.Length == 0 || Array.IndexOf(_players, localPlayer) < 0)
            throw new ArgumentException("The local player must take part.", nameof(players));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputDelay);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(digestInterval);
        LocalPlayer = localPlayer;
        PlayerCount = _players.Length;
        InputDelay = inputDelay;
        DigestInterval = digestInterval;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public int LocalPlayer { get; }
    public int PlayerCount { get; }
    public int InputDelay { get; }
    public int DigestInterval { get; }
    public LockstepDesync? Desync { get; private set; }

    /// <summary>Digest checks that matched so far.</summary>
    public int ConfirmedDigests { get; private set; }

    /// <summary>Queues a local command for the next turn this peer sends.</summary>
    public void Queue(WorldCommand command) => _queued.Add(command ?? throw new ArgumentNullException(nameof(command)));

    /// <summary>
    /// Runs the next tick if every turn for it is in. It first sends this
    /// peer's turns up to <c>tick + InputDelay</c>. Returns the tick's
    /// commands, or null while waiting (or after a desync).
    /// </summary>
    /// <param name="step">Runs the update (default: <see cref="ScenarioSimulation.Step"/>), so a host can journal it.</param>
    public IReadOnlyList<ScheduledWorldCommand>? TryAdvance(ScenarioSimulation simulation,
        Action<ScenarioSimulation, IReadOnlyList<ScheduledWorldCommand>>? step = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        if (Desync is not null) return null;
        var tick = simulation.TickCount + 1;
        while (_nextTurnToSend <= tick + (ulong)InputDelay - 1)
        {
            // The first turns carry nothing: commands need InputDelay ticks to reach every peer.
            var commands = _nextTurnToSend < (ulong)InputDelay + 1 ? [] : _queued.ToArray();
            if (_nextTurnToSend >= (ulong)InputDelay + 1) _queued.Clear();
            var turn = new LockstepTurn(LocalPlayer, _nextTurnToSend, commands);
            Store(turn);
            _transport.Send(turn);
            _nextTurnToSend++;
        }

        Pump();
        if (Desync is not null || !_turns.TryGetValue(tick, out var turns) || turns.Any(turn => turn is null)) return null;

        _turns.Remove(tick);
        var scheduled = new List<ScheduledWorldCommand>();
        foreach (var turn in turns)
            foreach (var command in turn!.Commands)
                scheduled.Add(new ScheduledWorldCommand(tick, (tick << 16) + (uint)scheduled.Count, command));
        if (step is null) simulation.Step(scheduled);
        else step(simulation, scheduled);

        if (tick % (ulong)DigestInterval == 0)
        {
            var digest = SimulationDigest.Hash(simulation);
            _localDigests[tick] = digest;
            _transport.Send(new LockstepDigest(LocalPlayer, tick, digest));
            Compare();
        }
        return scheduled;
    }

    private void Pump()
    {
        while (_transport.TryReceive(out var message))
        {
            switch (message)
            {
                case LockstepTurn turn when Array.IndexOf(_players, turn.Player) >= 0:
                    Store(turn);
                    break;
                case LockstepDigest digest:
                    _pendingDigests.Add(digest);
                    break;
            }
        }
        Compare();
    }

    private void Store(LockstepTurn turn)
    {
        if (!_turns.TryGetValue(turn.Tick, out var turns)) _turns[turn.Tick] = turns = new LockstepTurn?[PlayerCount];
        turns[Array.IndexOf(_players, turn.Player)] = turn;
    }

    private void Compare()
    {
        for (var index = _pendingDigests.Count - 1; index >= 0; index--)
        {
            var remote = _pendingDigests[index];
            if (!_localDigests.TryGetValue(remote.Tick, out var local)) continue;
            _pendingDigests.RemoveAt(index);
            if (local == remote.Digest) ConfirmedDigests++;
            else Desync ??= new LockstepDesync(remote.Tick, remote.Player, local, remote.Digest);
        }
    }
}
