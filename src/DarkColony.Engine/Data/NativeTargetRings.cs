using System.Buffers.Binary;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Data;

/// <summary>
/// The executable's target-search offsets at <c>0x434090</c>: <c>(dx, dz)</c>
/// short pairs grouped into rings 0..16 of whole distance (ring r holds the cells
/// with r <= sqrt(dx^2 + dz^2) < r + 1; ring 16 only the four axis cells), each ended by
/// <c>dx == 99</c>. The selector <c>0x435570</c> walks rings 0 through its
/// radius in table order. The table is read from the user's <c>dc.exe</c>.
/// </summary>
public sealed class NativeTargetRings
{
    public const uint TableAddress = 0x434090;
    public const int MaximumRing = 16;
    private const short RingEnd = 99;
    private const int MaximumEntries = 2048;

    private readonly Dictionary<CellCoordinate, int> ringOf = [];

    private NativeTargetRings(IReadOnlyList<IReadOnlyList<CellCoordinate>> rings)
    {
        Rings = rings;
        for (var ring = 0; ring < rings.Count; ring++)
            foreach (var offset in rings[ring])
                ringOf.TryAdd(offset, ring);
    }

    /// <summary>Offsets of each ring, index 0 through <see cref="MaximumRing"/>.</summary>
    public IReadOnlyList<IReadOnlyList<CellCoordinate>> Rings { get; }

    /// <summary>The ring that holds an offset, or null beyond the table.</summary>
    public int? RingOf(int dx, int dz) => ringOf.TryGetValue(new CellCoordinate(dx, dz), out var ring) ? ring : null;

    public static NativeTargetRings Load(string executablePath) => FromImage(PeImage.Load(executablePath));

    public static NativeTargetRings FromImage(PeImage image)
    {
        var bytes = image.AtVirtualAddress(TableAddress, MaximumEntries * 4);
        var rings = new List<IReadOnlyList<CellCoordinate>>();
        var current = new List<CellCoordinate>();
        for (var entry = 0; entry < MaximumEntries && rings.Count <= MaximumRing; entry++)
        {
            var dx = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(entry * 4, 2));
            var dz = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(entry * 4 + 2, 2));
            if (dx == RingEnd)
            {
                rings.Add(current);
                current = [];
                continue;
            }
            current.Add(new CellCoordinate(dx, dz));
        }
        if (rings.Count != MaximumRing + 1)
            throw new InvalidDataException($"Target ring table ended after {rings.Count} rings; expected {MaximumRing + 1}.");
        if (rings[0].Count != 1 || rings[0][0] != new CellCoordinate(0, 0))
            throw new InvalidDataException("Target ring 0 is not the origin cell.");
        return new NativeTargetRings(rings);
    }

    /// <summary>Builds a table from explicit rings, for checks without an installation.</summary>
    public static NativeTargetRings FromRings(IEnumerable<IEnumerable<CellCoordinate>> rings) =>
        new(rings.Select(ring => (IReadOnlyList<CellCoordinate>)ring.ToArray()).ToArray());
}
