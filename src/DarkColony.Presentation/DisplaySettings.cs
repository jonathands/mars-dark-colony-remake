using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkColony.Presentation;

/// <summary>Where the game is shown.</summary>
/// <summary>Which mouse button orders units in a game.</summary>
public enum MouseControls
{
    /// <summary>As <c>dc.exe</c> (<c>0x4096E8</c>): the left button selects, and orders the selected units; the right one deselects.</summary>
    Original,
    /// <summary>The left button selects; the right one orders.</summary>
    Modern,
}

public enum WindowMode
{
    /// <summary>A window sized to the picture times <see cref="DisplaySettings.WindowScale"/>.</summary>
    Windowed,

    /// <summary>A borderless window covering its monitor, at the desktop resolution.</summary>
    Borderless,

    /// <summary>Exclusive fullscreen in <see cref="DisplaySettings.ExclusiveMode"/>.</summary>
    Exclusive,
}

/// <summary>A display mode for exclusive fullscreen. A refresh rate of 0 takes the highest available.</summary>
public readonly record struct DisplayModeChoice(int Width, int Height, int RefreshHz = 0)
{
    public override string ToString() => RefreshHz > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}@{RefreshHz}")
        : string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");

    /// <summary>Parses <c>WxH</c> or <c>WxH@Hz</c>.</summary>
    public static bool TryParse(string? text, out DisplayModeChoice mode)
    {
        mode = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var at = text.Split('@');
        if (at.Length > 2 || !TryParseSize(at[0], out var size)) return false;
        var refresh = 0;
        if (at.Length == 2 && (!int.TryParse(at[1], NumberStyles.None, CultureInfo.InvariantCulture, out refresh) || refresh is < 1 or > 1000)) return false;
        mode = new DisplayModeChoice(size.Width, size.Height, refresh);
        return true;
    }

    internal static bool TryParseSize(string text, out Size size)
    {
        size = default;
        var parts = text.Trim().ToLowerInvariant().Split('x');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
            width is < 320 or > 16384 || height is < 200 or > 16384) return false;
        size = new Size(width, height);
        return true;
    }
}

/// <summary>
/// The port's display settings. They are the port's own: the original only
/// ran at 640x480. Kept in <c>%LOCALAPPDATA%\DarkColonyPort\display.json</c>;
/// command-line flags override them for one run without saving.
/// </summary>
public sealed record DisplaySettings
{
    /// <summary>The classic picture: every screen of the original is 640x480.</summary>
    public static Size ClassicSize { get; } = new(640, 480);

    /// <summary>The largest gameplay view the port accepts.</summary>
    public static Size MaximumView { get; } = new(3840, 2160);

    /// <summary>The gameplay views offered by the Video panel, after classic and auto.</summary>
    public static IReadOnlyList<Size> ViewPresets { get; } =
    [
        new(800, 600), new(1024, 768), new(1280, 720), new(1280, 800),
        new(1366, 768), new(1600, 900), new(1920, 1080),
    ];

    public WindowMode Mode { get; init; } = WindowMode.Windowed;

    /// <summary>The window's size in multiples of the picture; 0 picks one from the monitor's DPI.</summary>
    public int WindowScale { get; init; }

    public ScaleMode Scale { get; init; } = ScaleMode.Integer;

    /// <summary>The display mode for <see cref="WindowMode.Exclusive"/>; none keeps the desktop's.</summary>
    public DisplayModeChoice? ExclusiveMode { get; init; }

    /// <summary>
    /// The gameplay view: <c>classic</c> (640x480, as the original), <c>auto</c>
    /// (the output divided by the largest whole scale that keeps at least
    /// 640x480), or <c>WxH</c>. Menus, briefings and videos stay 640x480, and
    /// network games always play classic.
    /// </summary>
    public string View { get; init; } = "classic";

    public bool VSync { get; init; } = true;

    /// <summary>
    /// Keeps the pointer on the picture during play. Unset confines it in
    /// fullscreen only; on and off apply to every mode.
    /// </summary>
    public bool? ConfineCursor { get; init; }

