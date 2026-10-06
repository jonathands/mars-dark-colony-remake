using System.Globalization;
using System.Text;
using DarkColony.Engine.Data;

namespace DarkColony.Engine.Interface;

/// <summary>The widget kinds of an interface definition, in <c>dc.exe</c>'s keyword table <c>0x479470</c>.</summary>
public enum NativeWidgetKind
{
    PushButton,
    CheckButton,
    Picture,
    InputText,
    Count,
    SpecialCount,
    Group,
    List,
    Scroll,
    Gadget,
    Label,
    ButtonAnimation,
}

/// <summary>A gadget's animation state (<c>gadget.c</c>, stored at gadget <c>+0xA8</c>).</summary>
public enum GadgetAnimationMode
{
    Loop = 0,
    OneOff = 1,
    Stopped = 2,
}

/// <summary>Text alignment as <c>0x421A04</c> reads it.</summary>
public enum NativeTextAlign
{
    Centre = 0,
    Left = 1,
    Right = 2,
}

/// <summary>
/// One widget of an interface definition. Every widget carries the shared
/// options that follow its <c>-</c> (<c>0x423669</c>); the slot allocator
/// <c>0x4222C8</c> defaults them to remap 7, intensity 16 and no background.
/// The other fields belong to particular kinds.
/// </summary>
public sealed record NativeWidget
{
    public required NativeWidgetKind Kind { get; init; }
    public required int Id { get; init; }
    public InterfaceRectangle Bounds { get; init; }

    /// <summary>The colour remap (0-7) of the widget's text, slot <c>+0x1C</c>.</summary>
    public int Remap { get; init; } = 7;

    /// <summary>The brightness (0-31) of the widget's text, slot <c>+0x20</c>.</summary>
    public int Intensity { get; init; } = NativeWidgetRules.NormalBrightness;

    /// <summary>The colour that clears the widget's box before it draws, slot <c>+0x18</c>.</summary>
    public string? Background { get; init; }

    public bool ReadOnly { get; init; }

    /// <summary>A button's first picture number (<c>+4</c>), negative for a brightness offset.</summary>
    public int FirstPicture { get; init; } = -1;

    /// <summary>A button's second picture number (<c>+8</c>), negative for a brightness offset.</summary>
    public int SecondPicture { get; init; } = -1;

    /// <summary>The <c>textmsg</c> number of a button's or label's text.</summary>
    public int? Message { get; init; }

    public NativeTextAlign Align { get; init; } = NativeTextAlign.Centre;
    public int Font { get; init; }

    /// <summary>A button's list (<c>list n step</c>) or a scroll bar's list.</summary>
    public int? LinkedList { get; init; }

    /// <summary>How far a button moves its list's selection.</summary>
    public int ListStep { get; init; }

    /// <summary>A gadget's animation name, looked up in the screen's FIN files.</summary>
    public string? Animation { get; init; }

    public GadgetAnimationMode Mode { get; init; } = GadgetAnimationMode.Loop;

    /// <summary>
    /// A masked gadget draws over the screen; an <c>unmask</c> gadget first
    /// clears its box to its background (<c>0x424891</c>).
    /// </summary>
    public bool Masked { get; init; } = true;

    /// <summary>A list's selected-row colour (<c>selbg</c>, colour 1 when absent).</summary>
    public string? SelectionBackground { get; init; }

    /// <summary>A scroll bar's frame and thumb colour (<c>fg</c>, colour 1 when absent).</summary>
    public string? Foreground { get; init; }

    /// <summary>A list's scroll bar.</summary>
    public int? LinkedScroll { get; init; }

    /// <summary>An <c>in_text</c>'s columns and rows of font cells.</summary>
    public int Columns { get; init; }
    public int Rows { get; init; }

    /// <summary>A picture's frame of the screen's picture sheet.</summary>
    public int Frame { get; init; } = -1;

    /// <summary>A <c>banim</c>'s gadgets, played in turn, and the buttons each one reveals.</summary>
    public IReadOnlyList<int> Gadgets { get; init; } = [];
    public IReadOnlyList<int> Buttons { get; init; } = [];
}

