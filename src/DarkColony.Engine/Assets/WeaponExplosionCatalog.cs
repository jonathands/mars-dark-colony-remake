using DarkColony.Engine.Data;

namespace DarkColony.Engine.Assets;

/// <summary>
/// The explosions a weapon's projectiles end in: weapon <c>+0x30</c>, with
/// their count at <c>+0x40</c>. The weapon loader (<c>0x43B88D</c>-<c>0x43B932</c>)
/// takes <c>&lt;sprite&gt;EXPLODE</c>, else <c>&lt;sprite&gt;EXPL</c>, when the
/// family's frame-0 animation is loaded. A nonzero boom template's effects
/// (<c>boomstat.txt</c>, at most four, each an animation of that exact name:
/// <c>0x43B4A0</c>) then take their place. An impact picks one of them with a
/// draw from the shared stream (see <c>docs/reverse-engineering/combat-damage.md</c>).
/// </summary>
public sealed class WeaponExplosionCatalog
{
    /// <summary>The boom template loop (<c>0x43B4ED</c>) reads at most four effects.</summary>
    public const int MaximumVariants = 4;

    private readonly IReadOnlyDictionary<int, IReadOnlyList<string>> names;

    private WeaponExplosionCatalog(IReadOnlyDictionary<int, IReadOnlyList<string>> names) => this.names = names;

    public static WeaponExplosionCatalog Empty { get; } = new(new Dictionary<int, IReadOnlyList<string>>());

    /// <summary>The weapon's explosion animations in slot order; empty when it has none.</summary>
    public IReadOnlyList<string> Names(int weaponId) => names.GetValueOrDefault(weaponId, []);

    /// <summary>Weapon <c>+0x40</c>: how many explosions an impact chooses from.</summary>
    public int Count(int weaponId) => Names(weaponId).Count;

    public static WeaponExplosionCatalog Build(WeaponCatalog weapons, AreaEffectCatalog areaEffects, Func<string, bool> animationLoaded)
    {
        ArgumentNullException.ThrowIfNull(weapons);
        ArgumentNullException.ThrowIfNull(areaEffects);
        ArgumentNullException.ThrowIfNull(animationLoaded);
        var result = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var weapon in weapons.Weapons.Values)
        {
            IReadOnlyList<string> explosions =
                animationLoaded(weapon.Sprite + "EXPLODE0") ? [weapon.Sprite + "EXPLODE0"]
                : animationLoaded(weapon.Sprite + "EXPL0") ? [weapon.Sprite + "EXPL0"]
                : [];
            if (weapon.AreaEffectTemplateId > 0 && areaEffects.TryGet(weapon.AreaEffectTemplateId, out var template) && template.EffectNames.Count != 0)
                explosions = [.. template.EffectNames.Take(MaximumVariants)];
            if (explosions.Count != 0) result[weapon.Id] = explosions;
        }
        return new WeaponExplosionCatalog(result);
    }
}
