using DarkColony.Engine.Data;

namespace DarkColony.Engine.Assets;

/// <summary>Resolves the original weapon-prefix BULLET family across FIN files.</summary>
public sealed record WeaponEffectCandidate(string FinPath, string AnimationName, ushort FirstFrame, ushort LastFrame);

public sealed class WeaponEffectCatalog
{
    private readonly IReadOnlyDictionary<int, WeaponEffectCandidate> bullets;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<WeaponEffectCandidate?>> explosions;

    private WeaponEffectCatalog(
        IReadOnlyDictionary<int, WeaponEffectCandidate> bullets,
        IReadOnlyDictionary<int, IReadOnlyList<WeaponEffectCandidate?>> explosions)
    {
        this.bullets = bullets;
        this.explosions = explosions;
    }

    public WeaponEffectCandidate? Bullet(int weaponId) => bullets.GetValueOrDefault(weaponId);

    /// <summary>
    /// The explosion an impact plays: slot <paramref name="variant"/> of the
    /// weapon's <see cref="WeaponExplosionCatalog"/> names (the impact
    /// event's draw), or null when there is none.
    /// </summary>
    public WeaponEffectCandidate? Explosion(int weaponId, int variant) =>
        explosions.TryGetValue(weaponId, out var list) && (uint)variant < (uint)list.Count ? list[variant] : null;

    /// <param name="spriteExists">
    /// Whether a sprite is shipped. The installation keeps FIN files whose
    /// sprites are not (pust.fin's PUSEXPLODE names a pust.spr that is on
    /// neither the disk nor the CD); such a file cannot be the game's effect.
    /// </param>
    /// <param name="explosionNames">The weapons' explosion animations; without it no weapon has one.</param>
    /// <param name="loadOrder">
    /// The <c>anim.dat</c> file order. An explosion name resolves to its first
    /// loaded animation (NUKE is in nuke.fin, which the game loads, and in
    /// effects.fin, which it does not).
    /// </param>
    public static WeaponEffectCatalog Build(WeaponCatalog weapons, string animateDirectory, Func<string, bool>? spriteExists = null,
        WeaponExplosionCatalog? explosionNames = null, IReadOnlyList<string>? loadOrder = null)
    {
        var all = new List<(string Path, AnimationRange Animation)>();
        foreach (var path in Directory.GetFiles(animateDirectory, "*.fin", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var definition = AnimationDefinition.Load(path);
                if (spriteExists is not null && !definition.SpriteNames.Where(name => name.Length != 0).All(spriteExists)) continue;
                all.AddRange(definition.Animations.Select(animation => (path, animation)));
            }
            catch (InvalidDataException) { }
        }

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (loadOrder is not null)
            for (var index = 0; index < loadOrder.Count; index++) rank.TryAdd(Path.GetFileName(loadOrder[index]), index);
        var loaded = all
            .Where(item => loadOrder is null || rank.ContainsKey(Path.GetFileName(item.Path)))
            .OrderBy(item => rank.GetValueOrDefault(Path.GetFileName(item.Path), int.MaxValue))
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var resolved = new Dictionary<int, WeaponEffectCandidate>();
        var resolvedExplosions = new Dictionary<int, IReadOnlyList<WeaponEffectCandidate?>>();
        foreach (var weapon in weapons.Weapons.Values)
        {
            if (FindEffect(all, weapon.Sprite + "BULLET") is { } bullet)
                resolved[weapon.Id] = new WeaponEffectCandidate(bullet.Path, bullet.Animation.Name, bullet.Animation.FirstFrame, bullet.Animation.LastFrame);
            var names = explosionNames?.Names(weapon.Id) ?? [];
            if (names.Count == 0) continue;
            resolvedExplosions[weapon.Id] = [.. names.Select(name =>
                loaded.FirstOrDefault(item => item.Animation.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Animation: not null } found
                    ? new WeaponEffectCandidate(found.Path, found.Animation.Name, found.Animation.FirstFrame, found.Animation.LastFrame)
                    : null)];
        }
        return new WeaponEffectCatalog(resolved, resolvedExplosions);
    }

    private static (string Path, AnimationRange Animation)? FindEffect(
        IEnumerable<(string Path, AnimationRange Animation)> all,
        string prefix)
    {
        var match = all.Where(item => item.Animation.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Animation.Name.Equals(prefix + "0", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Animation.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return match.Animation is null ? null : match;
    }
}
