using System.Buffers.Binary;
using DarkColony.Engine.Data;

namespace DarkColony.Engine.World;

/// <summary>Building collision masks recovered from the original executable tables.</summary>
public sealed class BuildingFootprintCatalog
{
    private const uint FootprintTableAddress = 0x47abe8;
    private const uint BuildEntityTableAddress = 0x47afa8;
    // Per-slot actor position relative to the city origin, in 1/32 cells:
    // 0x444F14 stores origin * 0x100 + offset * 8 as the 8.8 position.
    private const uint SlotPositionTableAddress = 0x47ab70;
    private const int PatternCount = 15;
    private const int MaximumOffsets = 8;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<CellCoordinate>> byEntityId;
    private readonly IReadOnlyDictionary<(int Faction, int Variant, int Slot), int> buildEntityIds;
    private readonly IReadOnlyList<IReadOnlyList<CellCoordinate>> slotPatterns;
    private readonly IReadOnlyList<(int X, int Z)> slotPositionOffsets;

    private BuildingFootprintCatalog(
        IReadOnlyDictionary<int, IReadOnlyList<CellCoordinate>> byEntityId,
        IReadOnlyDictionary<(int Faction, int Variant, int Slot), int> buildEntityIds,
        IReadOnlyList<IReadOnlyList<CellCoordinate>> slotPatterns,
        IReadOnlyList<(int X, int Z)> slotPositionOffsets)
    {
        this.byEntityId = byEntityId;
        this.buildEntityIds = buildEntityIds;
        this.slotPatterns = slotPatterns;
        this.slotPositionOffsets = slotPositionOffsets;
    }

    /// <summary>Number of building slots in a city (<c>BUILDINGS_PER_SIDE</c>).</summary>
    public const int CitySlotCount = PatternCount;

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
        var resolvedBuildEntities = new Dictionary<(int Faction, int Variant, int Slot), int>();
        for (var set = 0; set < 4; set++)
        for (var slot = 0; slot < PatternCount; slot++)
        {
            var entityId = BinaryPrimitives.ReadInt32LittleEndian(entityTable.Slice((set * PatternCount + slot) * 4, 4));
            if (entityId <= 0) continue;
            resolvedBuildEntities[(set / 2, set % 2, slot)] = entityId;
            if (!slotsByEntity.TryGetValue(entityId, out var slots)) slotsByEntity[entityId] = slots = [];
            slots.Add(slot);
        }

        var resolved = slotsByEntity
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(pair => pair.Key, pair => patterns[pair.Value.Single()]);
        var positionTable = image.AtVirtualAddress(SlotPositionTableAddress, PatternCount * 8);
        var positions = new (int X, int Z)[PatternCount];
        for (var slot = 0; slot < PatternCount; slot++)
            positions[slot] = (BinaryPrimitives.ReadInt32LittleEndian(positionTable.Slice(slot * 8, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(positionTable.Slice(slot * 8 + 4, 4)));
        return new BuildingFootprintCatalog(resolved, resolvedBuildEntities, patterns, positions);
    }

    /// <summary>
    /// The 8.8 position of a city building slot (<c>0x444F14</c>):
    /// <c>origin * 0x100 + offset * 8</c>, with the offset from <c>0x47AB70</c>.
    /// </summary>
    public FixedPointPosition CitySlotPosition(CellCoordinate cityOrigin, int slot)
    {
        var (x, z) = slotPositionOffsets[slot];
        return new FixedPointPosition(cityOrigin.X * FixedPointPosition.One + x * 8, cityOrigin.Z * FixedPointPosition.One + z * 8);
    }

    /// <summary>Cells a city slot occupies: its footprint pattern around the city origin.</summary>
    public IReadOnlyList<CellCoordinate> CitySlotCells(CellCoordinate cityOrigin, int slot) =>
        slotPatterns[slot].Select(offset => new CellCoordinate(cityOrigin.X + offset.X, cityOrigin.Z + offset.Z)).ToArray();

    public bool TryGetOffsets(int entityId, out IReadOnlyList<CellCoordinate> offsets) =>
        byEntityId.TryGetValue(entityId, out offsets!);

    /// <summary>
    /// Resolves the original <c>depend.txt</c> building tuple using dc.exe's
    /// four 15-slot build sets (two factions × two variants).
    /// </summary>
    public bool TryResolveBuildingEntity(int faction, int variant, int slot, out int entityId) =>
        buildEntityIds.TryGetValue((faction, variant, slot), out entityId);

    public IReadOnlyList<CellCoordinate> OccupiedCells(int entityId, CellCoordinate origin)
    {
        if (!TryGetOffsets(entityId, out var offsets)) return [];
        return offsets.Select(offset => new CellCoordinate(origin.X + offset.X, origin.Z + offset.Z)).ToArray();
    }
}
