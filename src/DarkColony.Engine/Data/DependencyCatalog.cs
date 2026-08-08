using System.Globalization;

namespace DarkColony.Engine.Data;

/// <summary>
/// A purchasable item from the original <c>gamestat/depend.txt</c> build tree.
/// <para>
/// The first four fields and the kind-specific parameter block are data-derived.
/// The remaining values before the <c>-1</c> sentinel are prerequisite item IDs.
/// Names and the detailed meaning of each parameter await executable tracing, so
/// consumers should use <see cref="Cost"/>, <see cref="TroopEntityId"/>, and
/// <see cref="PrerequisiteItemIds"/> rather than positional raw values.
/// </para>
/// </summary>
/// <summary>
/// Runtime meaning of a kind-2 dependency.  The packed kind/category/level
/// fields alone are not sufficient: the shipped Cyborg entry 80 uses the
/// same numeric shape as a weapon upgrade but its authored UI names the
/// Napalm/cruise-missile ability.
/// </summary>
public enum ResearchEffectKind
{
    WeaponLevel,
    ArmorLevel,
    Ability,
}

public sealed record DependencyDefinition(
    int Id,
    int Cost,
    int UiId,
    int Kind,
    IReadOnlyList<int> Parameters,
    IReadOnlyList<int> PrerequisiteItemIds)
{
    public bool IsBuilding => Kind == 0;
    public bool IsTroop => Kind == 1;
    public bool IsUpgrade => Kind == 2;
    /// <summary>Executable build-table slot used by a building record.</summary>
    public int? BuildingSlot => IsBuilding ? Parameters[0] : null;
    /// <summary>Executable build-table variant used by a building record.</summary>
    public int? BuildingVariant => IsBuilding ? Parameters[1] : null;
    /// <summary>Human (0) or Gray (1) build-table set used by a building record.</summary>
    public int? BuildingFaction => IsBuilding ? Parameters[2] : null;
    /// <summary>
    /// The direct entity reference carried by troop records. Building records use
    /// a type/level/faction tuple instead; resolve that through recovered build
    /// tables rather than treating their first parameter as an entity ID.
    /// </summary>
    public int? TroopEntityId => IsTroop ? Parameters[0] : null;
    /// <summary>Entity whose weapon/armor capability is targeted by an upgrade record.</summary>
    public int? UpgradeEntityId => IsUpgrade ? Parameters[0] : null;
    /// <summary>Original upgrade category: 0 weapon, 1 armor.</summary>
    public int? UpgradeCategory => IsUpgrade ? Parameters[1] : null;
    public int? UpgradeLevel => IsUpgrade ? Parameters[2] : null;
    /// <summary>
    /// Semantic research identity where the recovered UI establishes one.
    /// Do not use <see cref="UpgradeCategory"/> as a combat stat rule without
    /// checking this value.
    /// </summary>
    public ResearchEffectKind? ResearchEffect => !IsUpgrade ? null :
        (UiId == 131 && UpgradeEntityId == 4 || UiId == 78 && UpgradeEntityId == 12) &&
        UpgradeCategory == 0 && UpgradeLevel == 2
            ? ResearchEffectKind.Ability
            : UpgradeCategory == 0 ? ResearchEffectKind.WeaponLevel : ResearchEffectKind.ArmorLevel;

    public bool IsStatUpgrade => ResearchEffect is ResearchEffectKind.WeaponLevel or ResearchEffectKind.ArmorLevel;
}

/// <summary>Decoder for the single-resource production and prerequisite catalog.</summary>
public sealed class DependencyCatalog
{
    private DependencyCatalog(IReadOnlyDictionary<int, DependencyDefinition> items) => Items = items;

    public IReadOnlyDictionary<int, DependencyDefinition> Items { get; }
    public bool TryGet(int id, out DependencyDefinition definition) => Items.TryGetValue(id, out definition!);

    public static DependencyCatalog Load(string path) => Parse(File.ReadAllText(path));

    public static DependencyCatalog Parse(string text)
    {
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length != 0 && !line.StartsWith('%'))
            .ToArray();
        if (lines.Length == 0 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var declared))
            throw new InvalidDataException("depend.txt has no declared item count.");

        var items = new Dictionary<int, DependencyDefinition>();
        foreach (var line in lines.Skip(1))
        {
            var values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            var sentinel = Array.IndexOf(values, -1);
            if (sentinel < 0) throw new InvalidDataException("Dependency record has no -1 sentinel.");
            if (sentinel != values.Length - 1) throw new InvalidDataException("Dependency record has data after its -1 sentinel.");
            if (values.Length < 5) throw new InvalidDataException("Dependency record is too short.");

            var kind = values[3];
            var prefixLength = kind switch
            {
                0 or 2 => 7,
                1 => 5,
                _ => throw new InvalidDataException($"Dependency item {values[0]} has unrecognized kind {kind}.")
            };
            if (sentinel < prefixLength)
                throw new InvalidDataException($"Dependency item {values[0]} is missing its kind {kind} parameter block.");

            var item = new DependencyDefinition(
                values[0], values[1], values[2], kind,
                values[4..prefixLength], values[prefixLength..sentinel]);
            if (!items.TryAdd(item.Id, item)) throw new InvalidDataException($"Duplicate dependency item {item.Id}.");
        }

        // The shipped file's declared count excludes two active extension IDs (83, 84).
        // It is therefore a record-count check, not an ID range constraint.
        if (items.Count < declared) throw new InvalidDataException($"depend.txt declares {declared} items but contains only {items.Count} active records.");
        return new DependencyCatalog(items);
    }
}
