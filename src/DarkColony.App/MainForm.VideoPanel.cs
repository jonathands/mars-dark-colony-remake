using System.Globalization;
using DarkColony.App.Diagnostics;
using DarkColony.App.Rendering;
using DarkColony.App.Ui;
using DarkColony.Engine.Assets;
using DarkColony.Presentation;

namespace DarkColony.App;

/// <summary>
/// The port's settings panel (not in the original): display mode, size,
/// scaling, gameplay view, vsync, pointer lock, the start-up intro, the
/// FPS counter, the debugging guides and the mouse buttons. It is drawn with the OPTIONS popup's art (<c>popp.spr</c>,
/// <c>mfonto7</c>). The main menu's OPTIONS button in the top-right corner
/// and F10 open it there; the in-game OPTIONS popup has a VIDEO button. While
/// it is open in a single-player game the world waits, as with OPTIONS.
/// </summary>
public sealed partial class MainForm
{
    private const int VideoPanelTop = 96;
    private const int VideoPanelRowsTop = 138;
    // Ten rows end above the "NETWORK GAMES PLAY CLASSIC" note at y 352.
    private const int VideoPanelRowStep = 21;
    private static readonly Rectangle VideoPanelOk = new(320, 368, 32, 32);
    private static readonly Rectangle VideoPanelCancel = new(360, 368, 32, 32);
    // The display-mode confirmation draws its own OK and Cancel higher up.
    private static readonly Rectangle DisplayConfirmOk = new(320, 224, 32, 32);
    private static readonly Rectangle DisplayConfirmCancel = new(360, 224, 32, 32);
    // The in-game OPTIONS popup's port-only VIDEO button, left of OK/Cancel.
    private static readonly Rectangle OptionsVideoButton = new(152, 336, 112, 24);
    // The main menu's port-only OPTIONS button, in the free top-right corner.
    private static readonly Rectangle MainMenuOptionsButton = new(520, 8, 112, 24);

    private DisplaySettings? _videoDraft;
    private IReadOnlyList<DisplayModeChoice> _videoDisplayModes = [];
    // A new exclusive display mode waits to be kept: the settings to go
    // back to, and when that happens by itself.
    private (DisplaySettings Previous, long Deadline)? _displayConfirm;

    private static Rectangle VideoPanelArrow(int row, int direction) =>
        new(direction < 0 ? 244 : 372, VideoPanelRowsTop + row * VideoPanelRowStep - 3, 16, 16);

    private bool VideoPanelOpen => _videoDraft is not null || _displayConfirm is not null;

