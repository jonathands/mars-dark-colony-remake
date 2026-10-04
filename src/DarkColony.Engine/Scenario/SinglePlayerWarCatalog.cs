using DarkColony.Engine.Data;

namespace DarkColony.Engine.Scenario;

/// <summary>
/// Data-derived list of complete original Single Player War scenarios.
/// The native game uses <c>scenario/mplayer</c> as its free-war root; a map
/// becomes selectable only when its SCN, MAP, and PTH companions are present.
/// </summary>
public sealed class SinglePlayerWarCatalog
{
    private SinglePlayerWarCatalog(IReadOnlyList<SinglePlayerWarScenario> scenarios)
    {
        Scenarios = scenarios;
    }

    public IReadOnlyList<SinglePlayerWarScenario> Scenarios { get; }

    public IReadOnlyList<SinglePlayerWarScenario> ForRace(int race) =>
        Scenarios.Where(scenario => scenario.Definition.EnabledTeamForRace(race) is not null).ToArray();

    public static SinglePlayerWarCatalog Load(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var directory = installation.DataFile("scenario", "mplayer");
        if (!Directory.Exists(directory)) return new SinglePlayerWarCatalog([]);

        var scenarios = Directory.EnumerateFiles(directory, "*.scn")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(stem => !string.IsNullOrWhiteSpace(stem))
            .Where(stem =>
                File.Exists(Path.Combine(directory, $"{stem}.map")) &&
                File.Exists(Path.Combine(directory, $"{stem}.pth")))
            .Select(stem => new SinglePlayerWarScenario(stem!, ScenarioDefinition.Load(Path.Combine(directory, $"{stem}.scn"))))
            // The native Single Player War browser presents the user-facing
            // SCN title alphabetically (the captured first entries are
            // 4 Kingdoms, Armageddon, Beon Bay, and Big Crater).
            .OrderBy(scenario => scenario.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(scenario => scenario.Stem, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SinglePlayerWarCatalog(scenarios);
    }
}

public sealed record SinglePlayerWarScenario(string Stem, ScenarioDefinition Definition)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Definition.DisplayName) ? Stem : Definition.DisplayName!;

    public ScenarioTeam? EnabledTeamForRace(int race) => Definition.EnabledTeamForRace(race);

    /// <summary>
    /// Creates the authoritative selection boundary between the native War
    /// lobby and an SCN.  A race is selectable only when that SCN actually
    /// contains an enabled team for it; UI code must not invent a team index.
    /// </summary>
    public bool TryCreateLaunch(int race, out SinglePlayerWarLaunch launch) =>
        TryCreateLaunch(race, SinglePlayerWarSettings.Default, out launch);

    public bool TryCreateLaunch(int race, SinglePlayerWarSettings settings, out SinglePlayerWarLaunch launch)
    {
        if (!settings.IsValid)
        {
            launch = default!;
            return false;
        }
        var team = EnabledTeamForRace(race);
        if (team is null)
        {
            launch = default!;
            return false;
        }

        launch = new SinglePlayerWarLaunch(Stem, DisplayName, race, team.TeamId, settings);
        return true;
    }

    /// <summary>
    /// The native session start for these lobby rows (<see cref="WarSession"/>).
    /// The local player is the human row <paramref name="localOwner"/> owns. It
    /// fails when settings are invalid, that row got no team, or the team is not
    /// enabled in the SCN.
    /// </summary>
    public bool TryCreateSession(IReadOnlyList<WarLobbyRow> rows, int localOwner, SinglePlayerWarSettings settings,
        NativeRandomTable random, out SinglePlayerWarLaunch launch)
    {
        launch = default!;
        if (!settings.IsValid) return false;
        var seats = WarSession.Assign(Stem, rows, random);
        var local = seats.FirstOrDefault(seat => seat is { Kind: WarSeatKind.Human } && seat.Owner == localOwner);
        if (local is null || !seats.All(seat => seat is null || Definition.Teams.Any(team => team.TeamId == seat.TeamId && team.Enabled))) return false;
        launch = new SinglePlayerWarLaunch(Stem, DisplayName, local.Race, local.TeamId, settings) { Rows = [.. rows], Seats = seats };
        return true;
    }
}

/// <summary>
/// Validated local-player identity for a free War scenario.  This is data
/// selection, not simulation configuration: economy and power-up application
/// remain separate until their native launch handoff is recovered.
/// </summary>
public sealed record SinglePlayerWarLaunch(
    string Stem,
    string DisplayName,
    int Race,
    int LocalTeamId,
    SinglePlayerWarSettings Settings)
{
    /// <summary>The lobby rows of a session launch (null for a launch made from a team choice).</summary>
    public IReadOnlyList<WarLobbyRow>? Rows { get; init; }

    /// <summary>The team each row got at the session start, indexed by team.</summary>
    public IReadOnlyList<WarSeat?>? Seats { get; init; }

    /// <summary>
    /// The scenario as this launch plays it: the native session's seats when
    /// there are any, else the commander roster for the local team with every
    /// other enabled team on the computer.
    /// </summary>
    public ScenarioDefinition ApplyTo(ScenarioDefinition scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (Seats is { } seats) return WarSession.Apply(scenario, seats, Settings.CommanderRank);
        return scenario.WithSelectedWarRoster(LocalTeamId, Race, Settings.CommanderRank).WithComputerOpponents(LocalTeamId);
    }
}

/// <summary>
/// Native Single Player War lobby settings, retained with the launch selection.
/// Values are UI-confirmed; their gameplay application is intentionally not
/// inferred until the executable's scenario-start handoff is decoded.
/// </summary>
public readonly record struct SinglePlayerWarSettings(
    int StorageCells,
    int Artifacts,
    bool EruptingVents,
    bool RenewableVents,
    int P7QuantityPercent,
    int P7FlowPercent,
    int CommanderRank)
{
    public static SinglePlayerWarSettings Default => new(0, 0, false, false, 100, 100, 0);

    public bool IsValid =>
        StorageCells is >= 0 and <= 3 &&
        Artifacts is >= 0 and <= 3 &&
        P7QuantityPercent is >= 25 and <= 500 && P7QuantityPercent % 25 == 0 &&
        P7FlowPercent is >= 25 and <= 500 && P7FlowPercent % 25 == 0 &&
        CommanderRank is >= 0 and <= 3;
}
