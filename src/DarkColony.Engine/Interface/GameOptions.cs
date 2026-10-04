using System.Buffers.Binary;
using DarkColony.Engine.Data;

namespace DarkColony.Engine.Interface;

/// <summary>
/// The in-game OPTIONS popup (<c>intrface/lopte</c>; opened at
/// <c>0x432D50</c>, handled by <c>0x432B00</c>). Each pair of arrow buttons
/// steps one setting within its limits:
/// <list type="bullet">
/// <item><description>40/41: game speed, 10-200 % in steps of 10;</description></item>
/// <item><description>42/43: sound volume, 0-10, applied at once (mixer wave volume);</description></item>
/// <item><description>67/68: CD-ROM volume, 0-10, applied at once (<c>auxSetVolume</c>, v x 0x1800 per channel, <c>0x452BA4</c>);</description></item>
/// <item><description>44/45: game detail, 0-2 (LOW, MEDIUM, HIGH).</description></item>
/// </list>
/// Button 56 commits. Button 55 closes, and the volumes it already applied stay.
/// The speed is the world update interval, <c>0x19C8 / speed</c> ms
/// (100 % = 66 ms). Opening rounds the current interval back down to a multiple of 10 %.
/// </summary>
public sealed record GameOptions(int SpeedPercent, int SoundVolume, int CdVolume, int Detail)
{
    /// <summary>dc.exe's defaults, four ints: detail, sound volume, CD volume, update interval (2, 5, 5, 66).</summary>
    public const uint DefaultsAddress = 0x478CC4;
    public const int IntervalNumerator = 0x19C8;
    public const int MinimumSpeed = 10;
    public const int MaximumSpeed = 200;
    public const int MaximumVolume = 10;
    public const int MaximumDetail = 2;

    public static GameOptions Load(string executablePath) => FromImage(PeImage.Load(executablePath));

    public static GameOptions FromImage(PeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var data = image.AtVirtualAddress(DefaultsAddress, 16).ToArray();
        int Read(int index) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(index * 4));
        return new GameOptions(SpeedFromInterval(Read(3)), Read(1), Read(2), Read(0));
    }

    /// <summary><c>0x432D8C</c>: the speed shown for an update interval.</summary>
    public static int SpeedFromInterval(int intervalMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMilliseconds);
        return IntervalNumerator / intervalMilliseconds / 10 * 10;
    }

    /// <summary><c>0x432B47</c>: the world update interval this speed commits.</summary>
    public int UpdateIntervalMilliseconds => IntervalNumerator / SpeedPercent;

    /// <summary>The CD audio volume word for one channel (<c>0x452BEA</c>).</summary>
    public static int AuxVolume(int level) => Math.Clamp(level, 0, MaximumVolume) * 0x1800;

    public GameOptions Slower() => SpeedPercent > MinimumSpeed ? this with { SpeedPercent = SpeedPercent - 10 } : this;
    public GameOptions Faster() => SpeedPercent < MaximumSpeed ? this with { SpeedPercent = SpeedPercent + 10 } : this;
    public GameOptions Quieter() => SoundVolume > 0 ? this with { SoundVolume = SoundVolume - 1 } : this;
    public GameOptions Louder() => SoundVolume < MaximumVolume ? this with { SoundVolume = SoundVolume + 1 } : this;
    public GameOptions CdQuieter() => CdVolume > 0 ? this with { CdVolume = CdVolume - 1 } : this;
    public GameOptions CdLouder() => CdVolume < MaximumVolume ? this with { CdVolume = CdVolume + 1 } : this;
    public GameOptions LessDetail() => Detail > 0 ? this with { Detail = Detail - 1 } : this;
    public GameOptions MoreDetail() => Detail < MaximumDetail ? this with { Detail = Detail + 1 } : this;
}
