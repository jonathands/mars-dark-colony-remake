using DarkColony.Presentation;

namespace DarkColony.App.Rendering;

public sealed class GpuImage
{
    public GpuImage(int width, int height, byte[] rgba, bool transient = false)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
        IsTransient = transient;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }
    /// <summary>
    /// A per-frame primitive whose dimensions vary with input or movement.
    /// The D3D surface must not retain a device texture for it indefinitely.
    /// </summary>
    public bool IsTransient { get; }
}

public readonly record struct SpriteCommand(GpuImage Image, Rectangle Destination);

public sealed class GameCanvas
{
    private readonly List<SpriteCommand> _commands = [];
    private readonly List<SpriteCommand> _foregroundCommands = [];
    private readonly Dictionary<int, GpuImage> _solidImages = [];
    private readonly Dictionary<(int Width, int Height, int Color, int Thickness, bool Filled), GpuImage> _ellipses = [];

    private Point _origin;

    public IReadOnlyList<SpriteCommand> Commands => _commands;
    public IReadOnlyList<SpriteCommand> ForegroundCommands => _foregroundCommands;

    public void Reset()
    {
        _commands.Clear();
        _foregroundCommands.Clear();
        _origin = Point.Empty;
    }

    /// <summary>
    /// Moves everything drawn until the result is disposed by
    /// <paramref name="offset"/>: a 640x480 popup is centred on a larger view.
    /// </summary>
    public Translation Translate(Point offset)
    {
        var previous = _origin;
        _origin = new Point(_origin.X + offset.X, _origin.Y + offset.Y);
        return new Translation(this, previous);
    }

    public readonly struct Translation(GameCanvas canvas, Point previous) : IDisposable
    {
        public void Dispose() => canvas._origin = previous;
    }

    public void Draw(GpuImage image, int x, int y) =>
        _commands.Add(new SpriteCommand(image, new Rectangle(x + _origin.X, y + _origin.Y, image.Width, image.Height)));

    public void Draw(GpuImage image, Rectangle destination) =>
        _commands.Add(new SpriteCommand(image, Shifted(destination)));

    public void DrawForeground(GpuImage image, int x, int y) =>
        _foregroundCommands.Add(new SpriteCommand(image, new Rectangle(x + _origin.X, y + _origin.Y, image.Width, image.Height)));

    public void DrawForeground(GpuImage image, Rectangle destination) =>
        _foregroundCommands.Add(new SpriteCommand(image, Shifted(destination)));

    private Rectangle Shifted(Rectangle destination) =>
        _origin.IsEmpty ? destination : new Rectangle(destination.X + _origin.X, destination.Y + _origin.Y, destination.Width, destination.Height);

    public void Fill(Rectangle destination, Color color)
    {
        var key = color.ToArgb();
        if (!_solidImages.TryGetValue(key, out var image))
        {
            image = new GpuImage(1, 1, [color.R, color.G, color.B, color.A]);
            _solidImages[key] = image;
        }

        Draw(image, destination);
    }

    public void FillForeground(Rectangle destination, Color color)
    {
        var key = color.ToArgb();
        if (!_solidImages.TryGetValue(key, out var image))
        {
            image = new GpuImage(1, 1, [color.R, color.G, color.B, color.A]);
            _solidImages[key] = image;
        }
        DrawForeground(image, destination);
    }

    public void Ellipse(Rectangle destination, Color color, int thickness = 1, bool filled = false, bool foreground = false)
    {
        if (destination.Width <= 0 || destination.Height <= 0) return;
        var key = (destination.Width, destination.Height, color.ToArgb(), thickness, filled);
        if (!_ellipses.TryGetValue(key, out var image))
        {
            var rgba = new byte[destination.Width * destination.Height * 4];
            var outerRadiusX = destination.Width / 2d;
            var outerRadiusY = destination.Height / 2d;
            var innerRadiusX = Math.Max(0, outerRadiusX - thickness);
            var innerRadiusY = Math.Max(0, outerRadiusY - thickness);
            for (var y = 0; y < destination.Height; y++)
            for (var x = 0; x < destination.Width; x++)
            {
                var dx = (x + .5 - outerRadiusX) / outerRadiusX;
                var dy = (y + .5 - outerRadiusY) / outerRadiusY;
                var outer = dx * dx + dy * dy;
                var inside = outer <= 1;
                var inner = innerRadiusX == 0 || innerRadiusY == 0
                    ? double.PositiveInfinity
                    : Math.Pow((x + .5 - outerRadiusX) / innerRadiusX, 2) + Math.Pow((y + .5 - outerRadiusY) / innerRadiusY, 2);
                if (!inside || (!filled && inner < 1)) continue;
                var offset = (y * destination.Width + x) * 4;
                rgba[offset] = color.R;
                rgba[offset + 1] = color.G;
                rgba[offset + 2] = color.B;
                rgba[offset + 3] = color.A;
            }
            image = new GpuImage(destination.Width, destination.Height, rgba);
            _ellipses[key] = image;
        }
        if (foreground) DrawForeground(image, destination);
        else Draw(image, destination);
    }

    /// <summary>
    /// A thick Bresenham line, drawn as one filled span per row
    /// (<see cref="PixelLine"/>). Route and selection lines change every frame;
    /// as spans they need no texture of their own.
    /// </summary>
    public void Line(Point start, Point end, Color color, int thickness = 1, bool foreground = false)
    {
        foreach (var span in PixelLine.Spans(start, end, thickness))
        {
            if (foreground) FillForeground(span, color);
            else Fill(span, color);
        }
    }
}
