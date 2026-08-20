using DarkColony.Engine.World;

namespace DarkColony.Engine.Movement;

/// <summary>
/// Integer-domain equivalent of dc.exe 0x4413A0/0x4121A0 and the
/// 0x441504 direction-vector table. One revolution is 256 facing units.
/// </summary>
public static class NativeBearing
{
    private const int NativeAngleCircle = 0x2000;

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
            var ratio = (int)(absoluteX * 256 / absoluteZ);
            var acute = NativeAtan(ratio);
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
            var ratio = (int)(absoluteZ * 256 / absoluteX);
            var acute = NativeAtan(ratio);
            angle = x < 0
                ? z >= 0 ? 0x1000 - acute : acute + 0x1000
                : z < 0 ? (NativeAngleCircle - acute) & 0x1fff : acute;
        }
        return unchecked((byte)(angle / 32));
    }

    public static NativeDirectionVector Vector(byte bearing)
    {
        var radians = (bearing * 32) * Math.Tau / NativeAngleCircle;
        // The shipped 0x4796B8 quarter-wave table truncates toward zero.
        return new NativeDirectionVector(
            checked((short)(Math.Cos(radians) * NativeDirectionVector.Scale)),
            checked((short)(Math.Sin(radians) * NativeDirectionVector.Scale)));
    }

    private static int NativeAtan(int ratio) =>
        (int)(Math.Atan(ratio / 256.0) * 4096 / Math.PI);
}
