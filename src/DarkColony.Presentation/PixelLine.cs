using System.Drawing;

namespace DarkColony.Presentation;

/// <summary>
/// A thick Bresenham line as one horizontal span per row. The line is the
/// union of a <c>thickness</c>-pixel square at each step; along a straight
/// path each row of that union is one unbroken run, so the spans cover every
/// pixel once. Drawn as filled rectangles, a translucent line blends each
/// pixel once and needs no texture of its own.
/// </summary>
public static class PixelLine
{
    public static IReadOnlyList<Rectangle> Spans(Point start, Point end, int thickness = 1)
    {
        if (thickness <= 0) throw new ArgumentOutOfRangeException(nameof(thickness), thickness, "A line must be at least one pixel thick.");
        var minX = Math.Min(start.X, end.X);
        var minY = Math.Min(start.Y, end.Y);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var width = Math.Abs(dx) + thickness;
        var height = Math.Abs(dy) + thickness;
        var rowLeft = new int[height];
        var rowRight = new int[height];
        Array.Fill(rowLeft, int.MaxValue);
        Array.Fill(rowRight, int.MinValue);
        // The walk in the line's own box: from the corner nearest the start.
        var x = dx < 0 ? width - thickness : 0;
        var y = dy < 0 ? height - thickness : 0;
        var targetX = dx < 0 ? 0 : width - thickness;
        var targetY = dy < 0 ? 0 : height - thickness;
        var stepX = Math.Abs(targetX - x);
        var stepY = -Math.Abs(targetY - y);
        var directionX = x < targetX ? 1 : -1;
        var directionY = y < targetY ? 1 : -1;
        var error = stepX + stepY;
        while (true)
        {
            for (var row = y; row < y + thickness; row++)
            {
                rowLeft[row] = Math.Min(rowLeft[row], x);
                rowRight[row] = Math.Max(rowRight[row], x + thickness - 1);
            }
            if (x == targetX && y == targetY) break;
            var twiceError = error * 2;
            if (twiceError >= stepY) { error += stepY; x += directionX; }
            if (twiceError <= stepX) { error += stepX; y += directionY; }
        }
        var spans = new List<Rectangle>(height);
        for (var row = 0; row < height; row++)
            if (rowRight[row] >= rowLeft[row])
                spans.Add(new Rectangle(minX + rowLeft[row], minY + row, rowRight[row] - rowLeft[row] + 1, 1));
        return spans;
    }
}
