using System.Globalization;
using System.Text.RegularExpressions;

namespace DarkColony.Engine.Data;

/// <summary>Original <c>gamestat/weapstat.txt</c> records used by the firing path.</summary>
public sealed record WeaponDefinition(
    int Id,
    string Sprite,
    string DisplayName,
    int WeaponClass,
    int SoundId,
    int RateOfFire,
    int Damage,
    int ProjectileSpeed,
    int Range,
    int Shots,
    int Reload,
    /// <summary>
    /// Source column nine, named <c>magic_chewing</c> by the shipped
    /// <c>weapstat.txt</c> header. Its runtime meaning is not yet traced, so
    /// this is retained as data rather than made into a combat rule.
    /// </summary>
    int MagicChewing,
    /// <summary>
    /// Source column ten. Every nonzero shipped value resolves to an ID in
    /// <c>boomstat.txt</c> (artillery, mine, napalm, and packet records), so
    /// retain the recovered area-effect template identity independently from
    /// its still-untraced runtime application.
    /// </summary>
    int AreaEffectTemplateId,
    /// <summary>Source column eleven; its native runtime meaning is not yet named.</summary>
    int UnknownField11);

public sealed class WeaponCatalog
{
    private static readonly Regex NameLine = new(
        @"^%\s*(.*?)\s*\((\d+)\)\s*$", RegexOptions.CultureInvariant);

    private WeaponCatalog(IReadOnlyDictionary<int, WeaponDefinition> weapons) => Weapons = weapons;

    public IReadOnlyDictionary<int, WeaponDefinition> Weapons { get; }
    public bool TryGet(int id, out WeaponDefinition definition) => Weapons.TryGetValue(id, out definition!);

    public static WeaponCatalog Load(string path) => Parse(File.ReadAllText(path));

    public static WeaponCatalog Parse(string text)
    {
        var names = new Dictionary<int, string>();
        var records = new List<string>();
        foreach (var sourceLine in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = sourceLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('%'))
            {
                var match = NameLine.Match(line);
                if (match.Success) names[int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)] = match.Groups[1].Value.Trim();
                continue;
            }
            records.Add(line);
        }

        if (records.Count == 0 || !int.TryParse(records[0], NumberStyles.None, CultureInfo.InvariantCulture, out var declared))
            throw new InvalidDataException("weapstat.txt has no declared weapon count.");
        if (records.Count - 1 != declared)
            throw new InvalidDataException($"weapstat.txt declares {declared} weapons but contains {records.Count - 1}.");

        var weapons = new Dictionary<int, WeaponDefinition>();
        foreach (var line in records.Skip(1))
        {
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 13) throw new InvalidDataException($"Weapon record has {words.Length} fields; expected 13.");
            var id = int.Parse(words[0], CultureInfo.InvariantCulture);
            var values = words.Skip(2).Select(word => int.Parse(word, CultureInfo.InvariantCulture)).ToArray();
            weapons.Add(id, new WeaponDefinition(id, words[1], names.GetValueOrDefault(id, $"Weapon {id}"),
                values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8],
                values[9], values[10]));
        }
        return new WeaponCatalog(weapons);
    }
}
