using System.Drawing;

namespace DarkColony.Presentation;

/// <summary>
/// The gameplay screen at a given logical size. The original HUD
/// (<c>intrface.gif</c> and <c>maine</c>) is 640x480. A larger view keeps its
/// parts and anchors them to the edges: everything right of
/// <see cref="SplitX"/> moves right with the width, everything below
/// <see cref="SplitY"/> moves down with the height. The HUD picture fills
/// the gap by repeating plain bands of itself: columns 200-399 (top border,
/// black view and message strip) and rows 342-391 (left border, black view
/// and the empty command column).
/// </summary>
public sealed record GameplayScreen
{
    /// <summary>Points at or right of this column follow the right edge.</summary>
    public const int SplitX = 400;

    /// <summary>
    /// Points at or below this row follow the bottom edge. It lies after the
    /// last command row (358-398) and before the identity strip (404).
    /// </summary>
    public const int SplitY = 392;

    /// <summary>The repeated columns: the top border and message strip are plain across them.</summary>
    public const int BandLeft = 200;

    /// <summary>
    /// The repeated rows: in <c>intrface.gif</c> rows 342-391 of the right
    /// panel are identical, and the view and left border are plain there.
    /// </summary>
    public const int BandTop = 342;

    /// <summary>The classic gameplay screen.</summary>
    public static GameplayScreen Classic { get; } = new(DisplaySettings.ClassicSize);

    public GameplayScreen(Size size)
    {
        if (size.Width < 640 || size.Height < 480)
            throw new ArgumentOutOfRangeException(nameof(size), size, "The gameplay screen is at least 640x480.");
        Size = size;
    }

    public Size Size { get; }

    /// <summary>How much wider and taller than 640x480 the screen is.</summary>
    public Size Extra => new(Size.Width - 640, Size.Height - 480);

    public bool IsClassic => Extra.IsEmpty;

    /// <summary>
    /// The world view: (4,6) 512x448 at 640x480 (<c>0x41ec1e</c>), growing
    /// with the screen.
    /// </summary>
    public Rectangle Viewport => new(4, 6, 512 + Extra.Width, 448 + Extra.Height);

    /// <summary>
    /// The area that takes world clicks and is drawn as world: the view with
    /// the frame left and above it, (0,0) 516x458 at 640x480.
    /// </summary>
    public Size WorldArea => new(516 + Extra.Width, 458 + Extra.Height);

    /// <summary>The screen pixel at the centre of the view: (260,230) at 640x480.</summary>
    public Point ViewCentre => new(Viewport.X + Viewport.Width / 2, Viewport.Y + Viewport.Height / 2);

    /// <summary>Where a 640x480 popup (OPTIONS, the Video panel) is drawn: centred.</summary>
    public Point PopupOffset => new(Extra.Width / 2, Extra.Height / 2);

    /// <summary>A point of the classic HUD moved to this screen.</summary>
    public Point Anchor(Point classic) => new(
        classic.X + (classic.X >= SplitX ? Extra.Width : 0),
        classic.Y + (classic.Y >= SplitY ? Extra.Height : 0));

    /// <summary>A classic HUD rectangle moved with its top-left corner; its size is kept.</summary>
    public Rectangle Anchor(Rectangle classic) => new(Anchor(classic.Location), classic.Size);

    /// <summary>The <c>intrface.gif</c> column a screen column shows.</summary>
    public int SourceColumn(int x) => Source(x, SplitX, BandLeft, Extra.Width);

    /// <summary>The <c>intrface.gif</c> row a screen row shows.</summary>
    public int SourceRow(int y) => Source(y, SplitY, BandTop, Extra.Height);

    // Before the split: itself. After the inserted stretch: shifted back.
    // In the stretch: the band, phased so that its last column meets the split.
    private static int Source(int value, int split, int bandStart, int extra)
    {
        if (value < split) return value;
        if (value >= split + extra) return value - extra;
        var period = split - bandStart;
        var offset = ((value - split - extra) % period + period) % period;
        return bandStart + offset;
    }
}
