using System.Diagnostics;
using System.Runtime.InteropServices;
using DarkColony.App.Diagnostics;
using DarkColony.App.Rendering;
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
    // The window's bounds while windowed, restored when fullscreen ends.
    private Rectangle? _windowedBounds;
    // Alt+Enter goes back to the last fullscreen kind used.
    private WindowMode _lastFullscreenMode = WindowMode.Borderless;
    private Rectangle _cursorClip;
    // The monitor whose mode exclusive fullscreen changed, until it is restored.
    private string? _exclusiveDevice;
    // The gameplay screen: classic, or a larger view with the HUD anchored.
    private GameplayScreen _gameplayScreen = GameplayScreen.Classic;

    /// <summary>The display settings in effect: the settings file plus command-line overrides.</summary>
    public DisplaySettings DisplaySettings
    {
        get => _display;
        init => _display = value.Sanitized();
    }

    /// <summary>Where changed display settings are saved; none keeps them for this run only.</summary>
    public string? DisplaySettingsPath { get; init; }

    /// <summary>
    /// Diagnostic (<c>--display-cycle N</c>): switches windowed → borderless →
    /// exclusive → windowed N times, logging live resources at each step,
    /// then closes. It checks that mode switches leak nothing.
    /// </summary>
    public int? DisplayCycles { get; init; }

    protected override void OnLoad(EventArgs eventArgs)
    {
        base.OnLoad(eventArgs);
        _surface.PictureLayoutChanged += (_, _) =>
        {
            RuntimeLog.Info($"Presentation: {_surface.PictureLayout}");
            SetGameplayCursorVisibility(visible: _screen != MenuScreenId.Gameplay);
            UpdateCursorClip();
        };
        // An automatic gameplay view follows the output's size.
        _surface.SizeChanged += (_, _) => UpdateGameplayScreen();
        if (_display.Mode != WindowMode.Windowed) _lastFullscreenMode = _display.Mode;
        // Exclusive fullscreen needs a visible window: start borderless and
        // let OnShown enter it.
        var requested = _display;
        ApplyDisplaySettings(requested.Mode == WindowMode.Exclusive ? requested with { Mode = WindowMode.Borderless } : requested);
        _display = requested;
        RuntimeLog.Info($"Display: {_display.Mode}, window scale {(_display.WindowScale == 0 ? "auto" : _display.WindowScale)}, " +
            $"{_display.Scale} scaling, view {_display.View}, vsync {(_display.VSync ? "on" : "off")}, DPI {DeviceDpi}.");
        RuntimeLog.Info($"Presentation: {_surface.PictureLayout}");
    }

    private void ApplyDisplaySettings(DisplaySettings settings)
    {
        _display = settings.Sanitized();
        _surface.ScaleMode = _display.Scale;
        _surface.VSync = _display.VSync;
        ApplyWindowMode();
        UpdateGameplayScreen();
    }

    /// <summary>
    /// Picks the gameplay screen from the settings and the output, and the
    /// logical size of the current screen. Network games always play the
    /// classic 640x480 (the user's choice: nobody sees more of the map than
    /// the others). The camera keeps its centre when the view changes.
    /// </summary>
    private void UpdateGameplayScreen()
    {
        var wanted = IsNetworkGame || InNetworkLobby
            ? GameplayScreen.Classic
            : new GameplayScreen(_display.GameplayViewFor(_surface.ClientSize));
        if (wanted != _gameplayScreen)
        {
            var before = _gameplayScreen.WorldArea;
            var after = wanted.WorldArea;
            _cameraX += (before.Width - after.Width) / 2;
            _cameraY += (before.Height - after.Height) / 2;
            _gameplayScreen = wanted;
            _gameplayHudLayout = GameplayHudLayout.Load(_installation, wanted);
            ClampGameplayCamera();
            RuntimeLog.Info($"Gameplay view {wanted.Size.Width}x{wanted.Size.Height}.");
        }
        _surface.LogicalSize = CurrentLogicalSize();
    }

    /// <summary>Where 640x480 popups sit on the current screen.</summary>
    private Point PopupOffset => _screen == MenuScreenId.Gameplay ? _gameplayScreen.PopupOffset : Point.Empty;

    private static Point Unshift(Point point, Point offset) => new(point.X - offset.X, point.Y - offset.Y);

    /// <summary>The world part of the gameplay screen: 516x458 at 640x480.</summary>
    private Size GameplayWorldArea => _gameplayScreen.WorldArea;

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        if (_display.Mode == WindowMode.Exclusive && _exclusiveDevice is null) ApplyWindowMode();
        if (DisplayCycles is { } cycles) StartDisplayCycle(cycles);
    }

    private void ApplyWindowMode()
    {
        var screen = Screen.FromControl(this);
        if (_display.Mode != WindowMode.Exclusive) RestoreDisplayMode();
        if (_display.Mode != WindowMode.Windowed && FormBorderStyle != FormBorderStyle.None && WindowState == FormWindowState.Normal)
            _windowedBounds = Bounds;
        switch (_display.Mode)
        {
            case WindowMode.Windowed:
                FormBorderStyle = FormBorderStyle.Sizable;
                MaximizeBox = true;
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                if (_windowedBounds is { } bounds) Bounds = bounds;
                ClientSize = WindowedClientSize();
                if (_windowedBounds is null) CenterToScreen();
                break;
            case WindowMode.Borderless:
                FormBorderStyle = FormBorderStyle.None;
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                Bounds = screen.Bounds;
                break;
            case WindowMode.Exclusive:
                FormBorderStyle = FormBorderStyle.None;
                if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
                var device = _exclusiveDevice ?? DisplayModeSwitcher.MonitorOf(Handle).DeviceName;
                var wanted = _display.ExclusiveMode ?? DisplayModeSwitcher.DesktopMode(device);
                try
                {
                    var mode = DisplayModeSwitcher.Set(device, wanted);
                    _exclusiveDevice = device;
                    Bounds = DisplayModeSwitcher.MonitorOf(Handle).Bounds;
                    RuntimeLog.Info($"Exclusive fullscreen {mode} on {device}.");
                }
                catch (InvalidOperationException error)
                {
                    // A failed mode falls back to a window and says why.
                    RuntimeLog.Info($"Exclusive fullscreen {wanted} failed: {error.Message} Falling back to windowed.");
                    _status = $"Exclusive fullscreen {wanted} failed; windowed.";
                    _display = _display with { Mode = WindowMode.Windowed };
                    ApplyWindowMode();
                    return;
                }
                break;
        }
        UpdateCursorClip();
    }

    /// <summary>Alt+Enter: windowed ↔ the last fullscreen kind. Saved like a Video panel change.</summary>
    private void ToggleFullscreen()
    {
        var next = _display.Mode == WindowMode.Windowed ? _lastFullscreenMode : WindowMode.Windowed;
        ChangeDisplaySettings(_display with { Mode = next });
    }

    /// <summary>Applies new display settings and saves them.</summary>
    private void ChangeDisplaySettings(DisplaySettings settings)
    {
        var previous = _display;
        if (settings.Mode != WindowMode.Windowed) _lastFullscreenMode = settings.Mode;
        ApplyDisplaySettings(settings);
        RuntimeLog.Info($"Display: {previous.Mode} -> {_display.Mode}; {_surface.ResourceSummary()}.");
        // The leak-test cycle must not overwrite the player's settings.
        if (DisplaySettingsPath is null || DisplayCycles is not null) return;
        try
        {
            _display.Save(DisplaySettingsPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Info($"Display settings not saved: {error.Message}");
        }
    }

    /// <summary>Gives the monitor its desktop mode back.</summary>
    private void RestoreDisplayMode()
    {
        if (_exclusiveDevice is not { } device) return;
        _exclusiveDevice = null;
        DisplayModeSwitcher.Restore(device);
        RuntimeLog.Info($"Display mode of {device} restored.");
    }

    // Exclusive fullscreen gives the desktop its mode back while the game is
    // in the background, and takes the monitor again when it returns.
    protected override void OnActivated(EventArgs eventArgs)
    {
        base.OnActivated(eventArgs);
        if (_display.Mode == WindowMode.Exclusive && _exclusiveDevice is null && WindowState == FormWindowState.Normal) ApplyWindowMode();
        UpdateCursorClip();
    }

    protected override void OnDeactivate(EventArgs eventArgs)
    {
        base.OnDeactivate(eventArgs);
        if (_exclusiveDevice is not null && DisplayCycles is null)
        {
            RestoreDisplayMode();
            WindowState = FormWindowState.Minimized;
        }
        UpdateCursorClip();
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        if (_display.Mode == WindowMode.Exclusive && _exclusiveDevice is null && WindowState == FormWindowState.Normal && ActiveForm == this)
            ApplyWindowMode();
    }

    protected override void OnLocationChanged(EventArgs eventArgs)
    {
        base.OnLocationChanged(eventArgs);
        UpdateCursorClip();
    }

    /// <summary>
    /// Keeps the pointer on the picture during play while the window has the
    /// focus: by default in fullscreen, otherwise as the settings say.
    /// The 3-pixel scroll edges are then reachable without leaving the window.
    /// </summary>
    private void UpdateCursorClip()
    {
        var confine = ActiveForm == this && WindowState != FormWindowState.Minimized && Visible &&
            _screen == MenuScreenId.Gameplay && _display.ConfinesCursor;
        var clip = confine ? _surface.PictureScreenBounds() : Rectangle.Empty;
        if (clip == _cursorClip) return;
        _cursorClip = clip;
        Cursor.Clip = clip;
    }

    private void StartDisplayCycle(int cycles)
    {
        WindowMode[] order = [WindowMode.Borderless, WindowMode.Exclusive, WindowMode.Windowed];
        var step = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick += (_, _) =>
        {
            if (step == cycles * order.Length)
            {
                timer.Dispose();
                RuntimeLog.Info($"Display cycle done: {DisplayResources()}.");
                Close();
                return;
            }
            var mode = order[step % order.Length];
            ChangeDisplaySettings(_display with { Mode = mode });
            step++;
            RuntimeLog.Info($"Display cycle {step}/{cycles * order.Length}: {mode} (mode changed: {_exclusiveDevice is not null}), client {ClientSize.Width}x{ClientSize.Height}, {DisplayResources()}.");
        };
        RuntimeLog.Info($"Display cycle start: {DisplayResources()}.");
        timer.Start();
    }

    private string DisplayResources()
    {
        using var process = Process.GetCurrentProcess();
        return $"{_surface.ResourceSummary()}, {process.HandleCount} handles, " +
            $"{GetGuiResources(process.Handle, 0)} GDI / {GetGuiResources(process.Handle, 1)} USER objects, " +
            $"{process.PrivateMemorySize64 / (1024 * 1024)} MB private";
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

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

    /// <summary>The size the next frame is drawn at: the gameplay view on the gameplay screen, 640x480 elsewhere.</summary>
    private Size CurrentLogicalSize() => _screen == MenuScreenId.Gameplay && _video is null ? _gameplayScreen.Size : DisplaySettings.ClassicSize;

    /// <summary>
    /// Menus are drawn with the game's own cursor when the picture is not
    /// shown 1:1, so the cursor scales with it. At 1:1 they keep the system
    /// cursor, as before.
    /// </summary>
    private bool UsesSoftwareMenuCursor => _surface.PictureLayout.IntegerScale != 1;

    private void DrawMenuCursor(Graphics graphics)
    {
        if (!UsesSoftwareMenuCursor || _menuPointer is not { } pointer || !_pointerInsideSurface) return;
        DrawCursor(graphics, "DEFAULT", pointer);
    }
}
