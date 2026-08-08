using System.Globalization;
using System.Text;

namespace DarkColony.Engine.Data;

/// <summary>
/// A logical rectangle authored by an original <c>intrface/*</c> definition.
/// This deliberately avoids platform drawing types: the data reader is shared
/// by the deterministic engine and the Windows presentation host.
/// </summary>
public readonly record struct InterfaceRectangle(int X, int Y, int Width, int Height);

/// <summary>Recognized interactive and presentational gadget declarations.</summary>
public enum InterfaceControlKind
{
    PushButton,
    CheckButton,
    Count,
    Scount,
    Picture,
    InputText,
}

/// <summary>
/// One gadget declaration in an original interface source file. The source
/// fields after the primary frame are retained as text because their exact
/// runtime meaning is gadget-specific and has not yet been recovered.
/// </summary>
public sealed record InterfaceControl(
    InterfaceControlKind Kind,
    int Id,
    int GroupId,
    InterfaceRectangle Bounds,
    int? Frame,
    IReadOnlyList<string> RemainingArguments);

/// <summary>
/// A source group declaration. <see cref="Values"/> intentionally preserves
/// every value after the group's mode field: the trailing value has not been
/// proven to be a member ID in all original interface files.
/// </summary>
public sealed record InterfaceGroup(int Id, int Mode, IReadOnlyList<int> Values);

/// <summary>
/// Decoder for the textual interface definitions shipped below
/// <c>intrface</c>. It provides a data boundary for reconstructed UI layouts;
/// it does not attempt to execute original gadget callbacks.
/// </summary>
public sealed class InterfaceDefinition
{
    private InterfaceDefinition(
        int width,
        int height,
        IReadOnlyDictionary<int, string> textMessages,
        IReadOnlyDictionary<int, InterfaceControl> controls,
        IReadOnlyDictionary<int, InterfaceGroup> groups)
    {
        Width = width;
        Height = height;
        TextMessages = textMessages;
        Controls = controls;
        Groups = groups;
    }

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyDictionary<int, string> TextMessages { get; }
    public IReadOnlyDictionary<int, InterfaceControl> Controls { get; }
    public IReadOnlyDictionary<int, InterfaceGroup> Groups { get; }

    public string? LabelFor(int controlId) =>
        TextMessages.TryGetValue(controlId, out var label) ? label : null;

    public static InterfaceDefinition Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path, Encoding.Latin1));
    }

    public static InterfaceDefinition Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var textMessages = new Dictionary<int, string>();
        var controls = new Dictionary<int, InterfaceControl>();
        var groups = new Dictionary<int, InterfaceGroup>();
        var width = 0;
        var height = 0;

        var lineNumber = 0;
        foreach (var rawLine in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('%')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            switch (parts[0].ToLowerInvariant())
            {
                case "size":
                    Require(parts, 3, lineNumber, "size");
                    width = ReadInt(parts[1], lineNumber, "width");
                    height = ReadInt(parts[2], lineNumber, "height");
                    break;
                case "textmsg":
                    Require(parts, 3, lineNumber, "textmsg");
                    var textId = ReadInt(parts[1], lineNumber, "text ID");
                    var labelStart = rawLine.IndexOf(parts[2], StringComparison.Ordinal);
                    textMessages[textId] = rawLine[labelStart..].Trim();
                    break;
                case "group":
                    Require(parts, 3, lineNumber, "group");
                    var groupId = ReadInt(parts[1], lineNumber, "group ID");
                    var mode = ReadInt(parts[2], lineNumber, "group mode");
                    groups[groupId] = new InterfaceGroup(groupId, mode,
                        parts.Skip(3).Select(value => ReadInt(value, lineNumber, "group value")).ToArray());
                    break;
                default:
                    if (!TryControlKind(parts[0], out var kind)) break;
                    Require(parts, 7, lineNumber, parts[0]);
                    var id = ReadInt(parts[1], lineNumber, "control ID");
                    var controlGroupId = ReadInt(parts[2], lineNumber, "control group ID");
                    var bounds = new InterfaceRectangle(
                        ReadInt(parts[3], lineNumber, "x"),
                        ReadInt(parts[4], lineNumber, "y"),
                        ReadInt(parts[5], lineNumber, "width"),
                        ReadInt(parts[6], lineNumber, "height"));
                    int? frame = parts.Length > 7 && int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFrame) && parsedFrame >= 0
                        ? parsedFrame
                        : null;
                    controls[id] = new InterfaceControl(kind, id, controlGroupId, bounds, frame, parts.Skip(8).ToArray());
                    break;
            }
        }

        return new InterfaceDefinition(width, height, textMessages, controls, groups);
    }

    private static bool TryControlKind(string token, out InterfaceControlKind kind)
    {
        kind = token.ToLowerInvariant() switch
        {
            "pushb" => InterfaceControlKind.PushButton,
            "checkb" => InterfaceControlKind.CheckButton,
            "count" => InterfaceControlKind.Count,
            "scount" => InterfaceControlKind.Scount,
            "picture" => InterfaceControlKind.Picture,
            "in_text" => InterfaceControlKind.InputText,
            _ => default,
        };
        return token.Equals("pushb", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("checkb", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("count", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("scount", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("picture", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("in_text", StringComparison.OrdinalIgnoreCase);
    }

    private static void Require(string[] parts, int minimumCount, int lineNumber, string directive)
    {
        if (parts.Length < minimumCount)
            throw new FormatException($"Interface {directive} line {lineNumber} requires at least {minimumCount} fields.");
    }

    private static int ReadInt(string value, int lineNumber, string field) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new FormatException($"Interface line {lineNumber} has invalid {field} value '{value}'.");
}
