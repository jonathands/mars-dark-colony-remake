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
            .OrderBy(stem => stem, StringComparer.OrdinalIgnoreCase)
            .Select(stem => new SinglePlayerWarScenario(stem!, ScenarioDefinition.Load(Path.Combine(directory, $"{stem}.scn"))))
            .ToArray();
        return new SinglePlayerWarCatalog(scenarios);
    }
}

public sealed record SinglePlayerWarScenario(string Stem, ScenarioDefinition Definition)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Definition.DisplayName) ? Stem : Definition.DisplayName!;

    public ScenarioTeam? EnabledTeamForRace(int race) => Definition.EnabledTeamForRace(race);
}
