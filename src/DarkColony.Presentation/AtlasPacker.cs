namespace DarkColony.Presentation;

/// <summary>Where a packed image lies: its atlas page and the top-left of its pixels there.</summary>
public readonly record struct AtlasSlot(int Page, int X, int Y);

/// <summary>
/// Shelf packing of sprite images into square atlas pages, so the renderer
/// can draw a frame's sprites from a few textures in a few draw calls instead
/// of one call per sprite. Each page is a stack of shelves; an image goes on
/// the first shelf of the first page with room for it, else on a new shelf,
/// else on a new page. Every image keeps a transparent <see cref="Gutter"/>
/// around it, so no sampling can reach a neighbour. Images larger than
/// <see cref="LargestPacked"/> on a side are not packed; they keep a texture of their own.
/// </summary>
public sealed class AtlasPacker
{
    public const int Gutter = 1;
    // Shelf heights round up to this, so images of similar heights share shelves.
    private const int ShelfStep = 4;

    private readonly List<Page> pages = [];

    public AtlasPacker(int pageSize, int largestPacked)
    {
        if (largestPacked <= 0 || largestPacked + 2 * Gutter > pageSize)
            throw new ArgumentOutOfRangeException(nameof(largestPacked), largestPacked, "A packed image must fit on a page with its gutter.");
        PageSize = pageSize;
        LargestPacked = largestPacked;
    }

    public int PageSize { get; }
    public int LargestPacked { get; }
    public int PageCount => pages.Count;

    /// <summary>Packs an image, opening a page when none has room. False when it is too large to pack.</summary>
    public bool TryPack(int width, int height, out AtlasSlot slot)
    {
        slot = default;
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height}", "An image must not be empty.");
        if (width > LargestPacked || height > LargestPacked) return false;
        var cellWidth = width + 2 * Gutter;
        var cellHeight = height + 2 * Gutter;
        var shelfHeight = (cellHeight + ShelfStep - 1) / ShelfStep * ShelfStep;
        for (var index = 0; index <= pages.Count; index++)
        {
            if (index == pages.Count) pages.Add(new Page());
            var page = pages[index];
            foreach (var shelf in page.Shelves)
            {
                // A shelf takes images up to its height, but not much shorter ones,
                // which would waste most of their slot.
                if (shelf.Height < cellHeight || shelf.Height > shelfHeight * 2 || PageSize - shelf.NextX < cellWidth) continue;
                slot = new AtlasSlot(index, shelf.NextX + Gutter, shelf.Y + Gutter);
                shelf.NextX += cellWidth;
                return true;
            }
            if (page.NextShelfY + shelfHeight > PageSize) continue;
            var opened = new Shelf(page.NextShelfY, shelfHeight) { NextX = cellWidth };
            page.Shelves.Add(opened);
            page.NextShelfY += shelfHeight;
            slot = new AtlasSlot(index, Gutter, opened.Y + Gutter);
            return true;
        }
        throw new InvalidOperationException("Unreachable: a new page always has room.");
    }

    /// <summary>Forgets every packed image. The pages stay counted, ready to be refilled from the first.</summary>
    public void Reset()
    {
        foreach (var page in pages)
        {
            page.Shelves.Clear();
            page.NextShelfY = 0;
        }
    }

    private sealed class Page
    {
        public List<Shelf> Shelves { get; } = [];
        public int NextShelfY { get; set; }
    }

    private sealed class Shelf(int y, int height)
    {
        public int Y { get; } = y;
        public int Height { get; } = height;
        public int NextX { get; set; }
    }
}
