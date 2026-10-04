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
    IReadOnlyList<SavedStep> Steps)
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
    public bool Replay(ScenarioSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        var byTick = Steps.ToDictionary(step => step.Tick, step => step.Commands);
        while (simulation.TickCount < Ticks)
            simulation.Step(byTick.TryGetValue(simulation.TickCount + 1, out var commands) ? commands : []);
        return SimulationDigest.Hash(simulation) == Digest;
    }
}

/// <summary>Records the commands each step of a simulation consumes, for <see cref="SavedGame"/> and replays.</summary>
public sealed class CommandJournal
{
    private readonly List<SavedStep> steps = [];

    public IReadOnlyList<SavedStep> Steps => steps;

    /// <summary>A journal that continues a loaded game's steps, so a later save keeps the whole history.</summary>
    public static CommandJournal Resume(IEnumerable<SavedStep> earlier)
    {
        var journal = new CommandJournal();
        journal.steps.AddRange(earlier);
        return journal;
    }

    /// <summary>Steps the simulation with the commands and records them.</summary>
    public void Step(ScenarioSimulation simulation, IReadOnlyList<ScheduledWorldCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        if (commands.Count != 0) steps.Add(new SavedStep(simulation.TickCount + 1, [.. commands]));
        simulation.Step(commands);
    }

    public SavedGame Save(ScenarioSimulation simulation, string scenarioDirectory, string scenarioName,
        SavedWarLaunch? war = null, SavedCampaign? campaign = null)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        return new SavedGame(SavedGame.CurrentVersion, scenarioDirectory, scenarioName, war, campaign,
            simulation.TickCount, SimulationDigest.Hash(simulation), [.. steps]);
    }
}
