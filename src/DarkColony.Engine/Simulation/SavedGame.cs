using System.Text.Json;
using System.Text.Json.Serialization;
using DarkColony.Engine.Commands;

namespace DarkColony.Engine.Simulation;

/// <summary>The commands one simulation step consumed (only steps that had any are kept).</summary>
public sealed record SavedStep(ulong Tick, IReadOnlyList<ScheduledWorldCommand> Commands);

/// <summary>The Single Player War launch a saved game was started with.</summary>
public sealed record SavedWarLaunch(int Race, int LocalTeamId, int StorageCells, int Artifacts, bool EruptingVents,
    bool RenewableVents, int P7QuantityPercent, int P7FlowPercent, int CommanderRank);

/// <summary>Where a saved campaign stands: race, training, and mission number.</summary>
public sealed record SavedCampaign(bool Gray, bool Training, int Mission);

/// <summary>The state digest right after a step, recorded along the way so a replay can point at the first step that differs.</summary>
public sealed record SavedCheckpoint(ulong Tick, string Digest);

/// <summary>A replay's outcome: whether every checkpoint and the end matched, and the first tick that did not.</summary>
public sealed record ReplayCheck(bool Matches, ulong? FirstMismatchTick, int CheckpointsVerified);

/// <summary>
/// A game as the port saves it: the scenario, how it was launched, and every
/// command the simulation consumed, by step. Loading recreates the scenario
/// and replays the steps; the simulation is deterministic, so the replayed
/// state's digest equals <see cref="Digest"/>. The original's own save files
/// (loader <c>0x41A978</c>) hold raw runtime memory and are not read.
/// </summary>
public sealed record SavedGame(
    int Version,
    string ScenarioDirectory,
    string ScenarioName,
    SavedWarLaunch? War,
    SavedCampaign? Campaign,
    ulong Ticks,
    string Digest,
    IReadOnlyList<SavedStep> Steps,
    IReadOnlyList<SavedCheckpoint>? Checkpoints = null)
{
    public const int CurrentVersion = 1;
    public const string FileExtension = ".dcsave";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static SavedGame FromJson(string json)
    {
        var game = JsonSerializer.Deserialize<SavedGame>(json, Options) ?? throw new InvalidDataException("Saved game is empty.");
        if (game.Version != CurrentVersion) throw new InvalidDataException($"Saved game version {game.Version} is not {CurrentVersion}.");
        return game;
    }

    /// <summary>
    /// Replays the saved steps on a simulation freshly created from the same
    /// scenario, up to <see cref="Ticks"/>. Returns whether the state's digest
    /// matches the one saved.
    /// </summary>
    public bool Replay(ScenarioSimulation simulation) => VerifyReplay(simulation).Matches;

    /// <summary>
    /// Replays like <see cref="Replay"/>, comparing the state with every
    /// checkpoint on the way and with <see cref="Digest"/> at the end.
    /// </summary>
    public ReplayCheck VerifyReplay(ScenarioSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        var player = new ReplayPlayer(this);
        while (simulation.TickCount < Ticks) player.Step(simulation);
        return player.Result(simulation);
    }

    /// <summary>The commands recorded for <paramref name="tick"/>.</summary>
    public IReadOnlyList<ScheduledWorldCommand> CommandsFor(ulong tick) =>
        Steps.FirstOrDefault(step => step.Tick == tick)?.Commands ?? [];
}

/// <summary>Plays a saved game's steps one at a time, checking its checkpoints as they pass.</summary>
public sealed class ReplayPlayer
{
    private readonly SavedGame _game;
    private readonly Dictionary<ulong, IReadOnlyList<ScheduledWorldCommand>> _steps;
    private readonly Dictionary<ulong, string> _checkpoints;
    private ulong? _firstMismatch;
    private int _verified;

    public ReplayPlayer(SavedGame game)
    {
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _steps = game.Steps.ToDictionary(step => step.Tick, step => step.Commands);
        _checkpoints = (game.Checkpoints ?? []).ToDictionary(checkpoint => checkpoint.Tick, checkpoint => checkpoint.Digest);
    }

    public bool Finished(ScenarioSimulation simulation) => simulation.TickCount >= _game.Ticks;

    /// <summary>Runs the next recorded step and checks a checkpoint on it.</summary>
    public void Step(ScenarioSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        var tick = simulation.TickCount + 1;
        simulation.Step(_steps.TryGetValue(tick, out var commands) ? commands : []);
        if (!_checkpoints.TryGetValue(tick, out var expected)) return;
        if (SimulationDigest.Hash(simulation) == expected) _verified++;
        else _firstMismatch ??= tick;
    }

    public ReplayCheck Result(ScenarioSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        var end = simulation.TickCount == _game.Ticks && SimulationDigest.Hash(simulation) == _game.Digest;
        return new ReplayCheck(end && _firstMismatch is null, _firstMismatch ?? (end ? null : simulation.TickCount), _verified);
    }
}

/// <summary>Records the commands each step of a simulation consumes, for <see cref="SavedGame"/> and replays.</summary>
/// <param name="checkpointInterval">Record a digest after every step this many ticks apart (0: none).</param>
public sealed class CommandJournal(int checkpointInterval = 0)
{
    private readonly List<SavedStep> steps = [];
    private readonly List<SavedCheckpoint> checkpoints = [];

    public IReadOnlyList<SavedStep> Steps => steps;
    public IReadOnlyList<SavedCheckpoint> Checkpoints => checkpoints;

    /// <summary>A journal that continues a loaded game's steps, so a later save keeps the whole history.</summary>
    public static CommandJournal Resume(IEnumerable<SavedStep> earlier, IEnumerable<SavedCheckpoint>? earlierCheckpoints = null, int checkpointInterval = 0)
    {
        var journal = new CommandJournal(checkpointInterval);
        journal.steps.AddRange(earlier);
        if (earlierCheckpoints is not null) journal.checkpoints.AddRange(earlierCheckpoints);
        return journal;
    }

    /// <summary>Steps the simulation with the commands and records them.</summary>
    public void Step(ScenarioSimulation simulation, IReadOnlyList<ScheduledWorldCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        if (commands.Count != 0) steps.Add(new SavedStep(simulation.TickCount + 1, [.. commands]));
        simulation.Step(commands);
        if (checkpointInterval > 0 && simulation.TickCount % (ulong)checkpointInterval == 0)
            checkpoints.Add(new SavedCheckpoint(simulation.TickCount, SimulationDigest.Hash(simulation)));
    }

    public SavedGame Save(ScenarioSimulation simulation, string scenarioDirectory, string scenarioName,
        SavedWarLaunch? war = null, SavedCampaign? campaign = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        return new SavedGame(SavedGame.CurrentVersion, scenarioDirectory, scenarioName, war, campaign,
            simulation.TickCount, SimulationDigest.Hash(simulation), [.. steps], checkpoints.Count == 0 ? null : [.. checkpoints]);
    }
}
