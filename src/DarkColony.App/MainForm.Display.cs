using DarkColony.App.Diagnostics;
using DarkColony.App.Ui;
using DarkColony.Presentation;

namespace DarkColony.App;

/// <summary>
/// Display modes, a port enhancement (docs/DISPLAY_MODES_PLAN.md): the window
/// size and DPI, the size of the logical picture, and the pointer over a
/// scaled picture. The original only ran at 640x480; at window scale 1 with
/// the classic view the port draws exactly that.
/// </summary>
public sealed partial class MainForm
{
    private DisplaySettings _display = new();
    private bool _pointerInsideSurface;
    private Point? _menuPointer;

    /// <summary>The display settings in effect: the settings file plus command-line overrides.</summary>
    public DisplaySettings DisplaySettings
    {
        get => _display;
        init => _display = value.Sanitized();
    }

    /// <summary>Where changed display settings are saved; none keeps them for this run only.</summary>
    public string? DisplaySettingsPath { get; init; }

    protected override void OnLoad(EventArgs eventArgs)
    {
        base.OnLoad(eventArgs);
        _surface.PictureLayoutChanged += (_, _) =>
        {
            RuntimeLog.Info($"Presentation: {_surface.PictureLayout}");
            SetGameplayCursorVisibility(visible: _screen != MenuScreenId.Gameplay);
        };
        ApplyDisplaySettings(_display);
        RuntimeLog.Info($"Display: {_display.Mode}, window scale {(_display.WindowScale == 0 ? "auto" : _display.WindowScale)}, " +
            $"{_display.Scale} scaling, view {_display.View}, vsync {(_display.VSync ? "on" : "off")}, DPI {DeviceDpi}.");
        RuntimeLog.Info($"Presentation: {_surface.PictureLayout}");
    }

    private void ApplyDisplaySettings(DisplaySettings settings)
    {
        _display = settings.Sanitized();
        _surface.ScaleMode = _display.Scale;
        _surface.VSync = _display.VSync;
        _surface.LogicalSize = CurrentLogicalSize();
        ApplyWindowMode();
    }

    private void ApplyWindowMode()
    {
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        ClientSize = WindowedClientSize();
    }

    /// <summary>
    /// Windowed play shows the classic picture times the window scale. The
    /// automatic scale follows the monitor's DPI (150% gives 2x); every
    /// scale is limited to what fits the monitor's work area.
    /// </summary>
    private Size WindowedClientSize()
    {
        var work = Screen.FromControl(this).WorkingArea;
        var frame = SizeFromClientSize(Size.Empty);
        var classic = DisplaySettings.ClassicSize;
        var fit = Math.Max(1, Math.Min((work.Width - frame.Width) / classic.Width, (work.Height - frame.Height) / classic.Height));
        var wanted = _display.WindowScale > 0
            ? _display.WindowScale
            : (int)Math.Round(DeviceDpi / 96.0, MidpointRounding.AwayFromZero);
        var scale = Math.Clamp(wanted, 1, fit);
        if (scale != wanted) RuntimeLog.Info($"Window scale {wanted} does not fit {work.Width}x{work.Height}; using {scale}.");
        return new Size(classic.Width * scale, classic.Height * scale);
    }

    /// <summary>
    /// Native DPI: Windows would resize the window by the DPI ratio when it
    /// moves to another monitor. The port keeps the picture's physical size
    /// instead, unless the window scale is automatic, which follows the DPI.
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        base.OnDpiChanged(eventArgs);
        if (_display.Mode != WindowMode.Windowed || WindowState != FormWindowState.Normal) return;
        eventArgs.Cancel = true;
        var location = eventArgs.SuggestedRectangle.Location;
        BeginInvoke(() =>
        {
            Location = location;
            ClientSize = WindowedClientSize();
            RuntimeLog.Info($"DPI {eventArgs.DeviceDpiOld} -> {eventArgs.DeviceDpiNew}.");
        });
    }

    /// <summary>The size the next frame is drawn at.</summary>
    private Size CurrentLogicalSize() => DisplaySettings.ClassicSize;

    /// <summary>
    /// Menus are drawn with the game's own cursor when the picture is not
    /// shown 1:1, so the cursor scales with it. At 1:1 they keep the system
    /// cursor, as before.
    /// </summary>
    private bool UsesSoftwareMenuCursor => _surface.PictureLayout.IntegerScale != 1;

    private void DrawMenuCursor(Graphics graphics)
    {
        if (!UsesSoftwareMenuCursor || _menuPointer is not { } pointer || !_pointerInsideSurface) return;
        var animation = Animation("curs.fin", "DEFAULT");
        if (animation is null) return;
        var frame = NativeFrame("curs.fin", animation.FirstFrame, animation.LastFrame, _world.TickCount - _screenStartedAtTick);
        if (AnimationBitmap("curs.fin", frame) is not { } bitmap) return;
        var origin = AnimationOrigin("curs.fin", frame);
        if (_activeCanvas is { } canvas) canvas.DrawForeground(GpuBitmap(bitmap), pointer.X + origin.X, pointer.Y + origin.Y);
        else graphics.DrawImageUnscaled(bitmap, pointer.X + origin.X, pointer.Y + origin.Y);
    }
}