    private void OpenVideoPanel()
    {
        if (_installation is null) return;
        try
        {
            _optionsArt ??= Sprite.Load(_installation.DataFile("intrface", "popp.spr"));
            _videoDisplayModes = DisplayModeSwitcher.Modes(DisplayModeSwitcher.MonitorOf(Handle).DeviceName);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        {
            _status = $"Video panel error: {error.Message}";
            return;
        }
        _videoDraft = _display;
        _optionsDraft = null;
        ClearTransientInputState();
        RuntimeLog.Info("Video panel opened.");
    }

    private bool OpenVideoPanelFromKey()
    {
        OpenVideoPanel();
        return true;
    }

    /// <summary>Takes every click while the panel or its confirmation is open.</summary>
    private void HandleVideoPanelClick(Point point)
    {
        if (_displayConfirm is { } confirm)
        {
            if (DisplayConfirmOk.Contains(point)) KeepDisplayMode();
            else if (DisplayConfirmCancel.Contains(point)) RevertDisplayMode(confirm.Previous);
            return;
        }
        if (_videoDraft is not { } draft) return;
        foreach (var row in Enum.GetValues<DisplaySettingRow>())
        foreach (var direction in new[] { -1, 1 })
            if (VideoPanelArrow((int)row, direction).Contains(point))
            {
                _videoDraft = DisplaySettingsEditor.Step(draft, row, direction, _videoDisplayModes);
                return;
            }
        if (VideoPanelCancel.Contains(point))
        {
            _videoDraft = null;
            RuntimeLog.Info("Video panel closed without changes.");
            return;
        }
        if (!VideoPanelOk.Contains(point)) return;
        _videoDraft = null;
        if (draft == _display) return;
        var previous = _display;
        ChangeDisplaySettings(draft);
        if (DisplaySettingsEditor.NeedsConfirmation(previous, _display) && _display.Mode == WindowMode.Exclusive)
            _displayConfirm = (previous, Environment.TickCount64 + 15_000);
    }

    /// <summary>Esc closes the panel; on the confirmation it goes back.</summary>
    private bool HandleVideoPanelKey(Keys key)
    {
        if (!VideoPanelOpen) return false;
        if (key != Keys.Escape) return true;
        if (_displayConfirm is { } confirm) RevertDisplayMode(confirm.Previous);
        else _videoDraft = null;
        return true;
    }

    /// <summary>Called every timer tick: an unanswered confirmation reverts after 15 seconds.</summary>
    private void UpdateDisplayConfirmation()
    {
        if (_displayConfirm is { } confirm && Environment.TickCount64 >= confirm.Deadline) RevertDisplayMode(confirm.Previous);
    }

    private void KeepDisplayMode()
    {
        _displayConfirm = null;
        RuntimeLog.Info($"Display mode kept: {_display.ExclusiveMode?.ToString() ?? "desktop"}.");
    }

    private void RevertDisplayMode(DisplaySettings previous)
    {
        _displayConfirm = null;
        RuntimeLog.Info("Display mode not kept; reverting.");
        ChangeDisplaySettings(previous);
    }

    private void DrawVideoPanel(Graphics graphics)
    {
        if (!VideoPanelOpen || _optionsArt is not { } art) return;
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
        var font = LoadFont("mfonto7");
        var cell = font.Sprite.Frames[0].Width + 1;
        void Text(string text, int x, int y) => DrawCellText(graphics, text, x, y, font, colour: 4, palette: "intrface");
        void Centred(string text, int left, int width, int y) => Text(text, left + (width - text.Length * cell) / 2, y);

        // The lopte panel pieces: top, middle strips every 16 pixels, bottom.
        var confirm = _displayConfirm;
        var bottom = confirm is null ? 408 : 272;
        Picture(0, 112, VideoPanelTop);
        for (var y = VideoPanelTop + 16; y < bottom; y += 16) Picture(1, 112, y);
        Picture(2, 112, bottom);
        Picture(6, 208, VideoPanelTop + 10);
        if (confirm is { } pending)
        {
            Centred("DISPLAY", 208, 112, VideoPanelTop + 17);
            Centred("KEEP THIS DISPLAY MODE?", 112, 304, 160);
            var seconds = (int)Math.Ceiling(Math.Max(0, pending.Deadline - Environment.TickCount64) / 1000.0);
            Centred($"REVERTING IN {seconds}", 112, 304, 184);
            Picture(7, DisplayConfirmOk.X, DisplayConfirmOk.Y);
            Picture(8, DisplayConfirmCancel.X, DisplayConfirmCancel.Y);
            return;
        }
        if (_videoDraft is not { } draft) return;
        Centred("OPTIONS", 208, 112, VideoPanelTop + 17);
        foreach (var row in Enum.GetValues<DisplaySettingRow>())
        {
            var y = VideoPanelRowsTop + (int)row * VideoPanelRowStep;
            Picture(6, 124, y - 7);
            Text(DisplaySettingsEditor.Label(row), 130, y);
            var fixedSize = row == DisplaySettingRow.Size && draft.Mode == WindowMode.Borderless;
            if (!fixedSize)
            {
                Picture(12, VideoPanelArrow((int)row, -1).X, VideoPanelArrow((int)row, -1).Y);
                Picture(13, VideoPanelArrow((int)row, 1).X, VideoPanelArrow((int)row, 1).Y);
            }
            Centred(DisplaySettingsEditor.Value(draft, row), 260, 112, y);
        }
        if (IsNetworkGame || InNetworkLobby || !draft.IsClassicView) Centred("NETWORK GAMES PLAY CLASSIC", 112, 304, 352);
        Picture(7, VideoPanelOk.X, VideoPanelOk.Y);
        Picture(8, VideoPanelCancel.X, VideoPanelCancel.Y);
    }

    /// <summary>The port's VIDEO button in the in-game OPTIONS popup.</summary>
    private void DrawOptionsVideoButton(Graphics graphics)
    {
        if (_optionsDraft is not null) DrawPortButton(graphics, OptionsVideoButton, "VIDEO");
    }

    /// <summary>The main menu's OPTIONS button, which opens the settings panel.</summary>
    private void DrawMainMenuOptionsButton(Graphics graphics)
    {
        if (_screen != MenuScreenId.Main || VideoPanelOpen || _installation is null) return;
        try
        {
            _optionsArt ??= Sprite.Load(_installation.DataFile("intrface", "popp.spr"));
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            return;
        }
        DrawPortButton(graphics, MainMenuOptionsButton, "OPTIONS");
    }

    /// <summary>A port-only button: <c>popp.spr</c> frame 6 with a centred <c>mfonto7</c> label.</summary>
    private void DrawPortButton(Graphics graphics, Rectangle bounds, string label)
    {
        if (_optionsArt is not { } art || art.Frames.Count <= 6) return;
        var palette = ScreenPalette("intrface");
        var image = art.Frames[6];
        var rgba = art.FrameRgba(6, palette: palette);
        if (_activeCanvas is { } canvas) canvas.DrawForeground(new GpuImage(image.Width, image.Height, rgba, transient: true), bounds.X, bounds.Y);
        else
        {
            using var bitmap = BitmapFromRgba(image.Width, image.Height, rgba);
            graphics.DrawImageUnscaled(bitmap, bounds.X, bounds.Y);
        }
        var font = LoadFont("mfonto7");
        var cell = font.Sprite.Frames[0].Width + 1;
        DrawCellText(graphics, label, bounds.X + (bounds.Width - label.Length * cell) / 2, bounds.Y + 7, font, colour: 4, palette: "intrface");
    }

    // The FPS counter (SHOW FPS): frames drawn over the last whole second.
    private long _fpsSecondStart;
    private int _fpsFrames;
    private int _fps;

    private void CountFrame()
    {
        _fpsFrames++;
        var now = Environment.TickCount64;
        if (now - _fpsSecondStart < 1000) return;
        _fps = (int)(_fpsFrames * 1000L / Math.Max(1, now - _fpsSecondStart));
        _fpsFrames = 0;
        _fpsSecondStart = now;
    }

    private void DrawFpsCounter(Graphics graphics)
    {
        if (!_display.ShowFps || _installation is null) return;
        var font = LoadFont("mfonto7");
        var cell = font.Sprite.Frames[0].Width + 1;
        var text = string.Create(CultureInfo.InvariantCulture, $"FPS {_fps}");
        FillNative(graphics, new Rectangle(4, 4, text.Length * cell + 4, font.Sprite.Frames[0].Height + 4), Color.Black);
        DrawCellText(graphics, text, 6, 6, font, colour: 4, palette: "intrface");
    }
}
