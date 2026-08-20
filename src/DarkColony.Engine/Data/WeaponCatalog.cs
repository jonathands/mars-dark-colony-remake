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
    /// <summary>
    /// Source <c>shots</c> column. Despite that stale header name, loader and
    /// fire paths store it at weapon +0x1c and use it as a boomstat template.
    /// </summary>
    int AreaEffectTemplateId,
    /// <summary>
    /// Source <c>reload</c> column, stored at weapon +0x20 and used by
    /// 0x413181 as the burst-shot limit.
    /// </summary>
    int BurstShotLimit,
    /// <summary>
    /// Source <c>magic_chewing</c> column, stored at weapon +0x24 and selected
    /// as the cooldown after the final shot in a burst.
    /// </summary>
    int BurstReloadTicks,
    /// <summary>
    /// Source final flag, stored at native weapon byte +0x28. Common fire
    /// clears actor byte +0x0a after a shot when this is nonzero.
    /// </summary>
    int PostFireReset,
    /// <summary>
    /// Source penultimate column. Loader <c>0x43B7D2</c> stores it at runtime
    /// weapon byte <c>+0x44</c>; common fire <c>0x413130</c> passes that byte
    /// directly to projectile constructor <c>0x441710</c>, which stores it as
    /// projectile mode at record <c>+0x1E</c>. Modes 4-10 are the shipped
    /// Napalm/Disease, reinforcement-drop, and abduction impact families.
    /// </summary>
    int ProjectileMode)
{
    public bool HasAreaEffect => AreaEffectTemplateId > 0;
    /// <summary>
    /// Maximum native projectile age derived by <c>dc.exe 0x43B935</c> from
    /// weapon range and speed. Projectile update <c>0x4428FB</c> removes a
    /// still-flying shot after its age exceeds this value.
    /// </summary>
    public int ProjectileLifetimeTicks
    {
        get
        {
            if (ProjectileSpeed <= 0) return 1;
            var numerator = (((long)Range << 8) + 0x400) * 2 + 1;
            return checked((int)(numerator / (ProjectileSpeed * 2L) + 1));
        }
    }
}

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
                values[10], values[9]));
        }
        return new WeaponCatalog(weapons);
    }
}
