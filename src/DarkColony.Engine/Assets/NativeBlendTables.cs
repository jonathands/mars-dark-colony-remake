namespace DarkColony.Engine.Assets;

/// <summary>
/// One sprite colour of a translucent draw type, as a GPU blend can apply
/// it: the screen colour becomes <c>colour + screen * Multiplier / 255</c>.
/// </summary>
public readonly record struct NativeBlend(byte Red, byte Green, byte Blue, byte Multiplier)
{
    /// <summary>Leaves the screen as it is: what a transparent pixel does.</summary>
    public static NativeBlend Identity => new(0, 0, 0, 255);
}

/// <summary>
/// The world blitter's shadow and translucent FIN draw types (<c>fin-layers.md</c>).
/// Draw type 4 (<c>0x462468</c>) writes <c>table[sprite * 256 + screen]</c>
/// from the first 64 KB table that <c>0x44F200</c> loads from the tileset's
/// <c>.rmp</c> (the colour/brightness remap: brightness <c>sprite &gt;&gt; 3</c>,
/// colour <c>sprite &amp; 7</c>, so it darkens). Draw type 5 (<c>0x462444</c>)
/// adds 0x10000 to the table pointer first and reads the second table, whose
/// rows 32-79 are glow ramps (32 leaves the screen, 33-47 whiten it, 48-75
/// tint it red to yellow, 76-79 make it white). Draw type 2 is a shadow
/// (<c>0x4618C0</c>/<c>0x461D14</c> via <c>0x4603A7</c>): every opaque pixel of the
/// sprite reads row 0x48 of the first table, colour 0 at brightness 9, so the
/// ground darkens to 9/16; type 1 draws that shadow and then the sprite.
/// </summary>
/// <remarks>
/// The port's frame is RGB, not palette indices, so each row becomes the
/// least-squares fit of <c>colour + m * screen</c> (one m for all channels,
/// both clamped to 0-255) over the 256 colours of the tileset's palette.
/// The fit is the approximation; the tables are the user's own data.
/// </remarks>
public sealed class NativeBlendTables
{
    public const int TableSize = 0x10000;

    /// <summary>The row of the first table a shadow pixel reads (<c>mov ah, 0x48</c> at <c>0x460458</c>).</summary>
    public const int ShadowRow = 0x48;
    public const int FileSize = 3 * TableSize;
    private readonly NativeBlend[][] _rows;

    private NativeBlendTables(NativeBlend[][] rows) => _rows = rows;

    public static NativeBlendTables Load(string rmpPath, IReadOnlyList<VgaColor> palette) =>
        FromTables(File.ReadAllBytes(rmpPath), palette);

    public static NativeBlendTables FromTables(ReadOnlySpan<byte> tables, IReadOnlyList<VgaColor> palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (tables.Length < 2 * TableSize) throw new InvalidDataException($"A .rmp file holds {FileSize} bytes; this one has {tables.Length}.");
        if (palette.Count < 256) throw new InvalidDataException("The blend tables need a 256-colour palette.");
        var rows = new NativeBlend[2][];
        for (var table = 0; table < 2; table++)
        {
            rows[table] = new NativeBlend[256];
            for (var sprite = 0; sprite < 256; sprite++)
                rows[table][sprite] = Fit(tables.Slice(table * TableSize + sprite * 256, 256), palette);
        }
        return new NativeBlendTables(rows);
    }

    /// <summary>Whether a FIN draw type blends with the screen instead of covering it: the shadow (2) and the translucent types (4, 5).</summary>
    public static bool IsBlended(int drawType) => drawType is 2 or 4 or 5;

    /// <summary>The blend a sprite colour of draw type 2, 4 or 5 applies; index 0 is transparent.</summary>
    public NativeBlend For(int drawType, byte spriteIndex)
    {
        if (!IsBlended(drawType)) throw new ArgumentOutOfRangeException(nameof(drawType), drawType, "Only draw types 2, 4 and 5 blend.");
        if (spriteIndex == 0) return NativeBlend.Identity;
        return drawType == 2 ? _rows[0][ShadowRow] : _rows[drawType - 4][spriteIndex];
    }

    private static NativeBlend Fit(ReadOnlySpan<byte> row, IReadOnlyList<VgaColor> palette)
    {
        var screenMean = new double[3];
        var outMean = new double[3];
        for (var screen = 0; screen < 256; screen++)
        {
            var (s, o) = (palette[screen], palette[row[screen]]);
            screenMean[0] += s.Red; screenMean[1] += s.Green; screenMean[2] += s.Blue;
            outMean[0] += o.Red; outMean[1] += o.Green; outMean[2] += o.Blue;
        }
        for (var channel = 0; channel < 3; channel++)
        {
            screenMean[channel] /= 256;
            outMean[channel] /= 256;
        }
        double covariance = 0, variance = 0;
        for (var screen = 0; screen < 256; screen++)
        {
            var (s, o) = (palette[screen], palette[row[screen]]);
            ReadOnlySpan<double> sc = [s.Red - screenMean[0], s.Green - screenMean[1], s.Blue - screenMean[2]];
            ReadOnlySpan<double> oc = [o.Red - outMean[0], o.Green - outMean[1], o.Blue - outMean[2]];
            for (var channel = 0; channel < 3; channel++)
            {
                covariance += sc[channel] * oc[channel];
                variance += sc[channel] * sc[channel];
            }
        }
        var multiplier = Math.Clamp(variance == 0 ? 0 : covariance / variance, 0, 1);
        byte Channel(int channel) => (byte)Math.Round(Math.Clamp(outMean[channel] - multiplier * screenMean[channel], 0, 255));
        return new NativeBlend(Channel(0), Channel(1), Channel(2), (byte)Math.Round(multiplier * 255));
    }
}
