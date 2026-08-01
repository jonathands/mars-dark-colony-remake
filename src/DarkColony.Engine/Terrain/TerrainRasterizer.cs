namespace DarkColony.Engine.Terrain;

public sealed record TerrainImage(int Width, int Height, byte[] Rgba);

public static class TerrainRasterizer
{
    public const int TileSize = 32;

    public static TerrainImage RenderViewport(
        TerrainMap map,
        BtsTileset tileset,
        int cameraPixelX,
        int cameraPixelY,
        int viewportWidth,
        int viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0) throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        var pixels = new byte[checked(viewportWidth * viewportHeight * 4)];
        var firstCellX = Math.Max(0, cameraPixelX / TileSize);
        var firstCellY = Math.Max(0, cameraPixelY / TileSize);
        var lastCellX = Math.Min(map.Width - 1, (cameraPixelX + viewportWidth - 1) / TileSize);
        var lastCellY = Math.Min(map.Height - 1, (cameraPixelY + viewportHeight - 1) / TileSize);

        for (var cellY = firstCellY; cellY <= lastCellY; cellY++)
        {
            for (var cellX = firstCellX; cellX <= lastCellX; cellX++)
            {
                var cell = map[cellX, cellY];
                PaintTile(tileset, cell.BaseTileId, cellX, cellY, cell.FlipBaseHorizontally, false);
                if (cell.OverlayTileId != 0)
                {
                    PaintTile(tileset, cell.OverlayTileId, cellX, cellY, cell.FlipOverlayHorizontally, true);
                }
            }
        }

        return new TerrainImage(viewportWidth, viewportHeight, pixels);

        void PaintTile(BtsTileset source, ushort tileId, int cellX, int cellY, bool flip, bool transparentZero)
        {
            if (!source.TilesById.TryGetValue(tileId, out var tile)) return;
            var targetOriginX = cellX * TileSize - cameraPixelX;
            var targetOriginY = cellY * TileSize - cameraPixelY;
            for (var sourceY = 0; sourceY < TileSize; sourceY++)
            {
                var targetY = targetOriginY + sourceY;
                if ((uint)targetY >= viewportHeight) continue;
                for (var targetXInTile = 0; targetXInTile < TileSize; targetXInTile++)
                {
                    var targetX = targetOriginX + targetXInTile;
                    if ((uint)targetX >= viewportWidth) continue;
                    var sourceX = flip ? TileSize - 1 - targetXInTile : targetXInTile;
                    var paletteIndex = tile.PaletteIndices[sourceY * TileSize + sourceX];
                    if (transparentZero && paletteIndex == 0) continue;
                    var color = source.Palette[paletteIndex];
                    var offset = (targetY * viewportWidth + targetX) * 4;
                    pixels[offset] = color.Red;
                    pixels[offset + 1] = color.Green;
                    pixels[offset + 2] = color.Blue;
                    pixels[offset + 3] = 255;
                }
            }
        }
    }
}
