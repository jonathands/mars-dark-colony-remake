using DarkColony.Engine.World;

namespace DarkColony.Engine.Movement;

/// <summary>
/// Integer-domain equivalent of dc.exe 0x4413A0/0x4121A0 and the
/// 0x441504 direction-vector routine. One revolution is 256 facing units
/// (0x2000 native angle units).
/// </summary>
/// <remarks>
/// The executable reads two word tables: a quarter-wave sine at
/// <c>0x4796B8</c> (<c>trunc(2048 sin(i 2pi / 0x2000))</c>, i = 0..0x800) and
/// an arctangent at <c>0x47A6BA</c> (<c>trunc(atan(r / 256) 0x1000 / pi)</c>,
/// r = 0..255). The port rebuilds both with <see cref="decimal"/> series, so no
/// floating-point library result decides a truncation; a check compares
/// them entry by entry with the user's dc.exe.
/// </remarks>
public static class NativeBearing
{
    private const int NativeAngleCircle = 0x2000;
    private const int QuarterAngle = 0x800;

    private static readonly Lazy<short[]> QuarterSine = new(BuildQuarterSine);
    private static readonly Lazy<short[]> Arctangent = new(BuildArctangent);

    /// <summary>The rebuilt <c>0x4796B8</c> table (0x801 words).</summary>
    public static IReadOnlyList<short> QuarterSineTable => QuarterSine.Value;

    /// <summary>The rebuilt <c>0x47A6BA</c> table (256 words).</summary>
    public static IReadOnlyList<short> ArctangentTable => Arctangent.Value;

    public static byte Between(FixedPointPosition source, FixedPointPosition target) =>
        FromDelta(target.XRaw - source.XRaw, target.ZRaw - source.ZRaw);

    public static byte FromDelta(int x, int z)
    {
        if (x == 0 && z == 0) return 0;
        var absoluteX = Math.Abs((long)x);
        var absoluteZ = Math.Abs((long)z);
        int angle;
        if (absoluteZ > absoluteX)
        {
            var acute = Arctangent.Value[(int)(absoluteX * 256 / absoluteZ)];
            angle = x < 0
                ? z >= 0 ? acute + 0x800 : 0x1800 - acute
                : z < 0 ? acute + 0x1800 : 0x800 - acute;
        }
        else if (absoluteZ == absoluteX)
        {
            angle = x > 0
                ? z > 0 ? 0x400 : 0x1c00
                : z > 0 ? 0xc00 : 0x1400;
        }
        else
        {
            var acute = Arctangent.Value[(int)(absoluteZ * 256 / absoluteX)];
            angle = x < 0
                ? z >= 0 ? 0x1000 - acute : acute + 0x1000
                : z < 0 ? (NativeAngleCircle - acute) & 0x1fff : acute;
        }
        return unchecked((byte)(angle / 32));
    }

    /// <summary>
    /// <c>0x441504</c>: the quadrant picks the table entry and the signs, which
    /// apply after truncation (toward zero), for X = cos and Z = sin.
    /// </summary>
    public static NativeDirectionVector Vector(byte bearing)
    {
        var angle = bearing * 32 & (NativeAngleCircle - 1);
        var within = angle & (QuarterAngle - 1);
        var table = QuarterSine.Value;
        var low = table[within];
        var high = table[QuarterAngle - within];
        var (sine, cosine) = (angle >> 11) switch
        {
            0 => (low, high),
            1 => (high, (short)-low),
            2 => ((short)-low, (short)-high),
            _ => ((short)-high, low),
        };
        return new NativeDirectionVector(cosine, sine);
    }

    private const decimal Pi = 3.1415926535897932384626433833m;
    private const decimal SeriesEpsilon = 0.0000000000000000000000001m;
    private const decimal IntegerTolerance = 0.000000000000000001m;

    private static short[] BuildQuarterSine()
    {
        var table = new short[QuarterAngle + 1];
        for (var index = 0; index <= QuarterAngle; index++)
            table[index] = TruncateNearInteger(Sine(index * Pi / (NativeAngleCircle / 2)) * NativeDirectionVector.Scale);
        return table;
    }

    private static short[] BuildArctangent()
    {
        var table = new short[256];
        for (var ratio = 0; ratio < table.Length; ratio++)
            table[ratio] = TruncateNearInteger(Atan(ratio / 256m) * 0x1000 / Pi);
        return table;
    }

    /// <summary>Taylor series, for 0 &lt;= x &lt;= pi / 2.</summary>
    private static decimal Sine(decimal x)
    {
        var square = x * x;
        var term = x;
        var sum = x;
        for (var k = 1; Math.Abs(term) > SeriesEpsilon; k++)
        {
            term = -term * square / ((2 * k) * (2 * k + 1));
            sum += term;
        }
        return sum;
    }

    /// <summary>Euler's series, for 0 &lt;= x &lt;= 1: sum of 4^n (n!)^2 / (2n+1)! x^(2n+1) / (1+x^2)^(n+1).</summary>
    private static decimal Atan(decimal x)
    {
        if (x == 0) return 0;
        var ratio = x * x / (1 + x * x);
        var term = x / (1 + x * x);
        var sum = term;
        for (var n = 1; term > SeriesEpsilon; n++)
        {
            term = term * (2 * n) / (2 * n + 1) * ratio;
            sum += term;
        }
        return sum;
    }

    /// <summary>Truncates toward zero, treating a value within the series error of an integer as that integer.</summary>
    private static short TruncateNearInteger(decimal value)
    {
        var nearest = decimal.Round(value);
        return (short)(Math.Abs(value - nearest) < IntegerTolerance ? nearest : decimal.Truncate(value));
    }
}
