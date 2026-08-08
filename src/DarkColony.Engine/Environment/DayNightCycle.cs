namespace DarkColony.Engine.Time;

public enum DayNightPhase { Day, Night }

/// <summary>
/// Deterministic environment clock. Entity catalogs contain independent day and
/// night observation values; native phase duration remains an evidence target.
/// </summary>
public sealed class DayNightCycle
{
    // Provisional 60-second half-cycle at the recovered 66ms simulation step.
    // Kept as an explicit rule so an executable-recovered duration replaces one value.
    public const int DefaultTicksPerPhase = 900;

    public DayNightCycle(int ticksPerPhase = DefaultTicksPerPhase)
    {
        if (ticksPerPhase <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerPhase));
        TicksPerPhase = ticksPerPhase;
    }

    public int TicksPerPhase { get; }
    public ulong ElapsedTicks { get; private set; }
    /// <summary>Completed full day/night pairs, suitable for the original HUD days counter.</summary>
    public ulong CompletedDays => ElapsedTicks / ((ulong)TicksPerPhase * 2);
    public DayNightPhase Phase => (ElapsedTicks / (ulong)TicksPerPhase) % 2 == 0 ? DayNightPhase.Day : DayNightPhase.Night;

    public bool Step()
    {
        var before = Phase;
        ElapsedTicks++;
        return before != Phase;
    }
}
