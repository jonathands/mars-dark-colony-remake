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
    /// <summary>
    /// Source value 22, loaded to entity runtime byte +0xe0. Actor constructor
    /// 0x41B2E7 copies it to facing byte +0x09.
    /// </summary>
    public byte InitialFacing => unchecked((byte)Values[21]);
    /// <summary>
    /// Source value 25, loaded to entity runtime +0xf8. Actor update 0x4192C0
    /// adds it to byte +0x0a every 32 world ticks, saturating at 255.
    /// </summary>
    public int AbilityChargeRecovery => Values[24];
    /// <summary>
    /// Shipped field 14 is tested before movement class when dc.exe assigns an
    /// actor to one of its three world-selection grids. Only the two HMINE
    /// definitions set it, placing those actors in the dedicated mine layer.
    /// </summary>
    public bool UsesNativeMineLayer => Values[14] != 0;
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
    /// <summary>
    /// Native runtime +0x104, tested by packet opcode 0x1a before entering
    /// actor state 13. The loader maps it exactly to gamestat value 27.
    /// </summary>
    public int ImmediateSpecialCode => Values[27];
    /// <summary>
    /// Native runtime <c>+0xfc</c>, loaded from gamestat value 25 and used as
    /// the generic state-13 area-effect gate. The loader converts the source
    /// percentage to 8.8 before storing it; command eligibility only needs its
    /// source-level zero/nonzero identity.
    /// </summary>
    public bool HasImmediateAreaEffect => Values[25] != 0;
    /// <summary>
    /// Native runtime <c>+0x100</c>, loaded directly from gamestat value 26.
    /// Commander Inspire passes it to the area scan as the maximum number of
    /// qualifying occupancy hits (6/8/10/12 across the four ranks).
    /// </summary>
    public int ImmediateAreaTargetLimit => Values[26];
    /// <summary>
    /// Native loader field +0x108. The loader derives its +0x10c command gate
    /// from whether this value is nonzero; opcode 0x1b tests that gate before
    /// placing an actor in ground-special state 18.
    /// </summary>
    public int GroundSpecialCapability => Values[28];
    /// <summary>
    /// Native runtime +0x110, read by actor state 18 and temporarily installed
    /// as the actor's weapon before the common ground-fire routine executes.
    /// </summary>
    public int GroundSpecialWeaponId => Values[29];
    public bool HasGroundSpecialAttack => GroundSpecialCapability != 0 && GroundSpecialWeaponId >= 0;
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
