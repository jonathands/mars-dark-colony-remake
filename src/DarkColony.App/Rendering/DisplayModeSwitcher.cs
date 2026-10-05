using System.Runtime.InteropServices;
using DarkColony.Presentation;

namespace DarkColony.App.Rendering;

/// <summary>
/// Exclusive fullscreen as a temporary display mode change. DXGI's own
/// exclusive state needs a top-level swap chain window, and the port's
/// surface is a child control, so the port changes the monitor's mode with
/// <c>ChangeDisplaySettingsEx</c> (<c>CDS_FULLSCREEN</c>, never written to the
/// registry) and covers it with its borderless window. The desktop mode
/// comes back on <see cref="Restore"/>, and Windows restores it when the
/// process ends.
/// </summary>
internal static class DisplayModeSwitcher
{
    private const int EnumCurrentSettings = -1;
    private const int CdsFullscreen = 0x4;
    private const int DispChangeSuccessful = 0;
    private const int DmPelsWidth = 0x80000;
    private const int DmPelsHeight = 0x100000;
    private const int DmDisplayFrequency = 0x400000;

    /// <summary>The monitor holding a window: its device name and current bounds in physical pixels.</summary>
    public static (string DeviceName, Rectangle Bounds) MonitorOf(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("GetMonitorInfo failed.");
        return (info.DeviceName, Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom));
    }

    /// <summary>The monitor's desktop mode, as saved in the registry.</summary>
    public static DisplayModeChoice DesktopMode(string deviceName)
    {
        var mode = NewDevMode();
        if (!EnumDisplaySettings(deviceName, -2 /* ENUM_REGISTRY_SETTINGS */, ref mode)) return CurrentMode(deviceName);
        return new DisplayModeChoice(mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency);
    }

    /// <summary>The monitor's current mode.</summary>
    public static DisplayModeChoice CurrentMode(string deviceName)
    {
        var mode = NewDevMode();
        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode)) throw new InvalidOperationException($"EnumDisplaySettings({deviceName}) failed.");
        return new DisplayModeChoice(mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency);
    }

    /// <summary>
    /// The monitor's modes in its current colour depth, one per size with its
    /// highest refresh rate, 640x480 and up.
    /// </summary>
    public static IReadOnlyList<DisplayModeChoice> Modes(string deviceName)
    {
        var current = NewDevMode();
        EnumDisplaySettings(deviceName, EnumCurrentSettings, ref current);
        var modes = new List<DisplayModeChoice>();
        var mode = NewDevMode();
        for (var index = 0; EnumDisplaySettings(deviceName, index, ref mode); index++)
            if (mode.BitsPerPel == current.BitsPerPel && mode.PelsWidth >= 640 && mode.PelsHeight >= 480)
                modes.Add(new DisplayModeChoice(mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency));
        return modes.GroupBy(item => (item.Width, item.Height))
            .Select(group => group.MaxBy(item => item.RefreshHz))
            .OrderBy(item => item.Width).ThenBy(item => item.Height)
            .ToArray();
    }

    /// <summary>
    /// Switches the monitor to a size, at the requested refresh rate or the
    /// highest one available. Returns the mode set, or throws.
    /// </summary>
    public static DisplayModeChoice Set(string deviceName, DisplayModeChoice wanted)
    {
        var refresh = wanted.RefreshHz;
        if (refresh == 0)
            refresh = Modes(deviceName).FirstOrDefault(mode => mode.Width == wanted.Width && mode.Height == wanted.Height).RefreshHz;
        if (refresh == 0 && !Modes(deviceName).Any(mode => mode.Width == wanted.Width && mode.Height == wanted.Height))
            throw new InvalidOperationException($"{deviceName} has no {wanted.Width}x{wanted.Height} mode.");
        var mode = NewDevMode();
        mode.PelsWidth = wanted.Width;
        mode.PelsHeight = wanted.Height;
        mode.DisplayFrequency = refresh;
        mode.Fields = DmPelsWidth | DmPelsHeight | (refresh > 0 ? DmDisplayFrequency : 0);
        var result = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CdsFullscreen, IntPtr.Zero);
        if (result != DispChangeSuccessful)
            throw new InvalidOperationException($"ChangeDisplaySettingsEx({deviceName}, {wanted.Width}x{wanted.Height}@{refresh}) returned {result}.");
        return new DisplayModeChoice(wanted.Width, wanted.Height, refresh);
    }

    /// <summary>Returns the monitor to its desktop (registry) mode.</summary>
    public static void Restore(string deviceName) =>
        ChangeDisplaySettingsEx(deviceName, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

    private static DevMode NewDevMode() => new() { Size = (short)Marshal.SizeOf<DevMode>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInt
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public RectInt Monitor;
        public RectInt Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode mode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode mode, IntPtr window, int flags, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, IntPtr mode, IntPtr window, int flags, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}
