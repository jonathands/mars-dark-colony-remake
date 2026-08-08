using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DarkColony.Engine.Data;

public sealed record EntityDefinition(
    int Id,
    string Code,
    string DisplayName,
    int Faction,
    IReadOnlyList<int> Values)
{
    public int TurnSpeed => Values[1];
    public int MovementSpeed => Values[2];
    public int DayObservation => Values[3];
    public int NightObservation => Values[4];
    public int MovementClass => Values[10];
    // The original table slot also indexes the ten-column resistance matrix.
    // Keep MovementClass as the established pathing name until the distinct
    // runtime field is fully traced, but expose the combat interpretation
    // explicitly so callers cannot rely on a magic positional value.
    public int ArmorClass => Values[10];
    public int Health => Values[11];
    /// <summary>
    /// The shipped field 30 links equivalent Human/Gray records (for example
    /// SARG↔PSYC and BEON↔ZISP). It is a faction counterpart, not a deployed
    /// form or a state-transition target.
    /// </summary>
    public int FactionCounterpartEntityId => Values[30];
    public IReadOnlyList<int> WeaponSlots => Values.Skip(5).Take(3).ToArray();
}

public sealed class EntityCatalog
{
    private static readonly Regex ParenthesizedIdentity = new(
        @"^%\s*(.*?)\s*\((\d+)\)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex PlainIdentity = new(
        @"^%\s*(.*?)\s+(\d+)\s*$", RegexOptions.CultureInvariant);

    private EntityCatalog(IReadOnlyList<EntityDefinition> entities) => Entities = entities;

    public IReadOnlyList<EntityDefinition> Entities { get; }
    public EntityDefinition this[int id] => Entities[id];

    public static EntityCatalog Load(string path) =>
        Parse(File.ReadAllText(path, Encoding.Latin1));

    public static EntityCatalog Parse(string text)
    {
        var names = new Dictionary<int, string>();
        var logical = new List<string>();
        foreach (var sourceLine in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = sourceLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('%'))
            {
                var match = ParenthesizedIdentity.Match(line);
                if (!match.Success) match = PlainIdentity.Match(line);
                if (match.Success && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    names[id] = match.Groups[1].Value.Trim();
                continue;
            }

            logical.Add(line);
        }

        if (logical.Count == 0 || !int.TryParse(logical[0], NumberStyles.None, CultureInfo.InvariantCulture, out var declared))
            throw new InvalidDataException("gamestat.txt has no declared entity count.");
        if (logical.Count - 1 != declared)
            throw new InvalidDataException($"gamestat.txt declares {declared} entities but contains {logical.Count - 1}.");

        var entities = new EntityDefinition[declared];
        for (var id = 0; id < entities.Length; id++)
        {
            var words = logical[id + 1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 33) throw new InvalidDataException($"Entity {id} has {words.Length} fields; expected 33.");
            var values = new int[32];
            for (var field = 0; field < values.Length; field++)
                if (!int.TryParse(words[field + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[field]))
                    throw new InvalidDataException($"Entity {id} field {field + 1} is not an integer.");
            entities[id] = new EntityDefinition(id, words[0], names.GetValueOrDefault(id, words[0]), values[0], values);
        }

        return new EntityCatalog(entities);
    }
}
