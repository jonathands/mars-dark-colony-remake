using System.Globalization;

namespace DarkColony.Presentation;

/// <summary>The rows of the port's settings panel, top to bottom.</summary>
public enum DisplaySettingRow
{
    Mode,
    Size,
    Scaling,
    View,
    VSync,
    PointerLock,
    IntroVideo,
    ShowFps,
}

/// <summary>
/// The Video panel's rules: the label and value text of each row, and the
/// next or previous value an arrow picks. The panel itself is drawn by the app.
/// </summary>
public static class DisplaySettingsEditor
{
    /// <summary>The gameplay views the panel offers, in order.</summary>
    public static IReadOnlyList<string> ViewChoices { get; } =
        ["classic", "auto", .. DisplaySettings.ViewPresets.Select(size => string.Create(CultureInfo.InvariantCulture, $"{size.Width}x{size.Height}"))];

    public static string Label(DisplaySettingRow row) => row switch
    {
        DisplaySettingRow.Mode => "MODE",
        DisplaySettingRow.Size => "SIZE",
        DisplaySettingRow.Scaling => "SCALING",
        DisplaySettingRow.View => "VIEW",
        DisplaySettingRow.VSync => "VSYNC",
        DisplaySettingRow.PointerLock => "POINTER LOCK",
        DisplaySettingRow.IntroVideo => "INTRO VIDEO",
        DisplaySettingRow.ShowFps => "SHOW FPS",
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, null),
    };

    public static string Value(DisplaySettings settings, DisplaySettingRow row) => row switch
    {
        DisplaySettingRow.Mode => settings.Mode switch
        {
            WindowMode.Windowed => "WINDOW",
            WindowMode.Borderless => "FULLSCREEN",
            _ => "EXCLUSIVE",
        },
        DisplaySettingRow.Size => settings.Mode switch
        {
            WindowMode.Windowed => settings.WindowScale == 0 ? "AUTO" : string.Create(CultureInfo.InvariantCulture, $"{settings.WindowScale}X"),
            WindowMode.Exclusive when settings.ExclusiveMode is { } mode => string.Create(CultureInfo.InvariantCulture, $"{mode.Width}X{mode.Height}"),
            _ => "DESKTOP",
        },
        DisplaySettingRow.Scaling => settings.Scale.ToString().ToUpperInvariant(),
        DisplaySettingRow.View => settings.View.ToUpperInvariant(),
        DisplaySettingRow.VSync => settings.VSync ? "ON" : "OFF",
        DisplaySettingRow.PointerLock => settings.ConfineCursor switch { null => "FULLSCREEN", true => "ALWAYS", false => "NEVER" },
        DisplaySettingRow.IntroVideo => settings.IntroVideo ? "ON" : "OFF",
        DisplaySettingRow.ShowFps => settings.ShowFps ? "ON" : "OFF",
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, null),
    };

    /// <summary>
    /// The settings with one row moved to its next (<paramref name="direction"/>
    /// 1) or previous (-1) value, wrapping around. The exclusive sizes are the
    /// monitor's <paramref name="displayModes"/>, after "desktop".
    /// </summary>
    public static DisplaySettings Step(DisplaySettings settings, DisplaySettingRow row, int direction, IReadOnlyList<DisplayModeChoice> displayModes)
    {
        if (direction is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(direction), direction, "Expected 1 or -1.");
        T Cycle<T>(IReadOnlyList<T> values, int index) => values[((index + direction) % values.Count + values.Count) % values.Count];
        int IndexOf<T>(IReadOnlyList<T> values, T value) => Math.Max(0, values.ToList().IndexOf(value));
        switch (row)
        {
            case DisplaySettingRow.Mode:
                WindowMode[] modes = [WindowMode.Windowed, WindowMode.Borderless, WindowMode.Exclusive];
                return settings with { Mode = Cycle(modes, IndexOf(modes, settings.Mode)) };
            case DisplaySettingRow.Size when settings.Mode == WindowMode.Windowed:
                int[] scales = [0, 1, 2, 3, 4, 5, 6, 7, 8];
                return settings with { WindowScale = Cycle(scales, IndexOf(scales, settings.WindowScale)) };
            case DisplaySettingRow.Size when settings.Mode == WindowMode.Exclusive:
                DisplayModeChoice?[] sizes = [null, .. displayModes.Select(mode => (DisplayModeChoice?)mode)];
                var current = sizes.ToList().FindIndex(mode => mode?.Width == settings.ExclusiveMode?.Width && mode?.Height == settings.ExclusiveMode?.Height);
                return settings with { ExclusiveMode = Cycle(sizes, Math.Max(0, current)) };
            case DisplaySettingRow.Size:
                return settings;
            case DisplaySettingRow.Scaling:
                ScaleMode[] scaling = [ScaleMode.Integer, ScaleMode.Fit, ScaleMode.Stretch];
                return settings with { Scale = Cycle(scaling, IndexOf(scaling, settings.Scale)) };
            case DisplaySettingRow.View:
                return settings with { View = Cycle(ViewChoices, IndexOf(ViewChoices, settings.View.ToLowerInvariant())) };
            case DisplaySettingRow.VSync:
                return settings with { VSync = !settings.VSync };
            case DisplaySettingRow.PointerLock:
                bool?[] locks = [null, true, false];
                return settings with { ConfineCursor = Cycle(locks, IndexOf(locks, settings.ConfineCursor)) };
            case DisplaySettingRow.IntroVideo:
                return settings with { IntroVideo = !settings.IntroVideo };
            case DisplaySettingRow.ShowFps:
                return settings with { ShowFps = !settings.ShowFps };
            default:
                throw new ArgumentOutOfRangeException(nameof(row), row, null);
        }
    }

    /// <summary>
    /// Whether applying <paramref name="next"/> changes the monitor's display
    /// mode, which the panel then asks to keep within 15 seconds.
    /// </summary>
    public static bool NeedsConfirmation(DisplaySettings previous, DisplaySettings next) =>
        next.Mode == WindowMode.Exclusive && (previous.Mode != WindowMode.Exclusive || previous.ExclusiveMode != next.ExclusiveMode);
}
