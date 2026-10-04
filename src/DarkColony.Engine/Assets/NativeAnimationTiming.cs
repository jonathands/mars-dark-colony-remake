namespace DarkColony.Engine.Assets;

/// <summary>How an animation stepper ends its sequence (state byte <c>+6</c>).</summary>
public enum NativeAnimationMode
{
    /// <summary>Back to the first frame, which then holds for its own ticks.</summary>
    Loop,
    /// <summary>Mode 1: stops after the last frame (the state turns to 2, finished).</summary>
    Once,
    /// <summary>Mode 3: keeps showing the last frame.</summary>
    Hold,
}

/// <summary>
/// The executable's animation clock. The frame loader (<c>0x425674</c>)
/// turns each FIN frame delay <c>d</c> (0 meaning 15) into a byte of
/// <c>(d + 3) * 15 / 100</c> ticks. The stepper (<c>0x4264C8</c>) runs once
/// per update: when its countdown is zero it moves to the next frame and
/// reloads the countdown with that frame's ticks, then decrements it (a zero
/// byte wraps, so such a frame lasts 256 updates). It starts on the first
/// frame with a zero countdown, so the first step already shows frame 1.
/// </summary>
public static class NativeAnimationTiming
{
    /// <summary>The updates a frame with FIN delay <paramref name="delay"/> lasts (1-256).</summary>
    public static int FrameTicks(ushort delay)
    {
        var ticks = (byte)(((delay == 0 ? 15 : delay) + 3) * 15 / 100);
        return ticks == 0 ? 256 : ticks;
    }

    /// <summary>The ticks of each logical frame from <paramref name="first"/> to <paramref name="last"/>.</summary>
    public static int[] FrameTicks(AnimationDefinition definition, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var ticks = new int[Math.Max(0, last - first + 1)];
        for (var index = 0; index < ticks.Length; index++)
            ticks[index] = first + index < definition.LogicalFrames.Count ? FrameTicks(definition.LogicalFrames[first + index].Delay) : FrameTicks(0);
        return ticks;
    }

    /// <summary>
    /// The frame (0-based within the sequence) shown after <paramref name="steps"/>
    /// stepper runs, or -1 once a <see cref="NativeAnimationMode.Once"/> sequence is finished.
    /// </summary>
    public static int FrameAt(IReadOnlyList<int> frameTicks, ulong steps, NativeAnimationMode mode = NativeAnimationMode.Loop)
    {
        ArgumentNullException.ThrowIfNull(frameTicks);
        var count = frameTicks.Count;
        if (count == 0 || steps == 0) return 0;
        // First pass: frame f >= 1 shows for steps (S[f-1], S[f]], S[0] = 0.
        ulong elapsed = 0;
        for (var frame = 1; frame < count; frame++)
        {
            elapsed += (ulong)frameTicks[frame];
            if (steps <= elapsed) return frame;
        }
        switch (mode)
        {
            case NativeAnimationMode.Once:
                return -1;
            case NativeAnimationMode.Hold:
                return count - 1;
        }
        // Later passes start on frame 0, which now holds for its own ticks.
        ulong cycle = 0;
        foreach (var ticks in frameTicks) cycle += (ulong)ticks;
        var offset = (steps - elapsed - 1) % cycle;
        for (var frame = 0; frame < count; frame++)
        {
            if (offset < (ulong)frameTicks[frame]) return frame;
            offset -= (ulong)frameTicks[frame];
        }
        return count - 1;
    }
}
