namespace DarkColony.Engine.Data;

/// <summary>
/// One mission record of a campaign scene list. The two videos are what the
/// end of the mission plays (<c>0x403B48</c>): the first after a victory, the
/// second after a defeat. <see cref="Remaining"/> keeps the three lines not
/// decoded yet: three numbers, a flag, and a list ended by -1.
/// </summary>
public sealed record SceneMission(
    string Scenario,
    string Title,
    string Location,
    string ScenarioPath,
    string VictoryVideo,
    string DefeatVideo,
    IReadOnlyList<string> Remaining);

/// <summary>
/// <c>gamestat/hscene.txt</c>, <c>gscene.txt</c>, <c>htscene.txt</c> and
/// <c>gtscene.txt</c>, which <c>0x403B06</c> picks by race and training. Each
/// holds eight name lines, a mission count, then nine lines per mission.
/// </summary>
public sealed class SceneList
{
    private const int HeaderLines = 8;
    private const int RecordLines = 9;

    private SceneList(IReadOnlyList<string> names, IReadOnlyList<SceneMission> missions)
    {
        Names = names;
        Missions = missions;
    }

    public IReadOnlyList<string> Names { get; }
    public IReadOnlyList<SceneMission> Missions { get; }

    /// <summary>The list <c>0x403B06</c> reads for a race and mode.</summary>
    public static string FileName(bool gray, bool training) => (gray, training) switch
    {
        (false, false) => "hscene.txt",
        (true, false) => "gscene.txt",
        (false, true) => "htscene.txt",
        (true, true) => "gtscene.txt",
    };

    public static SceneList Load(string path) => Parse(File.ReadAllText(path));

    public static SceneList Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        if (lines.Length <= HeaderLines) throw new InvalidDataException("Scene list is shorter than its name header.");
        var names = lines.Take(HeaderLines).Select(line => line.TrimEnd().TrimEnd('.').TrimEnd()).ToArray();
        var body = lines.Skip(HeaderLines).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (body.Length == 0 || !int.TryParse(body[0], out var count) || count < 0)
            throw new InvalidDataException("Scene list has no mission count.");
        if (body.Length < 1 + count * RecordLines)
            throw new InvalidDataException($"Scene list declares {count} missions but holds {(body.Length - 1) / RecordLines}.");
        var missions = new SceneMission[count];
        for (var index = 0; index < count; index++)
        {
            var record = body.AsSpan(1 + index * RecordLines, RecordLines);
            missions[index] = new SceneMission(record[0], record[1], record[2], record[3], record[4], record[5], record[6..].ToArray());
        }
        return new SceneList(names, missions);
    }

    /// <summary>The record whose scenario is <paramref name="scenario"/> (like <c>human/human01.scn</c>).</summary>
    public SceneMission? Find(string scenario) =>
        Missions.FirstOrDefault(mission => string.Equals(mission.Scenario.Replace('\\', '/'), scenario.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
}
