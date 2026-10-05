using System.Diagnostics;
using System.Runtime.InteropServices;
using DarkColony.App.Diagnostics;

namespace DarkColony.App.Rendering;

/// <summary>
/// The game loop: runs one frame (simulation steps, then a render) whenever
/// the UI thread has no messages, paced by the display rather than a WinForms
/// timer. A WinForms timer fires at the system tick (15.6 ms), after every
/// paint and input message, so the old loop dropped a frame every couple of
/// seconds and could be starved by input.
/// <list type="bullet">
/// <item>With vsync, each frame first waits on the swap chain's frame latency
/// object (<see cref="Direct3DSurface.FrameWaitHandle"/>), which a one-frame
/// latency signals once per refresh: input is read just before the frame that
/// shows it, and the present does not block.</item>
/// <item>Without vsync, a high-resolution waitable timer caps the loop at
/// <see cref="UncappedFramesPerSecond"/>, so it does not spin a core.</item>
/// <item>Every wait also wakes for input, which is handled at once; and none
/// lasts longer than <see cref="LongestWaitMilliseconds"/>, so while the window
/// is minimized and nothing presents, the fixed-step clock still runs its
/// steps (several a frame, as it catches up).</item>
/// </list>
/// Modal loops (a dialog, dragging the window) stop the game, as in most games.
/// </summary>
internal sealed class GameLoop : IDisposable
{
    public const int UncappedFramesPerSecond = 240;
    // Longer than any refresh: a wait that times out while a frame is still
    // queued leaves its signal for later, and from then on every wait would
    // pass at once and the present would block a frame late instead.
    private const uint LongestWaitMilliseconds = 100;

    private readonly Action _frame;
    private readonly Direct3DSurface _surface;
    private readonly IntPtr _timer;
    private long _nextUncappedFrame;
    private bool _running;

    public GameLoop(Action frame, Direct3DSurface surface)
    {
        _frame = frame;
        _surface = surface;
        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION (Windows 10 1803); without it
        // an ordinary waitable timer, which rounds to the system tick.
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (_timer == IntPtr.Zero) _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
    }

    /// <summary>The loop's clock, in milliseconds: high resolution, unlike <see cref="Environment.TickCount64"/>.</summary>
    public static double Milliseconds => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    public void Start()
    {
        if (_running) return;
        _running = true;
        Application.Idle += OnIdle;
    }

    public void Dispose()
    {
        if (_running) Application.Idle -= OnIdle;
        _running = false;
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
    }

    private void OnIdle(object? sender, EventArgs eventArgs)
    {
        while (_running && !MessagePending())
        {
            if (!WaitForFrame()) return;
            _frame();
        }
    }

    /// <summary>Waits until the next frame is due. False when a message arrived first.</summary>
    private bool WaitForFrame()
    {
        if (!_surface.VSync && _timer != IntPtr.Zero)
        {
            var now = Stopwatch.GetTimestamp();
            var interval = Stopwatch.Frequency / UncappedFramesPerSecond;
            if (_nextUncappedFrame < now - interval) _nextUncappedFrame = now;
            if (_nextUncappedFrame > now)
            {
                // A relative due time, in 100 ns units, is negative.
                var due = -(_nextUncappedFrame - now) * 10_000_000 / Stopwatch.Frequency;
                SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                if (!Wait(_timer, LongestWaitMilliseconds)) return false;
            }
            _nextUncappedFrame += interval;
        }
        var frameHandle = _surface.FrameWaitHandle;
        return frameHandle == IntPtr.Zero || Wait(frameHandle, LongestWaitMilliseconds);
    }

    /// <summary>Waits for the handle, at most <paramref name="timeout"/> ms. False when input or a message came first.</summary>
    private static bool Wait(IntPtr handle, uint timeout)
    {
        var handles = new[] { handle };
        var started = FrameProfiler.Begin();
        var result = MsgWaitForMultipleObjectsEx(1, handles, timeout, QsAllInput, MwmoInputAvailable);
        FrameProfiler.Waited(started, result switch { WaitObject0 => 0, WaitObject0 + 1 => 1, _ => 2 });
        return result != WaitObject0 + 1;
    }

    private static bool MessagePending() => PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);

    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x4;
    private const uint WaitObject0 = 0;
    private const uint PmNoRemove = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Handle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
