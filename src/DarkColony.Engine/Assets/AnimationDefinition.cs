using System.Buffers.Binary;
using System.Text;

namespace DarkColony.Engine.Assets;

public sealed record AnimationRange(string Name, ushort FirstFrame, ushort LastFrame);

/// <summary>
/// A FIN draw layer. <see cref="Values"/> holds the record's four trailing
/// words; the last one is 1 when the layer is drawn mirrored (see <see cref="Mirrored"/>).
/// </summary>
public sealed record DrawLayer(string SpriteName, ushort SpriteFrame, short X, short Y, ushort[] Values)
{
    /// <summary>
    /// The fourth trailing word: 1 draws the frame mirrored left to right.
    /// Derived from the shipped data, not yet from the executable: the
    /// right-facing directions reuse the left-facing frames with this word
    /// set (EXPLSTAND4 is EXPLSTAND12's expl frame 4 mirrored), and only 0
    /// and 1 occur (11,871 of 56,605 layers in 72 files).
    /// </summary>
    public bool Mirrored => Values.Length > 3 && Values[3] != 0;

    /// <summary>
    /// The third trailing word: how the world blitter draws the layer. The
    /// sprite queue keeps it at entry <c>+0x17</c> (<c>0x4360B4</c>), and the
    /// blitter (<c>0x4546EA</c>, table <c>0x454664</c>) picks a routine by it (0-5).
    /// </summary>
    public int DrawType => Values.Length > 2 ? Values[2] : 0;

    /// <summary>
    /// Draw type 3: a light, not a sprite. The main pass skips it
    /// (<c>0x454741</c>, and table entry 3). A pre-pass (<c>0x4621A0</c>) writes
    /// its opaque pixels as <c>(source + destination) &amp; mask</c> into a
    /// per-tile buffer, only at GAME DETAIL medium or high (<c>0x4E686C</c>).
    /// These are the explosions' and napalm's <c>spot</c> and <c>smsp</c>
    /// ellipses, and the <c>blaz</c> flash of Gray and marine fire.
    /// </summary>
    public bool IsLight => DrawType == 3;
}

/// <summary>A FIN logical frame: its delay word (frame ticks are <c>(d + 3) * 15 / 100</c>, d 0 meaning 15; see <see cref="NativeAnimationTiming"/>) and its layers.</summary>
public sealed record LogicalFrame(ushort Delay, IReadOnlyList<DrawLayer> Layers)
{
    /// <summary>
    /// The frame header's eight hotspots, each a 16-byte name and x, y words;
    /// null where the name is empty or <c>NONAME</c>. Slot 7 marks a fire
    /// animation's muzzle (<see cref="NativeFireMuzzles"/>).
    /// </summary>
    public IReadOnlyList<FinHotspot?> Hotspots { get; init; } = [];
}

/// <summary>A named FIN frame hotspot, in pixels from the frame's origin.</summary>
public sealed record FinHotspot(string Name, short X, short Y);

public sealed record CompositeFrame(int X, int Y, int Width, int Height, byte[] Rgba);

public sealed class AnimationDefinition
{
    public const ushort StandardMarker = 29;

    /// <summary>Hotspots in a logical frame header (164 bytes: count, delay, then eight 20-byte hotspots).</summary>
    public const int HotspotCount = 8;

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
        var hotspots = new FinHotspot?[logicalFrameCount][];
        var totalLayers = 0;
        for (var index = 0; index < logicalFrameCount; index++)
        {
            var offset = animationsEnd + index * 164;
            headers[index] = (U16(data, offset), U16(data, offset + 2));
            totalLayers = checked(totalLayers + headers[index].Count);
            hotspots[index] = new FinHotspot?[HotspotCount];
            for (var slot = 0; slot < HotspotCount; slot++)
            {
                var hotspot = offset + 4 + slot * 20;
                var name = Name(data.Slice(hotspot, 16));
                if (name.Length != 0 && !name.Equals("NONAME", StringComparison.Ordinal))
                    hotspots[index][slot] = new FinHotspot(name, I16(data, hotspot + 16), I16(data, hotspot + 18));
            }
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
            logicalFrames[index] = new LogicalFrame(headers[index].Delay, layers) { Hotspots = hotspots[index] };
            layerOffset += headers[index].Count;
        }

        return new AnimationDefinition(spriteNames, animations, logicalFrames);
    }

    /// <summary>
    /// Composes one logical frame. An ordinary layer starts at its FIN X plus
    /// the sprite frame's X. A mirrored layer (<see cref="DrawLayer.Mirrored"/>)
    /// is drawn flipped with its left edge at its FIN X - 1, without the
    /// frame's X. That places every mirrored direction exactly opposite its
    /// source: EXPLSTAND12 spans x -26..30 and EXPLSTAND4 -30..26. It also
    /// keeps the VTOL's engine glow beside its hull in every frame. Vertically, <paramref name="bottomAnchored"/> places
    /// the frame's bottom row at the layer's Y, as the world sprite blit does
    /// (<c>0x454751</c> culls a queued sprite to <c>[y - height, y]</c>, and
    /// <c>0x4399AD</c> queues each layer at the actor's position plus its
    /// FIN offsets); the frame's own Y is only its place on the artist's
    /// canvas. The world blit also leaves out light layers
    /// (<see cref="DrawLayer.IsLight"/>), which brighten the ground instead;
    /// <c>lights</c> composes them alone.
    /// Otherwise the frame's Y is added to the layer's, which is how the
    /// interface art is laid out.
    /// </summary>
    /// <param name="includeLayer">Composes only the layers it accepts (all when null).</param>
    /// <param name="lights">Composes only the light layers, laid out as the world blit lays out the rest.</param>
    public CompositeFrame Compose(int frameIndex, Func<string, Sprite> spriteLoader, bool bottomAnchored = false,
        IReadOnlyList<VgaColor>? palette = null, Func<DrawLayer, bool>? includeLayer = null, bool lights = false)
    {
        ArgumentNullException.ThrowIfNull(spriteLoader);
        var logical = LogicalFrames[frameIndex];
        var layers = logical.Layers.Where(layer => (includeLayer is null || includeLayer(layer)) &&
            (lights ? layer.IsLight : !(bottomAnchored && layer.IsLight))).ToArray();
        if (layers.Length == 0) return new CompositeFrame(0, 0, 1, 1, new byte[4]);
        var sources = layers.Select(layer =>
        {
            var sprite = spriteLoader(layer.SpriteName);
            var frame = sprite.Frames[layer.SpriteFrame];
            var x = layer.Mirrored ? layer.X - 1 : layer.X + frame.AnchorX;
            var y = bottomAnchored ? layer.Y - frame.Height : layer.Y + frame.AnchorY;
            return (Layer: layer, Frame: frame, X: x, Y: y, Rgba: sprite.FrameRgba(layer.SpriteFrame, palette: palette));
        }).ToArray();
        var left = sources.Min(item => item.X);
        var top = sources.Min(item => item.Y);
        var right = sources.Max(item => item.X + item.Frame.Width);
        var bottom = sources.Max(item => item.Y + item.Frame.Height);
        var width = right - left;
        var height = bottom - top;
        var target = new byte[checked(width * height * 4)];
        foreach (var source in sources)
        {
            var targetX = source.X - left;
            var targetY = source.Y - top;
            var mirrored = source.Layer.Mirrored;
            for (var y = 0; y < source.Frame.Height; y++)
            {
                for (var x = 0; x < source.Frame.Width; x++)
                {
                    var sourceX = mirrored ? source.Frame.Width - 1 - x : x;
                    var sourceOffset = (y * source.Frame.Width + sourceX) * 4;
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
