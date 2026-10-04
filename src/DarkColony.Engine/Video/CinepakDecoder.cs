using System.Buffers.Binary;

namespace DarkColony.Engine.Video;

/// <summary>
/// A Cinepak (<c>cvid</c>) decoder producing top-down RGB24 frames, the job
/// the original hands to the system codec through <c>ICDecompress</c>.
/// </summary>
/// <remarks>
/// <para>
/// A frame holds strips. Each strip keeps two 256-entry codebooks between
/// frames: V4 entries are 2x2 blocks and V1 entries are scaled up to 4x4.
/// Unless frame flag bit 0 is set, a strip starts from the previous strip's
/// codebooks.
/// </para>
/// <para>
/// Codebook chunks (0x20-0x27) store either 4 luma bytes, or 4 luma bytes
/// plus signed U and V. RGB is <c>Y + 2V</c>, <c>Y - U/2 - V</c>, <c>Y + 2U</c>, each
/// clamped to 0..255.
/// </para>
/// <para>
/// Vector chunks 0x30-0x32 cover the strip in 4x4 blocks:
/// <list type="bullet">
/// <item><description>0x30: a flag bit per block picks V4 or V1.</description></item>
/// <item><description>0x31: one bit first says whether the block changes at all.</description></item>
/// <item><description>0x32: every block is V1.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class CinepakDecoder
{
    private const int MaximumStrips = 32;
    private const int EntryBytes = 12;

    private readonly byte[][] _v4 = new byte[MaximumStrips][];
    private readonly byte[][] _v1 = new byte[MaximumStrips][];

    public CinepakDecoder(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Frame = new byte[width * height * 3];
        for (var strip = 0; strip < MaximumStrips; strip++)
        {
            _v4[strip] = new byte[256 * EntryBytes];
            _v1[strip] = new byte[256 * EntryBytes];
        }
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The current picture, top-down, three bytes (R, G, B) per pixel.</summary>
    public byte[] Frame { get; }

    /// <summary>Applies one compressed frame; an empty frame keeps the picture.</summary>
    public void Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        if (data.Length < 10) throw new InvalidDataException("Cinepak frame header is truncated.");
        var flags = data[0];
        var strips = Math.Min((int)BinaryPrimitives.ReadUInt16BigEndian(data[8..]), MaximumStrips);
        var position = 10;
        var top = 0;
        for (var strip = 0; strip < strips; strip++)
        {
            if (position + 12 > data.Length) throw new InvalidDataException("Cinepak strip header is truncated.");
            var y1 = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 4)..]);
            var x1 = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 6)..]);
            var y2 = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 8)..]);
            var x2 = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 10)..]);
            // A zero top means "below the previous strip", with y2 then a height.
            int stripTop = y1, stripBottom = y2;
            if (y1 == 0)
            {
                stripTop = top;
                stripBottom = top + y2;
            }
            var size = (int)(BinaryPrimitives.ReadUInt32BigEndian(data[position..]) & 0xFFFFFF) - 12;
            if (size < 0) throw new InvalidDataException("Cinepak strip size is negative.");
            position += 12;
            size = Math.Min(size, data.Length - position);
            if (strip > 0 && (flags & 1) == 0)
            {
                _v4[strip - 1].CopyTo(_v4[strip], 0);
                _v1[strip - 1].CopyTo(_v1[strip], 0);
            }
            DecodeStrip(strip, data.Slice(position, size), x1, stripTop, x2, stripBottom);
            position += size;
            top = stripBottom;
        }
    }

    private void DecodeStrip(int strip, ReadOnlySpan<byte> data, int left, int top, int right, int bottom)
    {
        var position = 0;
        while (position + 4 <= data.Length)
        {
            var id = data[position];
            var size = (int)(BinaryPrimitives.ReadUInt32BigEndian(data[position..]) & 0xFFFFFF) - 4;
            if (size < 0) throw new InvalidDataException("Cinepak chunk size is negative.");
            position += 4;
            size = Math.Min(size, data.Length - position);
            var chunk = data.Slice(position, size);
            switch (id)
            {
                case 0x20 or 0x21 or 0x24 or 0x25:
                    DecodeCodebook(_v4[strip], id, chunk);
                    break;
                case 0x22 or 0x23 or 0x26 or 0x27:
                    DecodeCodebook(_v1[strip], id, chunk);
                    break;
                case 0x30 or 0x31 or 0x32:
                    DecodeVectors(strip, id, chunk, left, top, right, bottom);
                    return;
            }
            position += size;
        }
    }

    private static void DecodeCodebook(byte[] codebook, byte id, ReadOnlySpan<byte> data)
    {
        var colour = (id & 0x04) == 0;
        var selective = (id & 0x01) != 0;
        var entryLength = colour ? 6 : 4;
        uint flag = 0, mask = 0;
        var position = 0;
        for (var entry = 0; entry < 256; entry++)
        {
            if (selective && (mask >>= 1) == 0)
            {
                if (position + 4 > data.Length) return;
                flag = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
                position += 4;
                mask = 0x80000000;
            }
            if (selective && (flag & mask) == 0) continue;
            if (position + entryLength > data.Length) return;
            var target = entry * EntryBytes;
            int u = 0, v = 0;
            if (colour)
            {
                u = (sbyte)data[position + 4];
                v = (sbyte)data[position + 5];
            }
            for (var pixel = 0; pixel < 4; pixel++)
            {
                var luma = data[position + pixel];
                codebook[target + pixel * 3] = Clamp(luma + 2 * v);
                codebook[target + pixel * 3 + 1] = Clamp(luma - u / 2 - v);
                codebook[target + pixel * 3 + 2] = Clamp(luma + 2 * u);
            }
            position += entryLength;
        }
    }

    private void DecodeVectors(int strip, byte id, ReadOnlySpan<byte> data, int left, int top, int right, int bottom)
    {
        var v1Only = (id & 0x02) != 0;
        var inter = (id & 0x01) != 0;
        uint flag = 0, mask = 0;
        var position = 0;

        bool NextBit(ReadOnlySpan<byte> source, ref int at)
        {
            if ((mask >>= 1) == 0)
            {
                if (at + 4 > source.Length) throw new InvalidDataException("Cinepak vector flags are truncated.");
                flag = BinaryPrimitives.ReadUInt32BigEndian(source[at..]);
                at += 4;
                mask = 0x80000000;
            }
            return (flag & mask) != 0;
        }

        for (var y = top; y < bottom; y += 4)
        {
            for (var x = left; x < right; x += 4)
            {
                if (inter && !NextBit(data, ref position)) continue;
                var v4 = !v1Only && NextBit(data, ref position);
                if (!v4)
                {
                    if (position >= data.Length) throw new InvalidDataException("Cinepak V1 vector is truncated.");
                    var entry = data[position++] * EntryBytes;
                    var book = _v1[strip];
                    for (var row = 0; row < 4; row++)
                        for (var column = 0; column < 4; column++)
                            Put(x + column, y + row, book, entry + ((row / 2) * 2 + column / 2) * 3);
                }
                else
                {
                    if (position + 4 > data.Length) throw new InvalidDataException("Cinepak V4 vector is truncated.");
                    var book = _v4[strip];
                    for (var quadrant = 0; quadrant < 4; quadrant++)
                    {
                        var entry = data[position++] * EntryBytes;
                        var baseX = x + (quadrant & 1) * 2;
                        var baseY = y + (quadrant >> 1) * 2;
                        for (var pixel = 0; pixel < 4; pixel++)
                            Put(baseX + (pixel & 1), baseY + (pixel >> 1), book, entry + pixel * 3);
                    }
                }
            }
        }
    }

    private void Put(int x, int y, byte[] book, int offset)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        var target = (y * Width + x) * 3;
        Frame[target] = book[offset];
        Frame[target + 1] = book[offset + 1];
        Frame[target + 2] = book[offset + 2];
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
