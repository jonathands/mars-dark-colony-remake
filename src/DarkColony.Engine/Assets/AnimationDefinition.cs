using System.Buffers.Binary;
using System.Text;

namespace DarkColony.Engine.Assets;

public sealed record AnimationRange(string Name, ushort FirstFrame, ushort LastFrame);

public sealed record DrawLayer(string SpriteName, ushort SpriteFrame, short X, short Y, ushort[] Values);

/// <summary>A FIN logical frame: its delay word (frame ticks are <c>(d + 3) * 15 / 100</c>, d 0 meaning 15; see <see cref="NativeAnimationTiming"/>) and its layers.</summary>
public sealed record LogicalFrame(ushort Delay, IReadOnlyList<DrawLayer> Layers);

public sealed record CompositeFrame(int X, int Y, int Width, int Height, byte[] Rgba);

public sealed class AnimationDefinition
{
    public const ushort StandardMarker = 29;

    private AnimationDefinition(
        IReadOnlyList<string> spriteNames,
        IReadOnlyList<AnimationRange> animations,
        IReadOnlyList<LogicalFrame> logicalFrames)
    {
        SpriteNames = spriteNames;
        Animations = animations;
        LogicalFrames = logicalFrames;
    }

    public IReadOnlyList<string> SpriteNames { get; }
    public IReadOnlyList<AnimationRange> Animations { get; }
    public IReadOnlyList<LogicalFrame> LogicalFrames { get; }

    public static AnimationDefinition Load(string path) => Parse(File.ReadAllBytes(path));

    public static AnimationDefinition Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) throw new InvalidDataException("FIN file is shorter than its header.");
        var marker = U16(data, 0);
        if (marker != StandardMarker) throw new InvalidDataException($"Unsupported FIN marker 0x{marker:x4}.");
        var logicalFrameCount = U16(data, 2);
        var animationCount = U16(data, 4);
        var spriteCount = U16(data, 6);
        var namesEnd = checked(8 + spriteCount * 8);
        var animationsEnd = checked(namesEnd + animationCount * 20);
        if (animationsEnd > data.Length) throw new InvalidDataException("FIN name tables exceed the file.");

        var spriteNames = new string[spriteCount];
        for (var index = 0; index < spriteCount; index++) spriteNames[index] = Name(data.Slice(8 + index * 8, 8));

        var animations = new AnimationRange[animationCount];
        for (var index = 0; index < animationCount; index++)
        {
            var offset = namesEnd + index * 20;
            animations[index] = new AnimationRange(Name(data.Slice(offset, 16)), U16(data, offset + 16), U16(data, offset + 18));
        }

        var logicalEnd = checked(animationsEnd + logicalFrameCount * 164);
        if (logicalEnd > data.Length) throw new InvalidDataException("FIN logical-frame table exceeds the file.");
        var headers = new (ushort Count, ushort Delay)[logicalFrameCount];
        var totalLayers = 0;
        for (var index = 0; index < logicalFrameCount; index++)
        {
            var offset = animationsEnd + index * 164;
            headers[index] = (U16(data, offset), U16(data, offset + 2));
            totalLayers = checked(totalLayers + headers[index].Count);
        }

        var expected = checked(logicalEnd + totalLayers * 22);
        if (data.Length < expected || (data.Length - expected) % 22 != 0)
        {
            throw new InvalidDataException($"FIN length is {data.Length}; expected at least {expected}.");
        }

        var allLayers = new DrawLayer[(data.Length - logicalEnd) / 22];
        for (var index = 0; index < allLayers.Length; index++)
        {
            var offset = logicalEnd + index * 22;
            allLayers[index] = new DrawLayer(
                Name(data.Slice(offset, 8)), U16(data, offset + 8), I16(data, offset + 10), I16(data, offset + 12),
                [U16(data, offset + 14), U16(data, offset + 16), U16(data, offset + 18), U16(data, offset + 20)]);
        }

        var logicalFrames = new LogicalFrame[logicalFrameCount];
        var layerOffset = 0;
        for (var index = 0; index < logicalFrameCount; index++)
        {
            var layers = allLayers.Skip(layerOffset).Take(headers[index].Count).ToArray();
            logicalFrames[index] = new LogicalFrame(headers[index].Delay, layers);
            layerOffset += headers[index].Count;
        }

        return new AnimationDefinition(spriteNames, animations, logicalFrames);
    }

    /// <summary>
    /// Composes one logical frame. Every layer starts at its FIN X plus the
    /// sprite frame's X. Vertically, <paramref name="bottomAnchored"/> places
    /// the frame's bottom row at the layer's Y, as the world sprite blit does
    /// (<c>0x454751</c> culls a queued sprite to <c>[y - height, y]</c>, and
    /// <c>0x4399AD</c> queues each layer at the actor's position plus its
    /// FIN offsets); the frame's own Y is only its place on the artist's
    /// canvas. Otherwise the frame's Y is added to the layer's, which is how
    /// the interface art is laid out.
    /// </summary>
    public CompositeFrame Compose(int frameIndex, Func<string, Sprite> spriteLoader, bool bottomAnchored = false)
    {
        ArgumentNullException.ThrowIfNull(spriteLoader);
        var logical = LogicalFrames[frameIndex];
        if (logical.Layers.Count == 0) return new CompositeFrame(0, 0, 1, 1, new byte[4]);
        var sources = logical.Layers.Select(layer =>
        {
            var sprite = spriteLoader(layer.SpriteName);
            var frame = sprite.Frames[layer.SpriteFrame];
            var y = bottomAnchored ? layer.Y - frame.Height : layer.Y + frame.AnchorY;
            return (Layer: layer, Frame: frame, Y: y, Rgba: sprite.FrameRgba(layer.SpriteFrame));
        }).ToArray();
        var left = sources.Min(item => item.Layer.X + item.Frame.AnchorX);
        var top = sources.Min(item => item.Y);
        var right = sources.Max(item => item.Layer.X + item.Frame.AnchorX + item.Frame.Width);
        var bottom = sources.Max(item => item.Y + item.Frame.Height);
        var width = right - left;
        var height = bottom - top;
        var target = new byte[checked(width * height * 4)];
        foreach (var source in sources)
        {
            var targetX = source.Layer.X + source.Frame.AnchorX - left;
            var targetY = source.Y - top;
            for (var y = 0; y < source.Frame.Height; y++)
            {
                for (var x = 0; x < source.Frame.Width; x++)
                {
                    var sourceOffset = (y * source.Frame.Width + x) * 4;
                    if (source.Rgba[sourceOffset + 3] == 0) continue;
                    var targetOffset = ((targetY + y) * width + targetX + x) * 4;
                    source.Rgba.AsSpan(sourceOffset, 4).CopyTo(target.AsSpan(targetOffset));
                }
            }
        }

        return new CompositeFrame(left, top, width, height, target);
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static short I16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
    private static string Name(ReadOnlySpan<byte> data)
    {
        var zero = data.IndexOf((byte)0);
        return Encoding.ASCII.GetString(zero < 0 ? data : data[..zero]);
    }
}
