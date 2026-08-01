namespace DarkColony.Engine.Assets;

public sealed record SpriteFrame(
    ushort Width,
    ushort Height,
    ushort AnchorX,
    ushort AnchorY,
    byte[] Payload,
    bool IsCompressed)
{
    public byte[] DecodeIndices()
    {
        var expected = checked(Width * Height);
        if (!IsCompressed)
        {
            if (Payload.Length != expected)
            {
                throw new InvalidDataException($"Raw frame has {Payload.Length} pixels; expected {expected}.");
            }

            return (byte[])Payload.Clone();
        }

        var output = new byte[expected];
        var source = 0;
        var destination = 0;
        while (source < Payload.Length)
        {
            var control = Payload[source++];
            if (control >= 128)
            {
                var count = 256 - control;
                EnsureCapacity(destination, count, expected);
                destination += count;
                continue;
            }

            var literalCount = control + 1;
            if (source + literalCount > Payload.Length)
            {
                throw new InvalidDataException("SPR literal run exceeds its frame payload.");
            }

            EnsureCapacity(destination, literalCount, expected);
            Payload.AsSpan(source, literalCount).CopyTo(output.AsSpan(destination));
            source += literalCount;
            destination += literalCount;
        }

        if (destination != expected)
        {
            throw new InvalidDataException($"SPR frame decoded to {destination} pixels; expected {expected}.");
        }

        return output;
    }

    private static void EnsureCapacity(int position, int count, int expected)
    {
        if (position + count > expected)
        {
            throw new InvalidDataException("Decoded SPR frame exceeds its declared dimensions.");
        }
    }
}
