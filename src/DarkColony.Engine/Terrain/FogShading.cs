namespace DarkColony.Engine.Terrain;

/// <summary>
/// How the terrain pass (<c>0x453B94</c>) shades the fog of war (vision.md).
/// Each cell of the view, and a one-cell margin, gets a brightness out of 16
/// (<c>ScenarioSimulation.ViewBrightness</c>). A tile corner is the
/// mean of the four cells around it, rounded down (<c>sar 2</c>). A table of
/// 17 x 17 ramps of 32 steps (<c>0x51462C</c>, built by <c>0x4539F0</c>)
/// first gives the tile's left and right edges from its corners, top to
/// bottom, and then each row between them, left to right. Every terrain
/// pixel then reads the colour remap at its brightness, so 16 leaves it, 10
/// darkens it to 10/16 and 0 makes it black.
/// </summary>
public static class FogShading
{
    /// <summary>A cell in the team's current sight.</summary>
    public const int InSight = 16;

    /// <summary>A cell the team has seen but does not see now (<c>mov edx, 0xA</c> at <c>0x453BAE</c>).</summary>
    public const int OutOfSight = 10;

    /// <summary>A cell the team has never seen (grid bit 31 clear).</summary>
    public const int Unexplored = 0;

    public const int TileSize = 32;

    /// <summary>A tile corner from the four cells that meet there.</summary>
    public static int Corner(int a, int b, int c, int d) => (a + b + c + d) >> 2;

    /// <summary>Step <paramref name="step"/> (0-31) of the table's ramp from one brightness to another.</summary>
    public static int Ramp(int from, int to, int step) => (from * (TileSize - 1 - step) + to * step) / (TileSize - 1);

    /// <summary>Fills a 32 x 32 tile, row by row, with the brightness of each pixel.</summary>
    public static void Fill(Span<byte> tile, int topLeft, int topRight, int bottomLeft, int bottomRight)
    {
        if (tile.Length < TileSize * TileSize) throw new ArgumentException("The tile needs 32 x 32 entries.", nameof(tile));
        for (var row = 0; row < TileSize; row++)
        {
            var left = Ramp(topLeft, bottomLeft, row);
            var right = Ramp(topRight, bottomRight, row);
            for (var column = 0; column < TileSize; column++)
                tile[row * TileSize + column] = (byte)Ramp(left, right, column);
        }
    }
}
