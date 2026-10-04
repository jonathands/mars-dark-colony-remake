using DarkColony.Engine.Data;

namespace DarkColony.Engine.Assets;

/// <summary>
/// The colour/brightness remap that interface glyphs and sprites pass
/// through, as dc16.exe builds it at <c>0x44F34C</c> (dc.exe keeps the same
/// tables as palette indices in a <c>.rmp</c> file). A draw call names a
/// colour (0-7) and a brightness (0-31, <see cref="NormalBrightness"/> is
/// unchanged); the dc16 blitters <c>0x44FEA4</c>/<c>0x450028</c> index the table
/// at <c>colour * 0x200 + brightness * 0x1000</c>.
/// </summary>
/// <remarks>
/// <para>
/// Palette indices 138-143 are the font ramp: the colour selects one of
/// eight six-entry ramps, <c>index - 6 * (7 - colour)</c>, so colour 7 keeps
/// the cyan 138-143, colour 2 is yellow and colour 0 is red (96-101). Colour 5
/// takes its entries from a byte table instead (dc.exe <c>0x47BF86</c>, read
/// from the user's executable). Every other index keeps its own colour,
/// blended toward its luminance <c>3r + 6g + b</c> (tenths) by
/// <c>colour / 11</c>.
/// </para>
/// <para>
/// Each channel is then scaled by <c>brightness / 16</c> and capped at 255.
/// dc16.exe packs the result into its 16-bit surface, so a native capture
/// shows (140,12,8) where this returns (139,15,15).
/// </para>
/// </remarks>
public sealed class NativeColourRemap
{
    /// <summary>dc.exe's colour-5 table, indexed by palette index; dc16.exe has it at <c>0x48BFCE</c>.</summary>
    public const uint DimRampTableAddress = 0x47BF86;
    public const int NormalBrightness = 16;
    public const int Colours = 8;
    public const int Brightnesses = 32;

    private const int FontRampFirst = 0x8A;
    private const int FontRampLength = 6;
    private readonly byte[] _dimRamp;

    private NativeColourRemap(byte[] dimRamp) => _dimRamp = dimRamp;

    /// <summary>The colour-5 sources of font ramp entries 138-143.</summary>
    public IReadOnlyList<byte> DimRamp => _dimRamp;

    public static NativeColourRemap Load(string executablePath) => FromImage(PeImage.Load(executablePath));

    public static NativeColourRemap FromImage(PeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new NativeColourRemap(image.AtVirtualAddress(DimRampTableAddress + FontRampFirst, FontRampLength).ToArray());
    }

    public VgaColor Map(IReadOnlyList<VgaColor> palette, byte index, int colour, int brightness)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentOutOfRangeException.ThrowIfNegative(colour);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(colour, Colours);
        ArgumentOutOfRangeException.ThrowIfNegative(brightness);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(brightness, Brightnesses);

        int red, green, blue;
        if (index - FontRampFirst is >= 0 and < FontRampLength)
        {
            var source = colour == 5
                ? _dimRamp[index - FontRampFirst]
                : index - FontRampLength * (Colours - 1 - colour);
            var entry = Entry(palette, source);
            (red, green, blue) = (entry.Red, entry.Green, entry.Blue);
        }
        else
        {
            var entry = Entry(palette, index);
            var luminance = (3 * entry.Red + 6 * entry.Green + entry.Blue) * colour;
            var keep = 11 - colour;
            red = (10 * entry.Red * keep + luminance) / 110;
            green = (10 * entry.Green * keep + luminance) / 110;
            blue = (10 * entry.Blue * keep + luminance) / 110;
        }

        return new VgaColor(Scale(red, brightness), Scale(green, brightness), Scale(blue, brightness));
    }

    private static VgaColor Entry(IReadOnlyList<VgaColor> palette, int index) =>
        index < palette.Count ? palette[index] : default;

    private static byte Scale(int channel, int brightness) => (byte)Math.Min(255, channel * brightness / NormalBrightness);
}