    /// <summary>
    /// The mouse buttons in a game: <see cref="MouseControls.Original"/> as
    /// <c>dc.exe</c> (the left button selects and orders, the right one
    /// deselects), or <see cref="MouseControls.Modern"/> (the right button orders).
    /// </summary>
    public MouseControls Mouse { get; init; } = MouseControls.Original;

    /// <summary>Whether <c>avi/intro.avi</c> plays before the main menu at start-up (PLAY INTRO always plays it).</summary>
    public bool IntroVideo { get; init; } = true;

    /// <summary>Whether the frame rate is shown in the picture's top-left corner.</summary>
    public bool ShowFps { get; init; }

    /// <summary>
    /// Whether the port's own debugging guides are drawn over the world: the
    /// P7 vent markers, the last move order's path, the selected unit's
    /// waypoints, the boxes around attacked actors and the building drop
    /// label. The original draws none of them.
    /// </summary>
    public bool ShowGuides { get; init; }

    /// <summary>Whether the pointer is kept on the picture during play in <see cref="Mode"/>.</summary>
    [JsonIgnore]
    public bool ConfinesCursor => ConfineCursor ?? Mode != WindowMode.Windowed;

    /// <summary>Whether <see cref="View"/> is the original 640x480.</summary>
    [JsonIgnore]
    public bool IsClassicView => !string.Equals(View, "auto", StringComparison.OrdinalIgnoreCase) && ParseView(View) is null;

