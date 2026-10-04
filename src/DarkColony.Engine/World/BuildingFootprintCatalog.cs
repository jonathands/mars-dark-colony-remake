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
    // Slot -> player production queue (0x41AE30, read by 0x41AE6C); 4 = none.
    private const uint SlotQueueTableAddress = 0x41ae30;
    // Troop exit offsets from the city origin (0x41ADD0): per queue, three
    // (x, z) dword pairs selected by the troop's entity value 23.
    private const uint ProductionExitTableAddress = 0x41add0;
    private const int PatternCount = 15;
    private const int MaximumOffsets = 8;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<CellCoordinate>> byEntityId;
    private readonly IReadOnlyDictionary<(int Faction, int Variant, int Slot), int> buildEntityIds;
    private readonly IReadOnlyList<IReadOnlyList<CellCoordinate>> slotPatterns;
    private readonly IReadOnlyList<(int X, int Z)> slotPositionOffsets;
    private readonly IReadOnlyList<int> slotQueues;
    private readonly IReadOnlyList<(int X, int Z)> productionExits;

    private BuildingFootprintCatalog(
        IReadOnlyDictionary<int, IReadOnlyList<CellCoordinate>> byEntityId,
        IReadOnlyDictionary<(int Faction, int Variant, int Slot), int> buildEntityIds,
        IReadOnlyList<IReadOnlyList<CellCoordinate>> slotPatterns,
        IReadOnlyList<(int X, int Z)> slotPositionOffsets,
        IReadOnlyList<int> slotQueues,
        IReadOnlyList<(int X, int Z)> productionExits)
    {
        this.byEntityId = byEntityId;
        this.buildEntityIds = buildEntityIds;
        this.slotPatterns = slotPatterns;
        this.slotPositionOffsets = slotPositionOffsets;
        this.slotQueues = slotQueues;
        this.productionExits = productionExits;
    }

    /// <summary>Number of building slots in a city (<c>BUILDINGS_PER_SIDE</c>).</summary>
    public const int CitySlotCount = PatternCount;
    /// <summary>Production queues per player (player <c>+0xCA8 + queue * 2</c>).</summary>
    public const int ProductionQueueCount = 4;
    /// <summary>Exit offsets per queue, selected by entity value 23.</summary>
    public const int ProductionExitVariants = 3;

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
        var queueTable = image.AtVirtualAddress(SlotQueueTableAddress, PatternCount * 4);
        var queues = new int[PatternCount];
        for (var slot = 0; slot < PatternCount; slot++)
            queues[slot] = BinaryPrimitives.ReadInt32LittleEndian(queueTable.Slice(slot * 4, 4));
        var exitTable = image.AtVirtualAddress(ProductionExitTableAddress, ProductionQueueCount * ProductionExitVariants * 8);
        var exits = new (int X, int Z)[ProductionQueueCount * ProductionExitVariants];
        for (var index = 0; index < exits.Length; index++)
            exits[index] = (BinaryPrimitives.ReadInt32LittleEndian(exitTable.Slice(index * 8, 4)),
                BinaryPrimitives.ReadInt32LittleEndian(exitTable.Slice(index * 8 + 4, 4)));
        return new BuildingFootprintCatalog(resolved, resolvedBuildEntities, patterns, positions, queues, exits);
    }

    /// <summary>The production queue a city slot's building runs (<c>0x41AE6C</c>), or null if none.</summary>
    public int? SlotProductionQueue(int slot) =>
        (uint)slot < (uint)slotQueues.Count && (uint)slotQueues[slot] < ProductionQueueCount ? slotQueues[slot] : null;

    /// <summary>
    /// The cell where a queue's troops appear (<c>0x414314</c>): the city
    /// origin plus the <c>0x41ADD0</c> offset for the troop's exit variant.
    /// </summary>
    public CellCoordinate ProductionExit(CellCoordinate cityOrigin, int queue, int exitVariant)
    {
        var (x, z) = productionExits[queue * ProductionExitVariants + Math.Clamp(exitVariant, 0, ProductionExitVariants - 1)];
        return new CellCoordinate(cityOrigin.X + x, cityOrigin.Z + z);
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
