using DarkColony.App.Diagnostics;
using DarkColony.App.Rendering;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Interface;
using DarkColony.Engine.Simulation;

namespace DarkColony.App;

/// <summary>
/// The in-game OPTIONS popup (<c>intrface/lopte</c> over <c>intrface/popp</c>).
/// While it is open the world does not update (the native loop
/// <c>0x432B00</c> runs instead of the game).
/// </summary>
public sealed partial class MainForm
{
    private GameOptions? _gameOptions;
    private GameOptions? _optionsDraft;
    private Dictionary<int, string>? _optionsText;
    private Sprite? _optionsArt;

    // lopte pushbuttons.
    private static readonly (int Id, Rectangle Bounds)[] OptionsButtons =
    [
        (40, new(272, 192, 14, 14)), (41, new(368, 192, 14, 14)),
        (42, new(272, 224, 14, 14)), (43, new(368, 224, 14, 14)),
        (67, new(272, 256, 14, 14)), (68, new(368, 256, 14, 14)),
        (44, new(272, 288, 14, 14)), (45, new(368, 288, 14, 14)),
        (56, new(320, 332, 32, 32)), (55, new(360, 332, 32, 32)),
    ];

    private GameOptions CurrentGameOptions()
    {
        if (_gameOptions is not null) return _gameOptions;
        _gameOptions = _installation is null
            ? new GameOptions(100, 5, 5, 2)
            : GameOptions.Load(_installation.ExecutablePath);
        return _gameOptions;
    }

    private void OpenGameOptions()
    {
        if (_installation is null) return;
        try
        {
            _optionsText ??= MenuText(_installation.DataFile("intrface", "lopte"));
            _optionsArt ??= Sprite.Load(_installation.DataFile("intrface", "popp.spr"));
            // 0x432D8C: the speed comes back from the running interval.
            _optionsDraft = CurrentGameOptions() with { SpeedPercent = GameOptions.SpeedFromInterval(_clock.IntervalMilliseconds) };
            ClearTransientInputState();
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Options error: {error.Message}";
        }
    }

    /// <summary>A menu file's <c>textmsg N text</c> lines.</summary>
    private static Dictionary<int, string> MenuText(string path)
    {
        var text = new Dictionary<int, string>();
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == "textmsg" && int.TryParse(parts[1], out var id)) text[id] = parts[2].Trim();
        }
        return text;
    }

    /// <summary>Handles a click while the popup is open; it takes every click.</summary>
    private void HandleGameOptionsClick(Point point)
    {
        if (_optionsDraft is not { } draft) return;
        var id = OptionsButtons.FirstOrDefault(button => button.Bounds.Contains(point)).Id;
        var next = id switch
        {
            40 => draft.Slower(),
            41 => draft.Faster(),
            42 => draft.Quieter(),
            43 => draft.Louder(),
            67 => draft.CdQuieter(),
            68 => draft.CdLouder(),
            44 => draft.LessDetail(),
            45 => draft.MoreDetail(),
            _ => draft,
        };
        // The volumes take effect at once, before any commit.
        if (next.CdVolume != draft.CdVolume) ApplyCdVolume(next.CdVolume);
        _optionsDraft = next;
        if (id == 56)
        {
            // 0x432B26: commit; a new speed changes the update interval.
            _gameOptions = next;
            // The original sends a new speed to every player (0x4214E4); the port keeps network games at their speed.
            if (IsNetworkGame && next.UpdateIntervalMilliseconds != _clock.IntervalMilliseconds) _status = "Game speed is fixed in a network game.";
            else if (next.UpdateIntervalMilliseconds != _clock.IntervalMilliseconds)
            {
                _clock = new FixedStepClock(Environment.TickCount64, next.UpdateIntervalMilliseconds);
                RuntimeLog.Info($"Game speed {next.SpeedPercent}%: {next.UpdateIntervalMilliseconds} ms per update.");
            }
            _optionsDraft = null;
        }
        else if (id == 55) _optionsDraft = null;
    }

    private void ApplyCdVolume(int level) => _cdMusic?.SetVolume(GameOptions.AuxVolume(level));

    private void DrawGameOptions(Graphics graphics)
    {
        if (_optionsDraft is not { } draft || _optionsArt is not { } art) return;
        var palette = ScreenPalette("intrface");
        void Picture(int frame, int x, int y)
        {
            if (frame >= art.Frames.Count || art.Frames[frame].Width == 0) return;
            var image = art.Frames[frame];
            var rgba = art.FrameRgba(frame, palette: palette);
            if (_activeCanvas is { } canvas) canvas.DrawForeground(new GpuImage(image.Width, image.Height, rgba, transient: true), x + image.AnchorX, y + image.AnchorY);
            else
            {
                using var bitmap = BitmapFromRgba(image.Width, image.Height, rgba);
                graphics.DrawImageUnscaled(bitmap, x + image.AnchorX, y + image.AnchorY);
            }
        }

        // lopte pictures in gadget order: the panel (0-15), the title box (3), the row boxes (16-19).
        Picture(0, 112, 128);
        for (var row = 1; row <= 14; row++) Picture(1, 112, 128 + row * 16);
        Picture(2, 112, 352);
        Picture(6, 208, 136);
        foreach (var y in new[] { 188, 220, 252, 284 }) Picture(6, 152, y);
        foreach (var (id, bounds) in OptionsButtons)
            Picture(id switch { 55 => 8, 56 => 7, 40 or 42 or 67 or 44 => 12, _ => 13 }, bounds.X, bounds.Y);

        var font = LoadFont("mfonto7");
        var cell = font.Sprite.Frames[0].Width + 1;
        string Text(int id) => _optionsText is not null && _optionsText.TryGetValue(id, out var text) ? text : string.Empty;
        void Label(string text, int x, int y, int width, bool centre) =>
            DrawCellText(graphics, text, centre ? x + (width - text.Length * cell) / 2 : x, y, font, colour: 4, palette: "intrface");
        Label(Text(1), 212, 144, 106, centre: true);
        Label(Text(2), 156, 196, 100, centre: false);
        Label(Text(3), 156, 228, 100, centre: false);
        Label(Text(5), 156, 260, 100, centre: false);
        Label(Text(4), 156, 292, 100, centre: false);
        // in_text 46, 47, 69 and 48: eight cells, centred.
        Label($"{draft.SpeedPercent}%", 300, 194, 8 * cell, centre: true);
        Label($"{draft.SoundVolume}", 300, 226, 8 * cell, centre: true);
        Label($"{draft.CdVolume}", 300, 258, 8 * cell, centre: true);
        Label(Text(10 + draft.Detail), 300, 290, 8 * cell, centre: true);
    }
}
