using DarkColony.Engine.Data;

namespace DarkColony.Engine.Assets;

/// <summary>Resolves the original weapon-prefix BULLET family across FIN files.</summary>
public sealed record WeaponEffectCandidate(string FinPath, string AnimationName, ushort FirstFrame, ushort LastFrame);

public sealed class WeaponEffectCatalog
{
    private readonly IReadOnlyDictionary<int, WeaponEffectCandidate> bullets;
    private readonly IReadOnlyDictionary<int, WeaponEffectCandidate> impacts;

    private WeaponEffectCatalog(
        IReadOnlyDictionary<int, WeaponEffectCandidate> bullets,
        IReadOnlyDictionary<int, WeaponEffectCandidate> impacts)
    {
        this.bullets = bullets;
        this.impacts = impacts;
    }

    public WeaponEffectCandidate? Bullet(int weaponId) => bullets.GetValueOrDefault(weaponId);
    /// <summary>Resolved original <c>EXPLODE</c>/<c>EXPL</c> effect, if shipped.</summary>
    public WeaponEffectCandidate? Impact(int weaponId) => impacts.GetValueOrDefault(weaponId);

    /// <param name="spriteExists">
    /// Whether a sprite is shipped. The installation keeps FIN files whose
    /// sprites are not (pust.fin's PUSEXPLODE names a pust.spr that is on
    /// neither the disk nor the CD); such a file cannot be the game's effect.
    /// </param>
    public static WeaponEffectCatalog Build(WeaponCatalog weapons, string animateDirectory, Func<string, bool>? spriteExists = null)
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

        var resolved = new Dictionary<int, WeaponEffectCandidate>();
        var resolvedImpacts = new Dictionary<int, WeaponEffectCandidate>();
        foreach (var weapon in weapons.Weapons.Values)
        {
            if (FindEffect(all, weapon.Sprite + "BULLET") is { } bullet)
                resolved[weapon.Id] = new WeaponEffectCandidate(bullet.Path, bullet.Animation.Name, bullet.Animation.FirstFrame, bullet.Animation.LastFrame);
            if ((FindEffect(all, weapon.Sprite + "EXPLODE") ?? FindEffect(all, weapon.Sprite + "EXPL")) is { } impact)
                resolvedImpacts[weapon.Id] = new WeaponEffectCandidate(impact.Path, impact.Animation.Name, impact.Animation.FirstFrame, impact.Animation.LastFrame);
        }
        return new WeaponEffectCatalog(resolved, resolvedImpacts);
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
