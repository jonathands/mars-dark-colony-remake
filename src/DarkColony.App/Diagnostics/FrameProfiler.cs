using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DarkColony.App.Diagnostics;

/// <summary>
/// Frame timing for <c>--perf</c> (or <c>DARKCOLONY_PERF=1</c>). Every two
/// seconds it writes one log line: how long the loop spent in each stage
/// (average and worst frame), how often the timer ran, how many sprites and
/// texture uploads a frame took, and what the garbage collector did. It costs
/// nothing when off.
/// </summary>
internal static class FrameProfiler
{
    public enum Stage
    {
        /// <summary>Simulation steps and the feedback each one collects.</summary>
        Simulation,
        /// <summary>The render callback: walking the game state into sprite commands.</summary>
        Build,
        /// <summary>The D3D draw calls, including texture uploads.</summary>
        Submit,
        /// <summary>The swap chain's present (vsync waits here).</summary>
        Present,
    }

    private const long ReportIntervalMilliseconds = 2000;
    private static readonly int StageCount = Enum.GetValues<Stage>().Length;
    private static readonly long[] StageTotal = new long[StageCount];
    private static readonly long[] StageWorst = new long[StageCount];
    private static readonly long[] StageFrame = new long[StageCount];
    private static long frames;
    private static long worstFrame;
    private static long frameStarted;
    private static bool frameOpen;
    private static long loopFrames;
    private static long lastFrameStart;
    private static long worstFrameGap;
    private static long steps;
    private static long sprites;
    private static long drawCalls;
    private static long waitTotal;
    private static readonly long[] waitOutcomes = new long[3];
    private static long uploads;
    private static long uploadBytes;
    private static long reportStarted;
    private static long allocatedAtReport;
    private static readonly int[] CollectionsAtReport = new int[3];

    public static bool Enabled { get; private set; }

    public static void Initialize(IReadOnlyList<string> arguments)
    {
        Enabled = arguments.Any(argument => argument.Equals("--perf", StringComparison.OrdinalIgnoreCase)) ||
            Environment.GetEnvironmentVariable("DARKCOLONY_PERF") == "1";
        if (!Enabled) return;
        reportStarted = Stopwatch.GetTimestamp();
        ResetGarbageBaseline();
        RuntimeLog.Info("Frame profiler on: one perf line every 2 s (times in ms, avg/worst frame).");
    }

    /// <summary>The loop began a frame; the longest gap between two shows a dropped frame.</summary>
    public static void FrameStarted()
    {
        if (!Enabled) return;
        var now = Stopwatch.GetTimestamp();
        if (lastFrameStart != 0) worstFrameGap = Math.Max(worstFrameGap, now - lastFrameStart);
        lastFrameStart = now;
        loopFrames++;
    }

    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void End(Stage stage, long started)
    {
        if (!Enabled) return;
        StageFrame[(int)stage] += Stopwatch.GetTimestamp() - started;
    }

    public static void Step()
    {
        if (Enabled) steps++;
    }

    public static void Sprites(int count)
    {
        if (Enabled) sprites += count;
    }

    /// <summary>A loop wait ended: 0 its handle, 1 input, 2 its timeout.</summary>
    public static void Waited(long started, int outcome)
    {
        if (!Enabled) return;
        waitTotal += Stopwatch.GetTimestamp() - started;
        waitOutcomes[outcome]++;
    }

    public static void DrawCalls(int count)
    {
        if (Enabled) drawCalls += count;
    }

    public static void Upload(int bytes)
    {
        if (!Enabled) return;
        uploads++;
        uploadBytes += bytes;
    }

    /// <summary>Opens a frame unless one is open: the timer opens it before its steps.</summary>
    public static void BeginFrame()
    {
        if (!Enabled || frameOpen) return;
        frameOpen = true;
        frameStarted = Stopwatch.GetTimestamp();
    }

    /// <summary>Closes the frame's stages and writes the report when it is due.</summary>
    public static void EndFrame()
    {
        if (!Enabled || !frameOpen) return;
        frameOpen = false;
        var now = Stopwatch.GetTimestamp();
        frames++;
        worstFrame = Math.Max(worstFrame, now - frameStarted);
        for (var stage = 0; stage < StageCount; stage++)
        {
            StageTotal[stage] += StageFrame[stage];
            StageWorst[stage] = Math.Max(StageWorst[stage], StageFrame[stage]);
            StageFrame[stage] = 0;
        }
        if (Milliseconds(now - reportStarted) < ReportIntervalMilliseconds) return;
        Report(now);
    }

    private static void Report(long now)
    {
        var seconds = Milliseconds(now - reportStarted) / 1000.0;
        var line = new StringBuilder(256);
        line.Append(CultureInfo.InvariantCulture, $"perf: {frames / seconds:0} fps, frame worst {Milliseconds(worstFrame):0.0}");
        foreach (var stage in Enum.GetValues<Stage>())
        {
            var index = (int)stage;
            line.Append(CultureInfo.InvariantCulture,
                $" | {stage.ToString().ToLowerInvariant()} {Milliseconds(StageTotal[index]) / Math.Max(1, frames):0.00}/{Milliseconds(StageWorst[index]):0.0}");
        }
        line.Append(CultureInfo.InvariantCulture, $" | steps {steps / seconds:0.0}/s, loop {loopFrames / seconds:0}/s gap worst {Milliseconds(worstFrameGap):0.0}");
        line.Append(CultureInfo.InvariantCulture, $" | wait {Milliseconds(waitTotal) / Math.Max(1, frames):0.00} (frame {waitOutcomes[0]}, input {waitOutcomes[1]}, timeout {waitOutcomes[2]})");
        line.Append(CultureInfo.InvariantCulture, $" | sprites {sprites / Math.Max(1, frames)}/frame in {drawCalls / Math.Max(1, frames)} draws, uploads {uploads} ({uploadBytes / 1024} KB)");
        var allocated = GC.GetTotalAllocatedBytes();
        line.Append(CultureInfo.InvariantCulture,
            $" | alloc {(allocated - allocatedAtReport) / 1024.0 / 1024.0 / seconds:0.0} MB/s, gc {GC.CollectionCount(0) - CollectionsAtReport[0]}/{GC.CollectionCount(1) - CollectionsAtReport[1]}/{GC.CollectionCount(2) - CollectionsAtReport[2]}, heap {GC.GetTotalMemory(false) / 1024 / 1024} MB");
        RuntimeLog.Info(line.ToString());

        Array.Clear(StageTotal);
        Array.Clear(StageWorst);
        frames = 0;
        worstFrame = 0;
        loopFrames = 0;
        worstFrameGap = 0;
        steps = 0;
        sprites = 0;
        drawCalls = 0;
        waitTotal = 0;
        Array.Clear(waitOutcomes);
        uploads = 0;
        uploadBytes = 0;
        reportStarted = now;
        ResetGarbageBaseline();
    }

    private static void ResetGarbageBaseline()
    {
        allocatedAtReport = GC.GetTotalAllocatedBytes();
        for (var generation = 0; generation < 3; generation++) CollectionsAtReport[generation] = GC.CollectionCount(generation);
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
