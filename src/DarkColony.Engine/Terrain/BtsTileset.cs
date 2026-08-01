using System.Buffers.Binary;
using DarkColony.Engine.Assets;

namespace DarkColony.Engine.Terrain;

public sealed record TerrainTile(uint FrameId, byte[] PaletteIndices)
{
    public const int Width = 32;
    public const int Height = 32;
}

public sealed class BtsTileset
{
    private const int HeaderSize = 776;
    private const int TileRecordSize = 1028;

    private BtsTileset(
        uint headerValue,
        IReadOnlyList<VgaColor> palette,
        IReadOnlyList<TerrainTile> tiles,
        IReadOnlyDictionary<uint, TerrainTile> tilesById)
    {
        HeaderValue = headerValue;
        Palette = palette;
        Tiles = tiles;
        TilesById = tilesById;
    }

    public uint HeaderValue { get; }
    public IReadOnlyList<VgaColor> Palette { get; }
    public IReadOnlyList<TerrainTile> Tiles { get; }
    public IReadOnlyDictionary<uint, TerrainTile> TilesById { get; }

    public static BtsTileset Load(string path) => Parse(File.ReadAllBytes(path));

    public static BtsTileset Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new InvalidDataException("BTS is shorter than its header and palette.");
        var headerValue = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var tileCount = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        var expected = checked(HeaderSize + tileCount * TileRecordSize);
        if (data.Length != expected) throw new InvalidDataException($"BTS length is {data.Length}; expected {expected}.");

        var palette = new VgaColor[256];
        for (var index = 0; index < palette.Length; index++)
        {
            var offset = 8 + index * 3;
            palette[index] = new VgaColor(
                Sprite.ExpandVgaChannel(data[offset]),
                Sprite.ExpandVgaChannel(data[offset + 1]),
                Sprite.ExpandVgaChannel(data[offset + 2]));
        }

        var tiles = new TerrainTile[tileCount];
        var byId = new Dictionary<uint, TerrainTile>(tileCount);
        for (var index = 0; index < tileCount; index++)
        {
            var offset = HeaderSize + index * TileRecordSize;
            var tile = new TerrainTile(
                BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]),
                data.Slice(offset + 4, TerrainTile.Width * TerrainTile.Height).ToArray());
            if (!byId.TryAdd(tile.FrameId, tile)) throw new InvalidDataException($"BTS contains duplicate frame ID {tile.FrameId}.");
            tiles[index] = tile;
        }

        return new BtsTileset(headerValue, palette, tiles, byId);
    }
}
