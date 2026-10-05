using DarkColony.App.Diagnostics;
using DarkColony.App.Rendering;
using DarkColony.App.Ui;
using DarkColony.Engine.Assets;
using DarkColony.Presentation;

namespace DarkColony.App;

/// <summary>
/// The port's Video panel (not in the original): display mode, size,
/// scaling, gameplay view, vsync and pointer lock. It is drawn with the
/// OPTIONS popup's art (<c>popp.spr</c>, <c>mfonto7</c>). F10 opens it on the
/// main menu; the in-game OPTIONS popup has a VIDEO button. While it is open
/// in a single-player game the world waits, as with OPTIONS.
/// </summary>
public sealed partial class MainForm
{
    private const int VideoPanelTop = 96;
    private const int VideoPanelRowsTop = 150;
    private const int VideoPanelRowStep = 30;
    private static readonly Rectangle VideoPanelOk = new(320, 344, 32, 32);
    private static readonly Rectangle VideoPanelCancel = new(360, 344, 32, 32);
    // The in-game OPTIONS popup's port-only VIDEO button, left of OK/Cancel.
    private static readonly Rectangle OptionsVideoButton = new(152, 336, 112, 24);

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
            if (VideoPanelOk.Contains(point)) KeepDisplayMode();
            else if (VideoPanelCancel.Contains(point)) RevertDisplayMode(confirm.Previous);
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
        var bottom = confirm is null ? 384 : 272;
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
            Picture(7, VideoPanelOk.X, 224);
            Picture(8, VideoPanelCancel.X, 224);
            return;
        }
        if (_videoDraft is not { } draft) return;
        Centred("VIDEO", 208, 112, VideoPanelTop + 17);
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
        if (IsNetworkGame || InNetworkLobby || !draft.IsClassicView) Centred("NETWORK GAMES PLAY CLASSIC", 112, 304, 324);
        Picture(7, VideoPanelOk.X, VideoPanelOk.Y);
        Picture(8, VideoPanelCancel.X, VideoPanelCancel.Y);
    }

    /// <summary>The port's VIDEO button in the in-game OPTIONS popup.</summary>
    private void DrawOptionsVideoButton(Graphics graphics)
    {
        if (_optionsDraft is null || _optionsArt is not { } art || art.Frames.Count <= 6) return;
        var palette = ScreenPalette("intrface");
        var image = art.Frames[6];
        var rgba = art.FrameRgba(6, palette: palette);
        if (_activeCanvas is { } canvas) canvas.DrawForeground(new GpuImage(image.Width, image.Height, rgba, transient: true), OptionsVideoButton.X, OptionsVideoButton.Y);
        else
        {
            using var bitmap = BitmapFromRgba(image.Width, image.Height, rgba);
            graphics.DrawImageUnscaled(bitmap, OptionsVideoButton.X, OptionsVideoButton.Y);
        }
        var font = LoadFont("mfonto7");
        var cell = font.Sprite.Frames[0].Width + 1;
        const string label = "VIDEO";
        DrawCellText(graphics, label, OptionsVideoButton.X + (OptionsVideoButton.Width - label.Length * cell) / 2, OptionsVideoButton.Y + 7, font, colour: 4, palette: "intrface");
    }
}
