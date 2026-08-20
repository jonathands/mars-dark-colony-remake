using System.Text;
using System.Text.RegularExpressions;

namespace DarkColony.Engine.Scenario;

/// <summary>
/// One uninterpreted command line from an original mission <c>.tro</c> script.
/// Command arguments intentionally remain textual until their native runtime
/// handlers are traced; parsing a command is not evidence that the port can
/// execute it.
/// </summary>
public sealed record ScenarioTriggerCommand(string Verb, IReadOnlyList<string> Arguments, string Raw);

/// <summary>One original mission trigger and its source-order command list.</summary>
public sealed record ScenarioTrigger(
    int Id,
    string Mode,
    int RepeatCount,
    string Condition,
    ScenarioTriggerCondition ParsedCondition,
    IReadOnlyList<ScenarioTriggerCommand> Commands);

/// <summary>
/// Lossless structural reader for Dark Colony <c>.tro</c> mission scripts.
/// The executable owns condition evaluation and command execution; this type
/// only establishes a deterministic, testable data boundary for those later
/// subsystems.
/// </summary>
public static partial class ScenarioTriggers
{
    private static readonly Regex Header = TriggerHeader();

    public static IReadOnlyList<ScenarioTrigger> Load(string path) =>
        Parse(File.ReadAllText(path, Encoding.Latin1));

    public static IReadOnlyList<ScenarioTrigger> Parse(string text)
    {
        var triggers = new List<ScenarioTrigger>();
        var block = new List<string>();
        foreach (var raw in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Append(string.Empty))
        {
            var line = raw.Trim();
            if (line.Length != 0)
            {
                block.Add(line);
                continue;
            }

            if (block.Count == 0) continue;
            triggers.Add(ParseBlock(block));
            block.Clear();
        }

        if (triggers.Select(trigger => trigger.Id).Distinct().Count() != triggers.Count)
            throw new InvalidDataException("TRO contains duplicate trigger IDs.");
        return triggers;
    }

    private static ScenarioTrigger ParseBlock(IReadOnlyList<string> block)
    {
        var header = Header.Match(block[0]);
        if (!header.Success || !block[^1].Equals("end", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid TRO trigger block beginning '{block[0]}'.");

        var commands = block.Skip(1).Take(block.Count - 2).Select(line =>
        {
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) throw new InvalidDataException("TRO contains an empty command line.");
            return new ScenarioTriggerCommand(words[0], words.Skip(1).ToArray(), line);
        }).ToArray();
        var conditionText = header.Groups["condition"].Value;
        var parsedCondition = ScenarioTriggerConditionParser.TryParse(conditionText, out var parsed)
            ? parsed!
            : new ScenarioConditionOpaqueNode(conditionText);

        return new ScenarioTrigger(
            int.Parse(header.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture),
            header.Groups["mode"].Value,
            header.Groups["repeat"].Success
                ? int.Parse(header.Groups["repeat"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 1,
            conditionText,
            parsedCondition,
            commands);
    }

    [GeneratedRegex("^(?<id>\\d+)\\s+(?<mode>\\S+)(?:\\s+(?<repeat>-?\\d+))?\\s+(?<condition>\\(.*\\))$", RegexOptions.CultureInvariant)]
    private static partial Regex TriggerHeader();
}
