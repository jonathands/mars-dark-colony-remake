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

    private byte[][]? regionNeighbors;

    /// <summary>
    /// The neighbor list the PTH loader builds after the route table
    /// (<c>0x442D1F</c>, 32 bytes per region at map <c>+0x984A8</c>): for regions
    /// 1-254, the distinct nonzero <see cref="NextRegion"/> values toward targets
    /// 1-254, in order of first appearance (at most 31). Regions 0 and 255 have none.
    /// </summary>
    public IReadOnlyList<byte> RegionNeighbors(byte region)
    {
        if (regionNeighbors is null)
        {
            var lists = new byte[256][];
            var routes = Routes.Span;
            var found = new List<byte>(32);
            for (var source = 0; source < 256; source++)
            {
                found.Clear();
                if (source is > 0 and < 255)
                    for (var target = 1; target < 255; target++)
                    {
                        var next = routes[source * 256 + target];
                        if (next == 0 || found.Contains(next)) continue;
                        // The loader asserts the list never reaches 32 entries.
                        if (found.Count == 31) throw new InvalidDataException($"PTH region {source} has more than 31 neighbors.");
                        found.Add(next);
                    }
                lists[source] = [.. found];
            }
            regionNeighbors = lists;
        }
        return regionNeighbors[region];
    }

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
