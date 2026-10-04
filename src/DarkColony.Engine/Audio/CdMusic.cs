namespace DarkColony.Engine.Audio;

/// <summary>
/// dc.exe's CD music, which drives MCI <c>cdaudio</c> through
/// <c>0x42F7F0</c>-<c>0x42F962</c>. The menu (<c>0x404BA7</c>) only opens the
/// drive. Gameplay setup (<c>0x41EF4F</c> to <c>0x42F894</c>) sets TMSF time,
/// seeks to <see cref="FirstTrack"/> and plays without an end point, so the
/// disc runs through its last track. The music keeps going outside gameplay
/// until it ends. Quit (<c>0x404D4D</c>) stops it.
/// </summary>
public static class CdMusic
{
    public const int FirstTrack = 2;

    /// <summary>The audio a pass plays, in order: <see cref="FirstTrack"/> through the last audio track.</summary>
    public static IReadOnlyList<CueTrack> Pass(CueSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        return [.. sheet.AudioTracks.Where(track => track.Number >= FirstTrack).OrderBy(track => track.Number)];
    }
}

/// <summary>
/// The gameplay loop's drive poll (<c>0x431EF6</c>). Once more than
/// <see cref="PollMilliseconds"/> have passed since the last poll (the time
/// starts at 0, so the first loop polls), it asks the drive for its mode.
/// A stopped drive, or a failed request, is closed, reopened and played
/// again from track 2 (<c>0x42F8B8</c>).
/// </summary>
public sealed class CdMusicPoll
{
    public const int PollMilliseconds = 5000;

    private long _lastPoll;

    /// <summary>Whether the loop polls at <paramref name="now"/> (milliseconds); a poll restarts the interval.</summary>
    public bool Due(long now)
    {
        if (now - _lastPoll <= PollMilliseconds) return false;
        _lastPoll = now;
        return true;
    }
}
