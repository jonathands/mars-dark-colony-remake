using System.Buffers.Binary;

namespace DarkColony.Engine.Data;

/// <summary>
/// The executable's initialized 256-entry dword stream shared by fire
/// presentation, aim scatter, and blocked-route jitter. The values are read
/// from the user's <c>dc.exe</c>; they are never embedded in the port.
/// </summary>
public sealed class NativeRandomTable
{
    /// <summary><c>dc.exe</c> virtual address of the first dword (cursor at <c>0x479204</c>).</summary>
    public const uint TableAddress = 0x478e04;
    public const int Length = 256;

    private readonly uint[] values;

    private NativeRandomTable(uint[] values) => this.values = values;

    public uint this[int index] => values[index];

    /// <summary>
    /// Deterministic stand-in for engine checks that run without an original
    /// installation. It is not native data and must not back real gameplay.
    /// </summary>
    public static NativeRandomTable Synthetic { get; } = CreateSynthetic();

    public static NativeRandomTable Load(string executablePath) => FromImage(PeImage.Load(executablePath));

    public static NativeRandomTable FromImage(PeImage image)
    {
        var bytes = image.AtVirtualAddress(TableAddress, Length * sizeof(uint));
        var values = new uint[Length];
        for (var index = 0; index < values.Length; index++)
            values[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(index * sizeof(uint), sizeof(uint)));
        return new NativeRandomTable(values);
    }

    public static NativeRandomTable FromValues(IReadOnlyList<uint> values)
    {
        if (values.Count != Length) throw new ArgumentException($"A native random table has exactly {Length} values.", nameof(values));
        return new NativeRandomTable(values.ToArray());
    }

    private static NativeRandomTable CreateSynthetic()
    {
        var state = 0x52414e44u; // "RAND"
        var values = new uint[Length];
        for (var index = 0; index < values.Length; index++)
        {
            state = unchecked(state * 214013 + 2531011);
            values[index] = (state >> 16) & 0x7fff;
        }
        return new NativeRandomTable(values);
    }
}
