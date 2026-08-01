namespace DarkColony.Engine.Movement;

/// <summary>
/// Native 16-byte local path: at most 32 steps, first step in the low nibble
/// and second step in the high nibble of each byte.
/// </summary>
public sealed class PackedLocalPath
{
    public const int MaximumSteps = 32;
    public const int PackedByteCount = MaximumSteps / 2;
    private readonly byte[] _packed = new byte[PackedByteCount];

    public int Count { get; private set; }
    public ReadOnlySpan<byte> PackedBytes => _packed;

    public void Append(PathDirection direction)
    {
        if (Count == MaximumSteps) throw new InvalidOperationException("A native local path cannot exceed 32 steps.");
        var value = (byte)direction;
        if (value > 7) throw new ArgumentOutOfRangeException(nameof(direction));
        var byteIndex = Count / 2;
        if ((Count & 1) == 0)
        {
            _packed[byteIndex] = value;
        }
        else
        {
            _packed[byteIndex] |= (byte)(value << 4);
        }

        Count++;
    }

    public PathDirection this[int index]
    {
        get
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            var packed = _packed[index / 2];
            var value = (index & 1) == 0 ? packed : packed >> 4;
            return (PathDirection)(value & 0x0f);
        }
    }

    public void Clear()
    {
        Array.Clear(_packed);
        Count = 0;
    }
}
