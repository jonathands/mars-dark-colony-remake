using System.Buffers.Binary;

namespace DarkColony.Engine.Assets;

public sealed class Sprite
{
    public const ushort RawSignature = 0x0001;
    public const ushort CompressedSignature = 0x0081;
    private const int PaletteOffset = 8;
    private const int PaletteByteCount = 256 * 3;
    private const int FrameTableOffset = PaletteOffset + PaletteByteCount;

    private Sprite(ushort signature, IReadOnlyList<VgaColor> palette, IReadOnlyList<SpriteFrame> frames)
    {
        Signature = signature;
        Palette = palette;
        Frames = frames;
    }

    public ushort Signature { get; }
    public IReadOnlyList<VgaColor> Palette { get; }
    public IReadOnlyList<SpriteFrame> Frames { get; }

    public static Sprite Load(string path) => Parse(File.ReadAllBytes(path));

    public static Sprite Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < FrameTableOffset)
        {
            throw new InvalidDataException("File is shorter than the SPR header and palette.");
        }

        var signature = ReadUInt16(data, 0);
        if (signature is not RawSignature and not CompressedSignature)
        {
            throw new InvalidDataException($"Unsupported SPR signature 0x{signature:x4}.");
        }

        var frameCount = ReadUInt16(data, 2);
        var declaredPayloadSize = ReadUInt32(data, 4);
        var compressed = signature == CompressedSignature;
        var palette = new VgaColor[256];
        for (var index = 0; index < palette.Length; index++)
        {
            var offset = PaletteOffset + index * 3;
            palette[index] = new VgaColor(
                ExpandVgaChannel(data[offset]),
                ExpandVgaChannel(data[offset + 1]),
                ExpandVgaChannel(data[offset + 2]));
        }

        var tableEnd = checked(FrameTableOffset + frameCount * 8);
        if (tableEnd > data.Length) throw new InvalidDataException("Truncated SPR frame table.");

        var descriptors = new (ushort Width, ushort Height, ushort X, ushort Y)[frameCount];
        for (var index = 0; index < frameCount; index++)
        {
            var offset = FrameTableOffset + index * 8;
            descriptors[index] = (
                ReadUInt16(data, offset), ReadUInt16(data, offset + 2),
                ReadUInt16(data, offset + 4), ReadUInt16(data, offset + 6));
        }

        var position = tableEnd;
        long payloadTotal = 0;
        var frames = new SpriteFrame[frameCount];
        for (var index = 0; index < frameCount; index++)
        {
            var descriptor = descriptors[index];
            int payloadSize;
            if (compressed)
            {
                if (position + 4 > data.Length) throw new InvalidDataException($"SPR frame {index} has no payload length.");
                payloadSize = checked((int)ReadUInt32(data, position));
                position += 4;
            }
            else
            {
                payloadSize = checked(descriptor.Width * descriptor.Height);
            }

            var end = checked(position + payloadSize);
            if (end > data.Length) throw new InvalidDataException($"SPR frame {index} has a truncated payload.");
            frames[index] = new SpriteFrame(
                descriptor.Width, descriptor.Height, descriptor.X, descriptor.Y,
                data[position..end].ToArray(), compressed);
            payloadTotal += payloadSize;
            position = end;
        }

        if (position != data.Length) throw new InvalidDataException($"SPR has {data.Length - position} trailing bytes.");
        var validDeclaredSize = compressed
            ? declaredPayloadSize == payloadTotal
            : declaredPayloadSize == payloadTotal + frameCount * 4L || declaredPayloadSize == payloadTotal + frameCount * 12L;
        if (!validDeclaredSize)
        {
            throw new InvalidDataException($"SPR declared payload {declaredPayloadSize}; decoded {payloadTotal}.");
        }

        return new Sprite(signature, palette, frames);
    }

    /// <param name="palette">
    /// A screen palette to use instead of the file's own: the executable draws
    /// interface sprites with the palette of the screen they are on.
    /// </param>
    public byte[] FrameRgba(int frameIndex, bool transparentPaletteZero = true, IReadOnlyList<VgaColor>? palette = null)
    {
        var colors = palette ?? Palette;
        var indices = Frames[frameIndex].DecodeIndices();
        var rgba = new byte[indices.Length * 4];
        for (var index = 0; index < indices.Length; index++)
        {
            var paletteIndex = indices[index];
            var color = paletteIndex < colors.Count ? colors[paletteIndex] : Palette[paletteIndex];
            var destination = index * 4;
            rgba[destination] = color.Red;
            rgba[destination + 1] = color.Green;
            rgba[destination + 2] = color.Blue;
            rgba[destination + 3] = transparentPaletteZero && paletteIndex == 0 ? (byte)0 : (byte)255;
        }

        return rgba;
    }

    public static byte ExpandVgaChannel(byte value) => (byte)((value << 2) | (value >> 4));

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
