using DarkColony.Engine.World;

namespace DarkColony.Engine.Movement;

public enum CoarseRouteTermination
{
    ReachedTarget,
    ZeroSentinel,
    Cycle,
    StepLimit,
}

public sealed record CoarseRegionRoute(IReadOnlyList<byte> Regions, CoarseRouteTermination Termination);

/// <summary>
/// Decoded PTH route table and navigation-region grid. The loader at
/// <c>0x442B7C</c> reads region rows in file order into navigation rows
/// 0..height-1 without reversal, and SCN placements use the same world frame:
/// across the installed corpus, no placed ground unit stands on a region-0
/// cell in this orientation. (MAP tile rows, by contrast, are stored in screen
/// order, with world +Z pointing up the screen.)
/// </summary>
public sealed class PathRegionMap
{
    public const int RouteTableSize = 256 * 256;

    private PathRegionMap(int width, int height, byte[] routes, byte[] regions)
    {
        Width = width;
        Height = height;
        Routes = routes;
        Regions = regions;
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Routes { get; }
    /// <summary>Region bytes in file order: index <c>z * Width + x</c> in world coordinates.</summary>
    public ReadOnlyMemory<byte> Regions { get; }

    public static PathRegionMap Load(string path, int width, int height) => Parse(File.ReadAllBytes(path), width, height);

    public static PathRegionMap Parse(ReadOnlySpan<byte> data, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var expected = checked(RouteTableSize + width * height);
        if (data.Length != expected) throw new InvalidDataException($"PTH length is {data.Length}; expected {expected}.");
        return new PathRegionMap(width, height, data[..RouteTableSize].ToArray(), data[RouteTableSize..].ToArray());
    }

    public byte RegionAt(CellCoordinate cell)
    {
        if ((uint)cell.X >= (uint)Width || (uint)cell.Z >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(cell));
        return Regions.Span[cell.Z * Width + cell.X];
    }

    public byte NextRegion(byte sourceRegion, byte targetRegion) => Routes.Span[sourceRegion * 256 + targetRegion];

    public CoarseRegionRoute BuildCoarseRoute(byte sourceRegion, byte targetRegion)
    {
        var route = new List<byte> { sourceRegion };
        if (sourceRegion == targetRegion) return new CoarseRegionRoute(route, CoarseRouteTermination.ReachedTarget);
        var visited = new HashSet<byte> { sourceRegion };
        var current = sourceRegion;
        for (var step = 0; step < 256; step++)
        {
            current = NextRegion(current, targetRegion);
            route.Add(current);
            if (current == targetRegion) return new CoarseRegionRoute(route, CoarseRouteTermination.ReachedTarget);
            if (current == 0) return new CoarseRegionRoute(route, CoarseRouteTermination.ZeroSentinel);
            if (!visited.Add(current)) return new CoarseRegionRoute(route, CoarseRouteTermination.Cycle);
        }

        return new CoarseRegionRoute(route, CoarseRouteTermination.StepLimit);
    }
}
