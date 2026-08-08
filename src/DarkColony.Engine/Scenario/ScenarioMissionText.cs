using System.Text;
using System.Text.RegularExpressions;

namespace DarkColony.Engine.Scenario;

/// <summary>
/// Text assets paired with one campaign scenario. Markup colour codes are
/// removed for the engine-facing text; presentation may later retain a styled
/// token stream when the original dialogue renderer is decoded.
/// </summary>
public sealed partial record ScenarioMissionText(
    string Briefing,
    IReadOnlyDictionary<int, string> Messages,
    IReadOnlyDictionary<string, string> Outcomes)
{
    public static ScenarioMissionText LoadForScenario(string scenarioPath)
    {
        var briefingPath = Path.ChangeExtension(scenarioPath, ".txt");
        var messagePath = Path.ChangeExtension(scenarioPath, ".msg");
        var directory = Path.GetDirectoryName(scenarioPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(scenarioPath);
        var outcomes = Directory.Exists(directory)
            ? Directory.GetFiles(directory, $"{stem}.???")
                .Where(path => Regex.IsMatch(Path.GetExtension(path), "^\\.\\d{3}$", RegexOptions.CultureInvariant))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(path => Path.GetExtension(path)[1..], path => Clean(File.ReadAllText(path, Encoding.Latin1)), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return new ScenarioMissionText(
            File.Exists(briefingPath) ? Clean(File.ReadAllText(briefingPath, Encoding.Latin1)) : string.Empty,
            File.Exists(messagePath) ? ParseMessages(File.ReadAllText(messagePath, Encoding.Latin1)) : new Dictionary<int, string>(),
            outcomes);
    }

    public static IReadOnlyDictionary<int, string> ParseMessages(string text)
    {
        var messages = new Dictionary<int, string>();
        int? current = null;
        var body = new List<string>();
        foreach (var raw in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Append("text -1"))
        {
            var match = MessageHeader().Match(raw.Trim());
            if (!match.Success)
            {
                if (current is not null) body.Add(raw);
                continue;
            }

            if (current is not null && current.Value >= 0)
                messages.Add(current.Value, Clean(string.Join("\n", body)));
            current = int.Parse(match.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture);
            body.Clear();
        }

        return messages;
    }

    private static string Clean(string value) => ColourMarkup().Replace(value, string.Empty).Trim();

    [GeneratedRegex("^text\\s+(?<id>-?\\d+)\\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MessageHeader();

    [GeneratedRegex("~\\d+", RegexOptions.CultureInvariant)]
    private static partial Regex ColourMarkup();
}
