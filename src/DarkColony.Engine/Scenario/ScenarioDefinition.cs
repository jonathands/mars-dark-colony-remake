using System.Globalization;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Scenario;

public sealed record ScenarioTeam(
    int TeamId,
    bool Enabled,
    int? Race,
    int? StartingResource,
    int? AiProfile,
    int? TeamColor);

public sealed record ScenarioPlacement(
    int X,
    int Z,
    int EntityId,
    int Team,
    int Value,
    int Flag);

public sealed record ScenarioVent(int X, int Z, int EntityId, int Value, int Interval);

public sealed record AutonomousSpawnGroup(
    int GroupId,
    CellCoordinate Origin,
    int EntityId,
    int DesiredPopulation,
    int ScenarioFlag);

public sealed class ScenarioDefinition
{
    private ScenarioDefinition(
        string tileset,
        string internalName,
        string displayName,
        IReadOnlyList<ScenarioTeam> teams,
        IReadOnlyList<ScenarioPlacement> placements,
        IReadOnlyList<ScenarioVent> vents)
    {
        Tileset = tileset;
        InternalName = internalName;
        DisplayName = displayName;
        Teams = teams;
        Placements = placements;
        Vents = vents;
    }

    public string Tileset { get; }
    public string InternalName { get; }
    public string DisplayName { get; }
    public IReadOnlyList<ScenarioTeam> Teams { get; }
    public IReadOnlyList<ScenarioPlacement> Placements { get; }
    public IReadOnlyList<ScenarioVent> Vents { get; }
    public IReadOnlyList<ScenarioPlacement> OrdinaryPlacements => Placements.Where(placement => placement.Team != -1).ToArray();
    public IReadOnlyList<AutonomousSpawnGroup> AutonomousSpawnGroups => Placements
        .Where(placement => placement.Team == -1)
        .Select((placement, index) => new AutonomousSpawnGroup(
            index,
            new CellCoordinate(placement.X, placement.Z),
            placement.EntityId,
            placement.Value,
            placement.Flag))
        .ToArray();

    public static ScenarioDefinition Load(string path) =>
        Parse(File.ReadAllText(path, System.Text.Encoding.Latin1));

    public static ScenarioDefinition Parse(string text)
    {
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        if (lines.Length < 8) throw new InvalidDataException("SCN is shorter than its eight-line header.");

        var teamStarts = Enumerable.Range(0, lines.Length)
            .Where(index => lines[index].StartsWith("TEAM ", StringComparison.Ordinal))
            .ToArray();
        var teams = new List<ScenarioTeam>(teamStarts.Length);
        for (var teamIndex = 0; teamIndex < teamStarts.Length; teamIndex++)
        {
            var start = teamStarts[teamIndex];
            var end = teamIndex + 1 < teamStarts.Length ? teamStarts[teamIndex + 1] : lines.Length;
            var headingWords = lines[start].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (headingWords.Length < 3 ||
                !int.TryParse(headingWords[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var teamId) ||
                !int.TryParse(headingWords[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var enabled))
            {
                throw new InvalidDataException($"Invalid SCN team line: {lines[start]}");
            }

            teams.Add(new ScenarioTeam(
                teamId,
                enabled != 0,
                Field("Race"),
                Field("Money"),
                Field("AI"),
                Field("TeamColour")));

            int? Field(string name)
            {
                for (var index = start + 1; index + 1 < end; index++)
                {
                    if (!lines[index].Equals($"%{name}", StringComparison.OrdinalIgnoreCase)) continue;
                    var values = Integers(lines[index + 1]);
                    return values.Length == 0 ? null : values[0];
                }

                return null;
            }
        }

        var lastCity = Array.FindLastIndex(lines, line => line.Trim().Equals("%City", StringComparison.Ordinal));
        var placementStart = lastCity < 0 ? lines.Length : Math.Min(lines.Length, lastCity + 10);
        var placements = new List<ScenarioPlacement>();
        var vents = new List<ScenarioVent>();
        foreach (var line in lines[placementStart..])
        {
            var values = Integers(line);
            if (values.Length == 6)
            {
                placements.Add(new ScenarioPlacement(values[0], values[1], values[2], values[3], values[4], values[5]));
            }
            else if (values.Length == 5 && values[2] == 40)
            {
                vents.Add(new ScenarioVent(values[0], values[1], values[2], values[3], values[4]));
            }
        }

        return new ScenarioDefinition(
            lines[0].Trim(),
            lines[1].Trim(),
            lines[2].Trim(),
            teams,
            placements,
            vents);
    }

    private static int[] Integers(string line)
    {
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var values = new int[words.Length];
        for (var index = 0; index < words.Length; index++)
        {
            if (!int.TryParse(words[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[index])) return [];
        }

        return values;
    }
}
