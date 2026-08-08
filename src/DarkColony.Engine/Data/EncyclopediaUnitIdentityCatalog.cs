namespace DarkColony.Engine.Data;

/// <summary>
/// Explicit bridge from the original encyclopedia resource stems to the
/// corresponding mobile/runtime entity IDs. The encyclopedia's implicit IDs
/// are screen-local and must not be mistaken for <c>gamestat.txt</c> IDs.
/// </summary>
public static class EncyclopediaUnitIdentityCatalog
{
    private static readonly IReadOnlyDictionary<string, int> byResourceStem =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["gray"] = 8, ["atril"] = 11, ["syth"] = 10, ["ortu"] = 13,
            ["psych"] = 12, ["slug"] = 14, ["xeno"] = 9, ["slom"] = 44,
            ["sauc"] = 93, ["zisp"] = 50,
            ["troop"] = 0, ["barr"] = 3, ["reap"] = 2, ["scgm"] = 5,
            ["cybo"] = 4, ["expl"] = 6, ["turr"] = 1, ["engi"] = 43,
            ["drop"] = 92, ["beon"] = 49,
        };

    public static bool TryGetEntityId(EncyclopediaEntry entry, out int entityId)
    {
        var stem = Path.GetFileName(entry.ResourceStem);
        return byResourceStem.TryGetValue(stem, out entityId);
    }
}