/// <summary>
/// A whole interface definition (<c>intrface/*e</c>) as <c>widget.c</c>
/// reads it: the screen resources, its colours and button brightnesses,
/// every widget with its options, and the text messages.
/// </summary>
/// <remarks>
/// Colours are named. A screen starts with the five defaults of
/// <c>0x479420</c> (<see cref="NativeInterfaceColours"/>); a <c>colour</c>
/// line overrides a name it repeats and appends a new one (<c>0x421790</c>).
/// </remarks>
public sealed class NativeScreenDefinition
{
    private readonly Dictionary<int, NativeWidget> _widgets;

    private NativeScreenDefinition(
        string? background, string? pictures, IReadOnlyDictionary<int, string> fonts,
        IReadOnlyList<(string Name, byte Red, byte Green, byte Blue)> colours, int brightPushed, int brightHighlight,
        Dictionary<int, NativeWidget> widgets, IReadOnlyDictionary<int, string> messages)
    {
        Background = background;
        Pictures = pictures;
        Fonts = fonts;
        DeclaredColours = colours;
        BrightPushed = brightPushed;
        BrightHighlight = brightHighlight;
        _widgets = widgets;
        Messages = messages;
    }

    /// <summary>The background picture, for example <c>intrface/choo</c>.</summary>
    public string? Background { get; }

    /// <summary>The picture sheet buttons draw from, for example <c>intrface/knobe</c>.</summary>
    public string? Pictures { get; }

    public IReadOnlyDictionary<int, string> Fonts { get; }

    /// <summary>The <c>colour</c> lines, in file order.</summary>
    public IReadOnlyList<(string Name, byte Red, byte Green, byte Blue)> DeclaredColours { get; }

    public int BrightPushed { get; }
    public int BrightHighlight { get; }
    public IReadOnlyDictionary<int, string> Messages { get; }

    /// <summary>The widgets in slot order, which is also the order they draw in.</summary>
    public IEnumerable<NativeWidget> Widgets => _widgets.Values.OrderBy(widget => widget.Id);

    public NativeWidget? Widget(int id) => _widgets.GetValueOrDefault(id);

    public string? Message(int? id) => id is { } value ? Messages.GetValueOrDefault(value) : null;

