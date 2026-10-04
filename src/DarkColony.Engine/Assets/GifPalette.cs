namespace DarkColony.Engine.Assets;

/// <summary>The global colour table of a shipped GIF (the interface pictures share one palette).</summary>
public static class GifPalette
{
    public static IReadOnlyList<VgaColor> Load(string path) => Parse(File.ReadAllBytes(path));

    public static IReadOnlyList<VgaColor> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 13 || (data[10] & 0x80) == 0) throw new InvalidDataException("GIF has no global color table.");
        var count = 2 << (data[10] & 7);
        if (data.Length < 13 + count * 3) throw new InvalidDataException("Truncated GIF color table.");
        var palette = new VgaColor[count];
        for (var index = 0; index < count; index++)
            palette[index] = new VgaColor(data[13 + index * 3], data[14 + index * 3], data[15 + index * 3]);
        return palette;
    }
}
