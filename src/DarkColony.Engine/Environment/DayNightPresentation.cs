namespace DarkColony.Engine.Time;

/// <summary>
/// How the original shows the time of day (<c>docs/reverse-engineering/day-night.md</c>):
/// the tint of the ground and the HUD clock.
/// </summary>
public static class DayNightPresentation
{
    /// <summary>The colours of the remap the night tint runs through.</summary>
    public const int NightColours = 8;

    /// <summary>
    /// The colour part of the terrain remap, <c>(lighting * 7) &gt;&gt; 8</c>
    /// (<c>0x40AC6F</c>): 0 in full day, 7 at night. Colour c moves each
    /// ground colour toward its grey, about c/11 of the way.
    /// </summary>
    public static int TerrainColour(int lightingLevel) => Math.Clamp(lightingLevel, 0, 256) * 7 >> 8;

    /// <summary>
    /// The HUD clock's frame (<c>0x43A9F8</c>, <c>sprites/cloc.spr</c>). Day
    /// runs through the first half of the frames and night through the
    /// second, one frame per <c>cycle limit / half</c> ticks (a float), the
    /// quotient truncated (<c>0x42B63A</c>). The last tick of a phase would
    /// reach the next half, so it is held one frame back.
    /// </summary>
    public static int ClockFrame(DayNightCycle cycle, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 2);
        var half = frameCount / 2;
        var night = cycle.Phase == DayNightPhase.Night;
        if (cycle.CycleTickLimit <= 0) return night ? half : 0;
        var ticksPerFrame = (float)cycle.CycleTickLimit / half;
        var frame = (int)Math.Truncate(cycle.PhaseTicks / (double)ticksPerFrame + (night ? half : 0));
        var end = night ? frameCount : half;
        return Math.Clamp(frame == end ? end - 1 : frame, 0, frameCount - 1);
    }
}
