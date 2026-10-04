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
    IReadOnlyList<int> AllianceFlags)
{
    /// <summary>
    /// Second <c>%AISlots</c> pair: the team's city origin, which the SCN loader
    /// stores at player <c>+0xBC4/+0xBC8</c> (<c>0x41C07E</c>). Every built slot
    /// footprint around it lies on MAP cells with attribute bit 9 (the
    /// pedestal; asserted by <c>0x444F14</c>).
    /// </summary>
    public CellCoordinate? CityOrigin { get; init; }

    /// <summary>
    /// First <c>%AISlots</c> pair (player <c>+0xBCC/+0xBD0</c>, <c>0x41C04F</c>);
    /// the loader replaces (0,0) with the city origin. Multiplayer maps place
    /// the team's commander here; its gameplay role is not traced yet.
    /// </summary>
    public CellCoordinate? CitySecondaryPoint { get; init; }

    /// <summary>
    /// First <c>%City</c> line: (level, health) for building slots 0-4 (HQ,
    /// barracks, factory, laboratory, research center). Level 0 means the slot
    /// is empty; level n builds variant n - 1; health -1 means the entity's
    /// catalog health.
    /// </summary>
    public IReadOnlyList<ScenarioCitySlot> CitySlots { get; init; } = [];

    /// <summary>The loader builds city slots only when the origin X (+0xBC4) is nonzero.</summary>
    public bool HasCity => CityOrigin is { X: not 0 };
}

public readonly record struct ScenarioCitySlot(int Level, int Health);

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

    /// <summary>
    /// Single Player War leaves every enabled team but the local player to the
    /// computer. Free-War SCNs author all %AI profiles as 0, so mark the other
    /// enabled teams as computer-controlled (profile 1) unless they already
    /// carry a profile.
    /// </summary>
    public ScenarioDefinition WithComputerOpponents(int localTeamId)
    {
        var teams = Teams.Select(team =>
            team.Enabled && team.TeamId != localTeamId && (team.AiProfile ?? 0) <= 0
                ? team with { AiProfile = 1 }
                : team).ToArray();
        return new ScenarioDefinition(Tileset, InternalName, DisplayName, DayNight, teams, Placements, Vents);
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
                ValuesAfter("TeamAllies"))
            {
                CityOrigin = PairAfter("AISlots", 2),
                CitySecondaryPoint = PairAfter("AISlots", 1) is { X: 0, Z: 0 } ? PairAfter("AISlots", 2) : PairAfter("AISlots", 1),
                CitySlots = CitySlotsAfter(),
            });

            // %AISlots and %City own the lines that follow them; the loader
            // reads them positionally with "%d %d" and five "%d %d" pairs.
            CellCoordinate? PairAfter(string name, int lineOffset)
            {
                for (var index = start + 1; index + lineOffset < end; index++)
                {
                    if (!lines[index].Equals($"%{name}", StringComparison.OrdinalIgnoreCase)) continue;
                    var values = Integers(lines[index + lineOffset]);
                    return values.Length >= 2 ? new CellCoordinate(values[0], values[1]) : null;
                }
                return null;
            }

            IReadOnlyList<ScenarioCitySlot> CitySlotsAfter()
            {
                for (var index = start + 1; index + 1 < end; index++)
                {
                    if (!lines[index].Equals("%City", StringComparison.OrdinalIgnoreCase)) continue;
                    var values = Integers(lines[index + 1]);
                    return Enumerable.Range(0, Math.Min(5, values.Length / 2))
                        .Select(slot => new ScenarioCitySlot(values[slot * 2], values[slot * 2 + 1]))
                        .ToArray();
                }
                return [];
            }

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