    public static NativeScreenDefinition Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path, Encoding.Latin1));
    }

    public static NativeScreenDefinition Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? background = null, pictures = null;
        var fonts = new Dictionary<int, string>();
        var colours = new List<(string, byte, byte, byte)>();
        var brightPushed = 0;
        var brightHighlight = 0;
        var widgets = new Dictionary<int, NativeWidget>();
        var messages = new Dictionary<int, string>();
        var lineNumber = 0;
        foreach (var rawLine in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;
            var percent = rawLine.IndexOf('%', StringComparison.Ordinal);
            var line = (percent >= 0 ? rawLine[..percent] : rawLine).Trim();
            if (line.Length == 0) continue;
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var reader = new WordReader(words, lineNumber);
            var keyword = reader.Word().ToLowerInvariant();
            switch (keyword)
            {
                case "end":
                    return new NativeScreenDefinition(background, pictures, fonts, colours, brightPushed, brightHighlight, widgets, messages);
                case "background": background = reader.Word(); break;
                case "pictures": pictures = reader.Word(); break;
                case "font": fonts[reader.Int()] = reader.Word(); break;
                case "bright_pushed": brightPushed = reader.Int(); break;
                case "bright_highlight": brightHighlight = reader.Int(); break;
                case "colour":
                    colours.Add((reader.Word(), (byte)reader.Int(), (byte)reader.Int(), (byte)reader.Int()));
                    break;
                case "textmsg":
                {
                    var id = reader.Int();
                    var start = rawLine.IndexOf(words[2], StringComparison.Ordinal);
                    messages[id] = rawLine[start..].Trim();
                    break;
                }
                default:
                    if (ReadWidget(keyword, reader) is { } widget) widgets[widget.Id] = widget;
                    break;
            }
        }
        return new NativeScreenDefinition(background, pictures, fonts, colours, brightPushed, brightHighlight, widgets, messages);
    }

    private static NativeWidget? ReadWidget(string keyword, WordReader reader)
    {
        NativeWidgetKind? kind = keyword switch
        {
            "pushb" => NativeWidgetKind.PushButton,
            "checkb" => NativeWidgetKind.CheckButton,
            "picture" => NativeWidgetKind.Picture,
            "in_text" => NativeWidgetKind.InputText,
            "count" => NativeWidgetKind.Count,
            "scount" => NativeWidgetKind.SpecialCount,
            "list" => NativeWidgetKind.List,
            "scroll" => NativeWidgetKind.Scroll,
            "gadget" => NativeWidgetKind.Gadget,
            "label" => NativeWidgetKind.Label,
            "banim" => NativeWidgetKind.ButtonAnimation,
            _ => null,
        };
        if (kind is not { } widgetKind) return null;
        var id = reader.Int();
        reader.Int(); // the description number
        if (widgetKind == NativeWidgetKind.ButtonAnimation)
        {
            // 0x427938: gadget count, button count, the gadgets, then the buttons.
            var gadgetCount = reader.Int();
            var buttonCount = reader.Int();
            var gadgets = Enumerable.Range(0, gadgetCount).Select(_ => reader.Int()).ToArray();
            var buttons = Enumerable.Range(0, buttonCount).Select(_ => reader.Int()).ToArray();
            return new NativeWidget { Kind = widgetKind, Id = id, Gadgets = gadgets, Buttons = buttons };
        }

        if (widgetKind == NativeWidgetKind.Picture && reader.Remaining == 3)
        {
            // The old intro screen (buttonse) gives a picture only a position: "picture 5 0 0 70 10000".
            return new NativeWidget { Kind = widgetKind, Id = id, Bounds = new InterfaceRectangle(reader.Int(), reader.Int(), 0, 0), Frame = reader.Int() };
        }

        var bounds = new InterfaceRectangle(reader.Int(), reader.Int(), reader.Int(), reader.Int());
        var widget = new NativeWidget { Kind = widgetKind, Id = id, Bounds = bounds };
        switch (widgetKind)
        {
            case NativeWidgetKind.PushButton or NativeWidgetKind.CheckButton or NativeWidgetKind.Count:
                widget = widget with { FirstPicture = reader.Int(), SecondPicture = reader.Int() };
                while (reader.Peek() is { } word && word != "-")
                {
                    reader.Word();
                    switch (word.ToLowerInvariant())
                    {
                        // "label <align> <textmsg> <font>" (0x427071).
                        case "label":
                            widget = widget with { Align = Align(reader.Word()), Message = reader.Int(), Font = reader.Int() };
                            break;
                        // "list <list> <step>" (0x427049).
                        case "list":
                            widget = widget with { LinkedList = reader.Int(), ListStep = reader.Int() };
                            break;
                        // The catalog counts' "offset x y" and "highlight" are not used by the menus.
                        case "offset": reader.Int(); reader.Int(); break;
                        case "highlight": break;
                        default: throw reader.Unknown(word);
                    }
                }
                break;
            case NativeWidgetKind.SpecialCount:
                // "scount 75 0 524 456 72 17 104": one picture.
                widget = widget with { FirstPicture = reader.Int() };
                break;
            case NativeWidgetKind.Picture:
                // The fifth number is the picture; "picture 180 0 376 22 14 12 148".
                widget = widget with { Frame = reader.Int() };
                break;
            case NativeWidgetKind.InputText:
                // "in_text id desc x y columns rows font": the third and fourth numbers count cells.
                widget = widget with { Columns = bounds.Width, Rows = bounds.Height, Font = reader.Int(), Bounds = bounds with { Width = 0, Height = 0 }, Align = NativeTextAlign.Left };
                while (reader.Peek() is { } word && word != "-")
                {
                    reader.Word();
                    switch (word.ToLowerInvariant())
                    {
                        case "align": widget = widget with { Align = Align(reader.Word()) }; break;
                        case "bufsize": reader.Int(); break;
                        case "mask": break;
                        // "init 3" (getsvre); a bare "init" also appears.
                        case "init":
                            if (int.TryParse(reader.Peek(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) reader.Int();
                            break;
                        default: throw reader.Unknown(word);
                    }
                }
                break;
            case NativeWidgetKind.List:
                while (reader.Peek() is { } word && word != "-")
                {
                    reader.Word();
                    switch (word.ToLowerInvariant())
                    {
                        case "scroll": widget = widget with { LinkedScroll = reader.Int() }; break;
                        case "selbg": widget = widget with { SelectionBackground = reader.Word() }; break;
                        default: throw reader.Unknown(word);
                    }
                }
                break;
            case NativeWidgetKind.Scroll:
                while (reader.Peek() is { } word && word != "-")
                {
                    reader.Word();
                    switch (word.ToLowerInvariant())
                    {
                        case "list": widget = widget with { LinkedList = reader.Int() }; break;
                        case "fg": widget = widget with { Foreground = reader.Word() }; break;
                        default: throw reader.Unknown(word);
                    }
                }
                break;
            case NativeWidgetKind.Gadget:
                widget = widget with { Animation = reader.Word() };
                // 0x424AE8: anim_loop 0, anim_oneoff 1, anim_stopped 2.
                widget = widget with
                {
                    Mode = reader.Word().ToLowerInvariant() switch
                    {
                        "anim_loop" => GadgetAnimationMode.Loop,
                        "anim_oneoff" => GadgetAnimationMode.OneOff,
                        "anim_stopped" => GadgetAnimationMode.Stopped,
                        var other => throw reader.Unknown(other),
                    },
                };
                while (reader.Peek() is { } word && word != "-")
                {
                    reader.Word();
                    widget = word.ToLowerInvariant() switch
                    {
                        "mask" => widget with { Masked = true },
                        "unmask" => widget with { Masked = false },
                        "read_write" => widget,
                        _ => throw reader.Unknown(word),
                    };
                }
                break;
            case NativeWidgetKind.Label:
                // 0x426CD0: a label is left-aligned unless it says otherwise.
                widget = widget with { Align = NativeTextAlign.Left };
                while (reader.Peek() is { } word && word != "-")
                {
                    reader.Word();
                    switch (word.ToLowerInvariant())
                    {
                        case "label": widget = widget with { Message = reader.Int() }; break;
                        case "align": widget = widget with { Align = Align(reader.Word()) }; break;
                        case "font": widget = widget with { Font = reader.Int() }; break;
                        default: throw reader.Unknown(word);
                    }
                }
                break;
        }

        // The shared options after "-" (0x423669); some lines put "read_write" or "immediate" before it.
        while (reader.Peek() is { } option)
        {
            reader.Word();
            switch (option.ToLowerInvariant())
            {
                case "-": break;
                case "bg": widget = widget with { Background = reader.Word() }; break;
                case "remap": widget = widget with { Remap = reader.Int() }; break;
                case "intens": widget = widget with { Intensity = reader.Int() }; break;
                case "read_only": widget = widget with { ReadOnly = true }; break;
                case "read_write": widget = widget with { ReadOnly = false }; break;
                case "immediate" or "mask" or "unmask": break;
                default: throw reader.Unknown(option);
            }
        }
        return widget;
    }

    private static NativeTextAlign Align(string word) => word.ToLowerInvariant() switch
    {
        "left" => NativeTextAlign.Left,
        "right" => NativeTextAlign.Right,
        _ => NativeTextAlign.Centre,
    };

    private sealed class WordReader(string[] words, int lineNumber)
    {
        private int _next;

        public string? Peek() => _next < words.Length ? words[_next] : null;

        public int Remaining => words.Length - _next;

        public string Word() => _next < words.Length
            ? words[_next++]
            : throw new FormatException($"Interface line {lineNumber} ends early.");

        public int Int()
        {
            var word = Word();
            return int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new FormatException($"Interface line {lineNumber}: '{word}' is not a number.");
        }

        public FormatException Unknown(string word) => new($"Interface line {lineNumber}: unknown keyword '{word}'.");
    }
}
