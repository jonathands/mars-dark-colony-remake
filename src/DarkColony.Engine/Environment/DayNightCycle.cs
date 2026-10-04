using DarkColony.Engine.Scenario;

namespace DarkColony.Engine.Time;

public enum DayNightPhase { Day, Night }

/// <summary>
/// Deterministic environment clock. The mission header owns the native phase,
/// cycle limit, initial counter, and lighting-transition length.
/// </summary>
public sealed class DayNightCycle
{
    // Retained only for callers that construct a synthetic clock. Real SCNs
    // provide all four values through FromNativeScenario.
    public const int DefaultTicksPerPhase = 900;
    private readonly int ticksBeforeTransition;
    private readonly int transitionTicks;
    private int phaseTicks;
    private ulong completedDays;

    public DayNightCycle(int ticksPerPhase = DefaultTicksPerPhase)
        : this(ticksPerPhase - 1, 0, DayNightPhase.Day, ticksPerPhase)
    {
    }

    private DayNightCycle(int ticksBeforeTransition, int initialTicks, DayNightPhase phase, int transitionTicks)
    {
        if (ticksBeforeTransition < 0) throw new ArgumentOutOfRangeException(nameof(ticksBeforeTransition));
        if (initialTicks < 0 || initialTicks > ticksBeforeTransition) throw new ArgumentOutOfRangeException(nameof(initialTicks));
        if (transitionTicks <= 0) throw new ArgumentOutOfRangeException(nameof(transitionTicks));
        this.ticksBeforeTransition = ticksBeforeTransition;
        this.transitionTicks = transitionTicks;
        phaseTicks = initialTicks;
        Phase = phase;
    }

    /// <summary>Creates the world clock used by the original SCN loader.</summary>
    public static DayNightCycle FromNativeScenario(ScenarioDayNight dayNight) => new(
        dayNight.CycleTickLimit,
        dayNight.InitialTick,
        dayNight.InitialPhase == 0 ? DayNightPhase.Day : DayNightPhase.Night,
        dayNight.TransitionTickLimit);

    /// <summary>Compatibility duration for synthetic clocks.</summary>
    public int TicksPerPhase => ticksBeforeTransition + 1;
    public int PhaseTicks => phaseTicks;
    /// <summary>Native <c>world + 0x534</c>: the SCN cycle-tick limit.</summary>
    public int CycleTickLimit => ticksBeforeTransition;
    /// <summary>
    /// Native world field <c>+0x540</c>: an 8.8 lighting ramp over the first
    /// configured transition ticks. It remains at its end value afterward.
    /// </summary>
    public int LightingLevel => phaseTicks > transitionTicks
        ? (Phase == DayNightPhase.Day ? 0 : 256)
        : Phase == DayNightPhase.Day
            ? 256 - phaseTicks * 256 / transitionTicks
            : phaseTicks * 256 / transitionTicks;
    public ulong ElapsedTicks { get; private set; }
    /// <summary>Completed full day/night pairs, suitable for the original HUD days counter.</summary>
    public ulong CompletedDays => completedDays;
    public DayNightPhase Phase { get; private set; }

    public bool Step()
    {
        ElapsedTicks++;
        phaseTicks++;
        // dc.exe 0x4199a9 uses a strict limit comparison: it flips only once
        // the incremented counter is greater than SCN's cycle-tick value.
        if (phaseTicks <= ticksBeforeTransition) return false;
        phaseTicks = 0;
        Phase = Phase == DayNightPhase.Day ? DayNightPhase.Night : DayNightPhase.Day;
        if (Phase == DayNightPhase.Day) completedDays++;
        return true;
    }
}
