namespace DarkColony.Engine.Interface;

/// <summary>What a picture animation does on each step (the command the screen passes).</summary>
public enum PictureCommand
{
    Hold = 0,
    Forward = 1,
    Backward = 2,
}

/// <summary>
/// The executable's picture widget (<c>0x428A78</c> builds it, <c>0x428C3C</c>
/// runs it) as the encyclopedia uses it, in state 8. The SPR's frame 0 only
/// sets the box size (320x200); frames 1 to count - 1 are the pictures.
/// </summary>
/// <remarks>
/// <para>
/// Once more than the interval (0x21 ms) has passed since the last step, a
/// step moves the frame forward (wrapping from the last to 1) or backward
/// (wrapping from 1 to the last), or holds it. It then clears the box and draws
/// the frame. The widget starts on frame 1 with its last step at time 0, so
/// the first update already steps.
/// </para>
/// <para>
/// The encyclopedia (<c>0x4027FB</c>-<c>0x402905</c>) sets the command:
/// <list type="bullet">
/// <item><description>new entries start Forward;</description></item>
/// <item><description>holding LEFT gives Backward and holding RIGHT gives Forward, and either lasts after release;</description></item>
/// <item><description>the bare pushbuttons 9 and 10 give Hold and Forward.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class PictureAnimation
{
    public const int EncyclopediaInterval = 0x21;

    private long _lastStep;

    public PictureAnimation(int frameCount, int interval = EncyclopediaInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(interval);
        FrameCount = frameCount;
        Interval = interval;
    }

    public int FrameCount { get; }
    public int Interval { get; }
    public int Frame { get; private set; } = 1;
    public PictureCommand Command { get; set; } = PictureCommand.Forward;

    /// <summary>
    /// Runs the steps due by <paramref name="now"/> (milliseconds): one per
    /// <see cref="Interval"/> + 1 ms, as an unthrottled menu loop takes them.
    /// </summary>
    public void Advance(long now)
    {
        if (now - _lastStep <= Interval) return;
        // The first step comes at once; later ones keep a fixed period.
        var period = Interval + 1L;
        var due = _lastStep == 0 ? 1 : (now - _lastStep) / period;
        for (var step = 0L; step < Math.Min(due, FrameCount * 2L); step++) Step();
        _lastStep = _lastStep == 0 ? now : _lastStep + due * period;
    }

    public void Step()
    {
        switch (Command)
        {
            case PictureCommand.Forward:
                Frame = Frame + 1 == FrameCount ? 1 : Frame + 1;
                break;
            case PictureCommand.Backward:
                Frame = Frame - 1 == 0 ? FrameCount - 1 : Frame - 1;
                break;
        }
    }
}