    /// <summary>
    /// The gameplay view size for an output, or the classic 640x480. The
    /// output is the client area the picture is drawn on.
    /// </summary>
    public Size GameplayViewFor(Size output)
    {
        if (string.Equals(View, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var scale = Math.Max(1, Math.Min(output.Width / ClassicSize.Width, output.Height / ClassicSize.Height));
            return new Size(
                Math.Clamp(output.Width / scale, ClassicSize.Width, MaximumView.Width),
                Math.Clamp(output.Height / scale, ClassicSize.Height, MaximumView.Height));
        }
        return ParseView(View) ?? ClassicSize;
    }

    /// <summary>A view of at least 640x480 and at most <see cref="MaximumView"/>, or null for classic or invalid text.</summary>
    public static Size? ParseView(string? view) =>
        view is not null && DisplayModeChoice.TryParseSize(view, out var size) &&
        size.Width >= ClassicSize.Width && size.Height >= ClassicSize.Height &&
        size.Width <= MaximumView.Width && size.Height <= MaximumView.Height &&
        size != ClassicSize
            ? size
            : null;

    /// <summary>Replaces values the port cannot use with defaults.</summary>
    public DisplaySettings Sanitized() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : WindowMode.Windowed,
        WindowScale = WindowScale is >= 0 and <= 8 ? WindowScale : 0,
        Scale = Enum.IsDefined(Scale) ? Scale : ScaleMode.Integer,
        Mouse = Enum.IsDefined(Mouse) ? Mouse : MouseControls.Original,
        ExclusiveMode = ExclusiveMode is { Width: >= 320, Height: >= 200 } mode ? mode : null,
        View = string.Equals(View, "auto", StringComparison.OrdinalIgnoreCase) ? "auto"
            : ParseView(View) is { } size ? string.Create(CultureInfo.InvariantCulture, $"{size.Width}x{size.Height}")
            : "classic",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Reads the settings file. A missing file gives the defaults; an
    /// unreadable one gives the defaults and says why in <paramref name="problem"/>.
    /// </summary>
    public static DisplaySettings Load(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path)) return new DisplaySettings();
        try
        {
            var settings = JsonSerializer.Deserialize<DisplaySettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("The file is empty.");
            var sanitized = settings.Sanitized();
            if (sanitized != settings) problem = $"{path}: replaced invalid values with defaults.";
            return sanitized;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problem = $"{path}: {error.Message} Using the default display settings.";
            return new DisplaySettings();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(Sanitized(), JsonOptions));
    }

    /// <summary>
    /// Applies command-line overrides: <c>--windowed</c>, <c>--fullscreen</c>
    /// (borderless), <c>--exclusive [WxH[@Hz]]</c>, <c>--window-scale N</c>,
    /// <c>--scale-mode integer|fit|stretch</c>, <c>--view classic|auto|WxH</c>,
    /// <c>--vsync on|off</c>, <c>--confine-cursor on|off</c>, <c>--mouse original|modern</c>,
    /// <c>--intro-video on|off</c>, <c>--show-fps on|off</c> and <c>--guides on|off</c>. Unknown
    /// values are reported and ignored.
    /// </summary>
    public DisplaySettings WithArguments(IReadOnlyList<string> arguments, out IReadOnlyList<string> problems)
    {
        var settings = this;
        var found = new List<string>();
        string? Next(int index) => index + 1 < arguments.Count && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal) ? arguments[index + 1] : null;
        bool? Switch(string? value) => value?.ToLowerInvariant() switch { "on" or "true" or "1" => true, "off" or "false" or "0" => false, _ => null };
        for (var index = 0; index < arguments.Count; index++)
        {
            var value = Next(index);
            switch (arguments[index].ToLowerInvariant())
            {
                case "--windowed":
                    settings = settings with { Mode = WindowMode.Windowed };
                    break;
                case "--fullscreen":
                    settings = settings with { Mode = WindowMode.Borderless };
                    break;
                case "--exclusive":
                    if (value is not null && DisplayModeChoice.TryParse(value, out var mode)) settings = settings with { Mode = WindowMode.Exclusive, ExclusiveMode = mode };
                    else if (value is null) settings = settings with { Mode = WindowMode.Exclusive };
                    else found.Add($"--exclusive {value}: expected WxH or WxH@Hz.");
                    break;
                case "--window-scale":
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var scale) && scale is >= 0 and <= 8) settings = settings with { WindowScale = scale };
                    else found.Add($"--window-scale {value}: expected 0 (automatic) to 8.");
                    break;
                case "--scale-mode":
                    if (Enum.TryParse<ScaleMode>(value, ignoreCase: true, out var scaleMode) && Enum.IsDefined(scaleMode)) settings = settings with { Scale = scaleMode };
                    else found.Add($"--scale-mode {value}: expected integer, fit or stretch.");
                    break;
                case "--view":
                    if (value is not null && (value.Equals("classic", StringComparison.OrdinalIgnoreCase) || value.Equals("auto", StringComparison.OrdinalIgnoreCase) || ParseView(value) is not null))
                        settings = settings with { View = value.ToLowerInvariant() };
                    else found.Add($"--view {value}: expected classic, auto or WxH from 640x480 to 3840x2160.");
                    break;
                case "--vsync":
                    if (Switch(value) is { } vsync) settings = settings with { VSync = vsync };
                    else found.Add($"--vsync {value}: expected on or off.");
                    break;
                case "--confine-cursor":
                    if (Switch(value) is { } confine) settings = settings with { ConfineCursor = confine };
                    else found.Add($"--confine-cursor {value}: expected on or off.");
                    break;
                case "--mouse":
                    if (Enum.TryParse<MouseControls>(value, ignoreCase: true, out var mouse) && Enum.IsDefined(mouse)) settings = settings with { Mouse = mouse };
                    else found.Add($"--mouse {value}: expected original or modern.");
                    break;
                case "--intro-video":
                    if (Switch(value) is { } intro) settings = settings with { IntroVideo = intro };
                    else found.Add($"--intro-video {value}: expected on or off.");
                    break;
                case "--show-fps":
                    if (Switch(value) is { } fps) settings = settings with { ShowFps = fps };
                    else found.Add($"--show-fps {value}: expected on or off.");
                    break;
                case "--guides":
                    if (Switch(value) is { } guides) settings = settings with { ShowGuides = guides };
                    else found.Add($"--guides {value}: expected on or off.");
                    break;
            }
        }
        problems = found;
        return settings.Sanitized();
    }
}
