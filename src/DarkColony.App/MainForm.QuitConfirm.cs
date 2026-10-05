using DarkColony.App.Diagnostics;
using DarkColony.App.Rendering;
using DarkColony.App.Ui;
using DarkColony.Engine.Assets;

namespace DarkColony.App;

/// <summary>
/// The quit confirmation, <c>intrface/lqce</c>: "REALLY QUIT?" with
/// YES, QUIT and NO, CONTINUE. The original opens it (<c>0x4329E8</c>) from the
/// options tab's QUIT (control 62, <c>0x43368D</c>) and the Q key
/// (<c>0x40A444</c>); the port opens it for Esc as well. While it is open in a
/// single-player game the world waits, as with OPTIONS.
/// </summary>
public sealed partial class MainForm
{
    // lqce: pushb 56 (YES, popp frame 7) and 57 (NO, frame 8).
    private static readonly Rectangle QuitYesButton = new(344, 220, 32, 32);
    private static readonly Rectangle QuitNoButton = new(344, 268, 32, 32);

    private Dictionary<int, string>? _quitText;
    private bool _quitConfirmOpen;
    // The current mouse press went to a popup (MainForm.Input.cs).
    private bool _popupPress;

    private void OpenQuitConfirm()
    {
        if (_installation is null) return;
        try
        {
            _quitText ??= MenuText(_installation.DataFile("intrface", "lqce"));
            _optionsArt ??= Sprite.Load(_installation.DataFile("intrface", "popp.spr"));
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Quit confirmation error: {error.Message}";
            return;
        }
        _quitConfirmOpen = true;
        _optionsDraft = null;
        ClearTransientInputState();
        RuntimeLog.Info("Quit confirmation opened.");
    }

    private void HandleQuitConfirmClick(Point point)
    {
        if (QuitYesButton.Contains(point))
        {
            _quitConfirmOpen = false;
            RuntimeLog.Info("Quit confirmed.");
            // 0x4329C6 sets +0x13A: leaving a War is a defeat (stat (0,0) = 8, 0x40A9FB).
            if (_selectedScenario?.WarLaunch is not null && !_missionOutcomeReported && _scenarioSimulation is not null) EndWar(victory: false);
            else ShowScreen(MenuScreenId.Main);
        }
        else if (QuitNoButton.Contains(point)) CloseQuitConfirm();
    }

    /// <summary>While the confirmation is open it takes the keys: Esc continues, Enter quits.</summary>
    private bool HandleQuitConfirmKey(Keys key)
    {
        if (!_quitConfirmOpen) return false;
        if (key == Keys.Escape) CloseQuitConfirm();
        else if (key == Keys.Enter) HandleQuitConfirmClick(QuitYesButton.Location);
        return true;
    }

    private void CloseQuitConfirm()
    {
        _quitConfirmOpen = false;
        RuntimeLog.Info("Quit confirmation closed.");
    }

    private void DrawQuitConfirm(Graphics graphics)
    {
        if (!_quitConfirmOpen || _optionsArt is not { } art) return;
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

        // lqce's pictures: the panel's top, strips every 16 pixels and bottom,
        // the title box and the two label boxes.
        Picture(0, 112, 160);
        for (var y = 176; y < 320; y += 16) Picture(1, 112, y);
        Picture(2, 112, 320);
        Picture(6, 208, 168);
        Picture(6, 144, 224);
        Picture(6, 144, 272);
        Picture(7, QuitYesButton.X, QuitYesButton.Y);
        Picture(8, QuitNoButton.X, QuitNoButton.Y);

        // Labels 60-62 in font 0, remap 4: the title centred, the answers left.
        var font = LoadFont("mfonto7");
        var cell = font.Sprite.Frames[0].Width + 1;
        string Text(int id, string fallback) => _quitText?.GetValueOrDefault(id) ?? fallback;
        var title = Text(1, "REALLY QUIT?");
        DrawCellText(graphics, title, 212 + (106 - title.Length * cell) / 2, 176, font, colour: 4, palette: "intrface");
        DrawCellText(graphics, Text(2, "YES, QUIT"), 148, 232, font, colour: 4, palette: "intrface");
        DrawCellText(graphics, Text(3, "NO, CONTINUE"), 148, 280, font, colour: 4, palette: "intrface");
    }
}
