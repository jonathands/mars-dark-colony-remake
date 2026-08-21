namespace DarkColony.App.Rendering;

public sealed class GpuImage
{
    public GpuImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }
}

public readonly record struct SpriteCommand(GpuImage Image, Rectangle Destination);

public sealed class GameCanvas
{
    private readonly List<SpriteCommand> _commands = [];
    private readonly List<SpriteCommand> _foregroundCommands = [];
    private readonly Dictionary<int, GpuImage> _solidImages = [];
    private readonly Dictionary<(int Width, int Height, int Color, int Thickness, bool Filled), GpuImage> _ellipses = [];
    private readonly Dictionary<(int Dx, int Dy, int Color, int Thickness), GpuImage> _lines = [];

    public IReadOnlyList<SpriteCommand> Commands => _commands;
    public IReadOnlyList<SpriteCommand> ForegroundCommands => _foregroundCommands;

    public void Reset()
    {
        _commands.Clear();
        _foregroundCommands.Clear();
    }

    public void Draw(GpuImage image, int x, int y) =>
        _commands.Add(new SpriteCommand(image, new Rectangle(x, y, image.Width, image.Height)));

    public void Draw(GpuImage image, Rectangle destination) =>
        _commands.Add(new SpriteCommand(image, destination));

    public void DrawForeground(GpuImage image, int x, int y) =>
        _foregroundCommands.Add(new SpriteCommand(image, new Rectangle(x, y, image.Width, image.Height)));

    public void DrawForeground(GpuImage image, Rectangle destination) =>
        _foregroundCommands.Add(new SpriteCommand(image, destination));

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

    public void Line(Point start, Point end, Color color, int thickness = 1, bool foreground = false)
    {
        var minX = Math.Min(start.X, end.X);
        var minY = Math.Min(start.Y, end.Y);
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var key = (dx, dy, color.ToArgb(), thickness);
        if (!_lines.TryGetValue(key, out var image))
        {
            var width = Math.Abs(dx) + thickness;
            var height = Math.Abs(dy) + thickness;
            var rgba = new byte[width * height * 4];
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
                for (var offsetY = 0; offsetY < thickness; offsetY++)
                for (var offsetX = 0; offsetX < thickness; offsetX++)
                {
                    var pixelX = x + offsetX;
                    var pixelY = y + offsetY;
                    if ((uint)pixelX >= width || (uint)pixelY >= height) continue;
                    var offset = (pixelY * width + pixelX) * 4;
                    rgba[offset] = color.R;
                    rgba[offset + 1] = color.G;
                    rgba[offset + 2] = color.B;
                    rgba[offset + 3] = color.A;
                }
                if (x == targetX && y == targetY) break;
                var twiceError = error * 2;
                if (twiceError >= stepY) { error += stepY; x += directionX; }
                if (twiceError <= stepX) { error += stepX; y += directionY; }
            }
            image = new GpuImage(width, height, rgba);
            _lines[key] = image;
        }
        var destination = new Rectangle(minX, minY, image.Width, image.Height);
        if (foreground) DrawForeground(image, destination);
        else Draw(image, destination);
    }
}
