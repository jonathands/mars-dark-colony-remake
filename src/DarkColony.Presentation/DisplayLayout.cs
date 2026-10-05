using System.Drawing;

namespace DarkColony.Presentation;

/// <summary>How the logical game picture is fitted onto the output.</summary>
public enum ScaleMode
{
    /// <summary>The largest whole-number scale that fits, centred between black bars. Pixel-exact.</summary>
    Integer,

    /// <summary>The largest scale that keeps the picture's shape, centred. Sharp-bilinear filtered.</summary>
    Fit,

    /// <summary>Fills the output and distorts the picture. Sharp-bilinear filtered.</summary>
    Stretch,
}

/// <summary>
/// Where the logical game picture lands on the output (the window's client
/// area or the screen, in physical pixels), and the mapping between the two.
/// </summary>
/// <param name="Logical">The game picture's size: 640x480, or a larger gameplay view.</param>
/// <param name="Output">The output's size in physical pixels.</param>
/// <param name="Destination">The picture's rectangle on the output. Empty when the output is.</param>
/// <param name="Mode">The requested mode. <see cref="PixelExact"/> says whether point sampling suffices.</param>
public readonly record struct DisplayLayout(Size Logical, Size Output, Rectangle Destination, ScaleMode Mode)
{
    /// <summary>The classic picture shown 1:1: 640x480 on a 640x480 output.</summary>
    public static DisplayLayout Classic { get; } = Compute(new Size(640, 480), new Size(640, 480), ScaleMode.Integer);

    /// <summary>
    /// True when every logical pixel covers the same whole number of output
    /// pixels, so point sampling draws it exactly.
    /// </summary>
    public bool PixelExact => !Destination.IsEmpty &&
        Destination.Width % Logical.Width == 0 && Destination.Height % Logical.Height == 0;

    /// <summary>
    /// The whole-number prescale of a sharp-bilinear filter: each axis is
    /// first enlarged by this with point sampling, then filtered to the
    /// destination, so the pixels stay even and sharp at any size.
    /// </summary>
    public Size Prescale => Destination.IsEmpty
        ? new Size(1, 1)
        : new Size(Math.Max(1, Destination.Width / Logical.Width), Math.Max(1, Destination.Height / Logical.Height));

    /// <summary>
    /// The whole-number scale in <see cref="ScaleMode.Integer"/>, or 0 when
    /// the output is smaller than the picture (it is then fitted instead).
    /// </summary>
    public int IntegerScale => PixelExact && Destination.Width / Logical.Width == Destination.Height / Logical.Height
        ? Destination.Width / Logical.Width
        : 0;

    public static DisplayLayout Compute(Size logical, Size output, ScaleMode mode)
    {
        if (logical.Width <= 0 || logical.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(logical), logical, "The logical picture must not be empty.");
        if (output.Width <= 0 || output.Height <= 0) return new DisplayLayout(logical, output, Rectangle.Empty, mode);
        var size = mode switch
        {
            ScaleMode.Stretch => output,
            ScaleMode.Integer when Math.Min(output.Width / logical.Width, output.Height / logical.Height) is var scale and > 0 =>
                new Size(logical.Width * scale, logical.Height * scale),
            // Fit, and Integer on an output smaller than the picture: keep the
            // shape and fill one axis. Integer arithmetic keeps it exact.
            _ => (long)output.Width * logical.Height <= (long)output.Height * logical.Width
                ? new Size(output.Width, (int)Math.Max(1, ((long)output.Width * logical.Height + logical.Width / 2) / logical.Width))
                : new Size((int)Math.Max(1, ((long)output.Height * logical.Width + logical.Height / 2) / logical.Height), output.Height),
        };
        var destination = new Rectangle((output.Width - size.Width) / 2, (output.Height - size.Height) / 2, size.Width, size.Height);
        return new DisplayLayout(logical, output, destination, mode);
    }

    /// <summary>Whether an output point lies on the picture rather than on a bar.</summary>
    public bool Contains(Point output) => Destination.Contains(output);

    /// <summary>
    /// The logical pixel under an output pixel's centre. A point on a bar maps to the
    /// nearest picture edge, so the 3-pixel scroll edges keep working there.
    /// </summary>
    public Point ToLogical(Point output)
    {
        if (Destination.IsEmpty) return Point.Empty;
        // The logical pixel under the output pixel's centre.
        var x = FloorDivide((2L * (output.X - Destination.X) + 1) * Logical.Width, 2L * Destination.Width);
        var y = FloorDivide((2L * (output.Y - Destination.Y) + 1) * Logical.Height, 2L * Destination.Height);
        return new Point((int)Math.Clamp(x, 0, Logical.Width - 1), (int)Math.Clamp(y, 0, Logical.Height - 1));
    }

    /// <summary>The output pixel at the centre of a logical pixel.</summary>
    public Point ToOutput(Point logical) => Destination.IsEmpty
        ? Point.Empty
        : new Point(
            Destination.X + (int)(((2L * logical.X + 1) * Destination.Width) / (2L * Logical.Width)),
            Destination.Y + (int)(((2L * logical.Y + 1) * Destination.Height) / (2L * Logical.Height)));

    /// <summary>The output rectangle a logical rectangle covers.</summary>
    public Rectangle ToOutput(Rectangle logical)
    {
        if (Destination.IsEmpty) return Rectangle.Empty;
        var (destination, size) = (Destination, Logical);
        int X(long x) => destination.X + (int)(x * destination.Width / size.Width);
        int Y(long y) => destination.Y + (int)(y * destination.Height / size.Height);
        return Rectangle.FromLTRB(X(logical.Left), Y(logical.Top), X(logical.Right), Y(logical.Bottom));
    }

    public override string ToString() =>
        $"output {Output.Width}x{Output.Height}, logical {Logical.Width}x{Logical.Height}, " +
        $"destination {Destination.X},{Destination.Y} {Destination.Width}x{Destination.Height}, {Mode}" +
        (PixelExact ? $" x{Destination.Width / Logical.Width}" : " filtered");

    private static long FloorDivide(long value, long divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
}
