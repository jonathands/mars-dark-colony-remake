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
    private readonly Dictionary<int, GpuImage> _solidImages = [];

    public IReadOnlyList<SpriteCommand> Commands => _commands;

    public void Reset() => _commands.Clear();

    public void Draw(GpuImage image, int x, int y) =>
        _commands.Add(new SpriteCommand(image, new Rectangle(x, y, image.Width, image.Height)));

    public void Draw(GpuImage image, Rectangle destination) =>
        _commands.Add(new SpriteCommand(image, destination));

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
}
