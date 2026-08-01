namespace DarkColony.Engine.Simulation;

public sealed class FixedStepClock
{
    public const int NativeDefaultIntervalMilliseconds = 0x42;

    private long _accumulatedTimestamp;

    public FixedStepClock(long initialTimestamp, int intervalMilliseconds = NativeDefaultIntervalMilliseconds)
    {
        if (intervalMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        }

        _accumulatedTimestamp = initialTimestamp;
        IntervalMilliseconds = intervalMilliseconds;
    }

    public int IntervalMilliseconds { get; }

    public long AccumulatedTimestamp => _accumulatedTimestamp;

    public int Advance(long currentTimestamp, Action step, int maximumCatchUpSteps = 256)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (maximumCatchUpSteps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCatchUpSteps));
        }

        var elapsed = currentTimestamp - _accumulatedTimestamp;
        var executed = 0;
        while (elapsed > IntervalMilliseconds && executed < maximumCatchUpSteps)
        {
            _accumulatedTimestamp += IntervalMilliseconds;
            elapsed -= IntervalMilliseconds;
            step();
            executed++;
        }

        return executed;
    }
}
