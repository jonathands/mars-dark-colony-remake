using System.Buffers.Binary;
using DarkColony.Engine.Data;

namespace DarkColony.Engine.World;

/// <summary>Building collision masks recovered from the original executable tables.</summary>
public sealed class BuildingFootprintCatalog
{
    private const uint FootprintTableAddress = 0x47abe8;
    private const uint BuildEntityTableAddress = 0x47afa8;
    private const int PatternCount = 15;
    private const int MaximumOffsets = 8;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<CellCoordinate>> byEntityId;

    private BuildingFootprintCatalog(IReadOnlyDictionary<int, IReadOnlyList<CellCoordinate>> byEntityId) =>
        this.byEntityId = byEntityId;

    public static BuildingFootprintCatalog Load(string executablePath)
    {
        var image = PeImage.Load(executablePath);
        var patterns = new IReadOnlyList<CellCoordinate>[PatternCount];
        var table = image.AtVirtualAddress(FootprintTableAddress, PatternCount * MaximumOffsets * 8);
        for (var pattern = 0; pattern < PatternCount; pattern++)
        {
            var raw = new CellCoordinate[MaximumOffsets];
            for (var slot = 0; slot < MaximumOffsets; slot++)
            {
                var offset = (pattern * MaximumOffsets + slot) * 8;
                raw[slot] = new CellCoordinate(
                    BinaryPrimitives.ReadInt32LittleEndian(table.Slice(offset, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(table.Slice(offset + 4, 4)));
            }

            var occupied = new List<CellCoordinate>();
            for (var slot = 0; slot < raw.Length; slot++)
            {
                if (slot > 0 && raw[slot] == raw[slot - 1]) break;
                if (!occupied.Contains(raw[slot])) occupied.Add(raw[slot]);
            }

            patterns[pattern] = occupied;
        }

        var entityTable = image.AtVirtualAddress(BuildEntityTableAddress, 4 * PatternCount * 4);
        var slotsByEntity = new Dictionary<int, HashSet<int>>();
        for (var set = 0; set < 4; set++)
        for (var slot = 0; slot < PatternCount; slot++)
        {
            var entityId = BinaryPrimitives.ReadInt32LittleEndian(entityTable.Slice((set * PatternCount + slot) * 4, 4));
            if (entityId <= 0) continue;
            if (!slotsByEntity.TryGetValue(entityId, out var slots)) slotsByEntity[entityId] = slots = [];
            slots.Add(slot);
        }

        var resolved = slotsByEntity
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(pair => pair.Key, pair => patterns[pair.Value.Single()]);
        return new BuildingFootprintCatalog(resolved);
    }

    public bool TryGetOffsets(int entityId, out IReadOnlyList<CellCoordinate> offsets) =>
        byEntityId.TryGetValue(entityId, out offsets!);

    public IReadOnlyList<CellCoordinate> OccupiedCells(int entityId, CellCoordinate origin)
    {
        if (!TryGetOffsets(entityId, out var offsets)) return [];
        return offsets.Select(offset => new CellCoordinate(origin.X + offset.X, origin.Z + offset.Z)).ToArray();
    }
}
