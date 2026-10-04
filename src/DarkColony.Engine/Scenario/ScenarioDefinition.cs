using System.Globalization;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Scenario;

public sealed record ScenarioTeam(
    int TeamId,
    bool Enabled,
    int? Race,
    int? StartingResource,
    int? AiProfile,
    int? TeamColor,
    IReadOnlyList<int> StartingDependencyFlags,
    IReadOnlyList<int> AllianceFlags);

public sealed record ScenarioPlacement(
    int X,
    int Z,
    int EntityId,
    int Team,
    int Value,
    int Flag);

/// <summary>
/// Native entity-40 records contain <c>x z entity initial-state reservoir</c>.
/// The fifth value is passed to the actor constructor as runtime <c>+0x0c</c>,
/// which the deployed-harvester pulse consumes as its source reservoir.
/// </summary>
public sealed record ScenarioVent(int X, int Z, int EntityId, int InitialState, int InitialReservoir);

/// <summary>
/// The four scalar lines after the SCN's map descriptor. dc.exe loads these
/// into world offsets +0x53c, +0x534, +0x530, and +0x538 respectively.
/// </summary>
public sealed record ScenarioDayNight(int InitialPhase, int CycleTickLimit, int InitialTick, int TransitionTickLimit)
{
    public bool IsNativeValid => CycleTickLimit >= 0 && InitialTick >= 0 &&
        InitialTick <= CycleTickLimit && TransitionTickLimit > 0;
}

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
        ScenarioDayNight dayNight,
        IReadOnlyList<ScenarioTeam> teams,
        IReadOnlyList<ScenarioPlacement> placements,
        IReadOnlyList<ScenarioVent> vents)
    {
        Tileset = tileset;
        InternalName = internalName;
        DisplayName = displayName;
        DayNight = dayNight;
        Teams = teams;
        Placements = placements;
        Vents = vents;
    }

    public string Tileset { get; }
    public string InternalName { get; }
    public string DisplayName { get; }
    public ScenarioDayNight DayNight { get; }
    public IReadOnlyList<ScenarioTeam> Teams { get; }
    public ScenarioTeam? EnabledTeamForRace(int race) => Teams
        .Where(team => team.Enabled && team.Race == race)
        .OrderBy(team => team.TeamId)
        .FirstOrDefault();
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

    /// <summary>
    /// Applies the recovered free-War roster handoff for one locally selected
    /// team. Multiplayer SCNs seed Human starter slots even for a Gray team;
    /// the native launch state selects faction-specific units before
    /// simulation begins.
    /// </summary>
    public ScenarioDefinition WithSelectedWarRoster(int teamId, int faction, int rank)
    {
        if (faction is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(faction));
        if (rank is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(rank));

        var placements = Placements.Select(placement =>
            placement.Team == teamId
                ? placement with { EntityId = SelectedWarRosterEntity(placement.EntityId, faction, rank) }
                : placement).ToArray();
        return new ScenarioDefinition(Tileset, InternalName, DisplayName, DayNight, Teams, placements, Vents);
    }

    private static int SelectedWarRosterEntity(int entityId, int faction, int rank) => entityId switch
    {
        // The paired basic ground units used by free-War SCN starter slots.
        0 or 8 => faction == 0 ? 0 : 8,
        2 or 10 => faction == 0 ? 2 : 10,
        // Commander ranks use their own contiguous Human/Gray ranges.
        >= 69 and <= 76 => (faction == 0 ? 69 : 73) + rank,
        _ => entityId,
    };

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

            // SCN team blocks use postfix labels: the value immediately before
            // %Race belongs to Race, and so on. Treating the following value as
            // the field makes a 1,500 starting resource look like a race.
            teams.Add(new ScenarioTeam(
                teamId,
                enabled != 0,
                ValueBefore("Race"),
                ValueBefore("Money"),
                ValueBefore("AI"),
                ValueBefore("TeamColour"),
                ValuesAfter("Depend"),
                ValuesAfter("TeamAllies")));

            int? ValueBefore(string name)
            {
                for (var index = start + 2; index < end; index++)
                {
                    if (!lines[index].Equals($"%{name}", StringComparison.OrdinalIgnoreCase)) continue;
                    var values = Integers(lines[index - 1]);
                    return values.Length == 0 ? null : values[0];
                }

                return null;
            }

            // Unlike the scalar postfix fields above, %Depend and
            // %TeamAllies own following sentinel-terminated rows. The native
            // availability checker stores state per dependency record, and its
            // 10x10 hostile matrix has not yet been bridged to the 15-value
            // alliance source row. Preserve both inputs instead of conflating
            // either with completed tech or runtime team relations.
            IReadOnlyList<int> ValuesAfter(string name)
            {
                for (var index = start + 1; index < end - 1; index++)
                {
                    if (!lines[index].Equals($"%{name}", StringComparison.OrdinalIgnoreCase)) continue;
                    var values = Integers(lines[index + 1]);
                    var sentinel = Array.IndexOf(values, -1);
                    return sentinel >= 0 ? values[..sentinel] : values;
                }

                return [];
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

        var dayNightValues = lines[4..8].Select(Integers).SelectMany(values => values).ToArray();
        if (dayNightValues.Length != 4)
            throw new InvalidDataException("SCN day/night header must contain four integer values.");
        return new ScenarioDefinition(
            lines[0].Trim(),
            lines[1].Trim(),
            lines[2].Trim(),
            new ScenarioDayNight(dayNightValues[0], dayNightValues[1], dayNightValues[2], dayNightValues[3]),
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
