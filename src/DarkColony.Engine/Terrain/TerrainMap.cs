using System.Buffers.Binary;

namespace DarkColony.Engine.Terrain;

public readonly record struct TerrainCell(
    ushort BaseTileId,
    ushort OverlayTileId,
    byte Flags,
    byte Ambient)
{
    public bool FlipBaseHorizontally => (Flags & 0x20) != 0;
    public bool FlipOverlayHorizontally => (Flags & 0x40) != 0;
    public int AmbientType => (Ambient >> 1) & 0x1f;
    public int AmbientVariant => Ambient & 1;
    public byte SourceFlags => (byte)(Ambient & 0xc0);
}

public sealed class TerrainMap
{
    private TerrainMap(int width, int height, IReadOnlyList<TerrainCell> cells)
    {
        Width = width;
        Height = height;
        Cells = cells;
    }

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<TerrainCell> Cells { get; }

    public TerrainCell this[int x, int y]
    {
        get
        {
            if ((uint)x >= Width || (uint)y >= Height) throw new ArgumentOutOfRangeException();
            return Cells[y * Width + x];
        }
    }

    public static TerrainMap Load(string path) => Parse(File.ReadAllBytes(path));

    public static TerrainMap Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) throw new InvalidDataException("MAP is shorter than its dimensions.");
        var width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data));
        var height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
        if (width <= 0 || height <= 0) throw new InvalidDataException("MAP dimensions must be positive.");
        var count = checked(width * height);
        var expected = checked(8 + count * 6);
        if (data.Length != expected) throw new InvalidDataException($"MAP length is {data.Length}; expected {expected}.");

        var attributeOffset = 8 + count * 4;
        var cells = new TerrainCell[count];
        for (var index = 0; index < count; index++)
        {
            var tileOffset = 8 + index * 4;
            cells[index] = new TerrainCell(
                BinaryPrimitives.ReadUInt16LittleEndian(data[tileOffset..]),
                BinaryPrimitives.ReadUInt16LittleEndian(data[(tileOffset + 2)..]),
                data[attributeOffset + index * 2],
                data[attributeOffset + index * 2 + 1]);
        }

        return new TerrainMap(width, height, cells);
    }
}
