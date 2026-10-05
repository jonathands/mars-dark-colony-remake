using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// The fire a projectile of mode 4 (Napalm, weapon 50; Disease, weapon 51)
/// leaves where it lands (<c>docs/reverse-engineering/special-command-dispatch.md</c>,
/// "The burn"). The impact (<c>0x441C7B</c>) sets state 3 instead of
/// exploding once, and each substep of the projectile update then runs
/// <c>0x4421B8</c> in its place.
/// </summary>
public sealed partial class ScenarioSimulation
{
    /// <summary>
    /// The ground grid's occupant for a burning empty cell (<c>0x3FE</c>,
    /// <c>0x44232D</c>). It is never an instance id, so nothing walks into
    /// the fire.
    /// </summary>
    public const int BurningCellOccupant = -0x3FE;

    /// <summary>The burn ends at its first working substep past this count (<c>0x442262</c>): about 211 updates.</summary>
    public const int NativeBurnSubsteps = 0x348;

    // 0x442343: an actor in the fire is hit when the count's low six bits
    // are zero, every 16 updates.
    private const int NativeBurnDamageMask = 0x3F;

    /// <summary>
    /// One substep of a burn. Only every fourth does work (<c>0x4421C7</c>).
    /// It walks the boom template's square around the projectile's cell, x
    /// outer and z inner, over the ground grid:
    /// <list type="bullet">
    /// <item>an empty cell is marked burning;</item>
    /// <item>an actor's cell, on a damage substep, hits that actor with the
    /// weapon at the cell's template weight (<c>0x441930</c>, no three-quarter
    /// rule, no same-team scaling), so a building is hit once per cell;</item>
    /// <item>once the count passes <see cref="NativeBurnSubsteps"/>, burning
    /// marks are cleared instead, and the projectile is removed (state 4).</item>
    /// </list>
    /// The first working substep (count 0) also starts sound 63, which the
    /// port does not play.
    /// </summary>
    /// <returns>Whether the burn is over.</returns>
    private bool UpdateBurn(ProjectileState projectile, WeaponDefinition? weapon, ICollection<DestroyedActorEvent> destroyed)
    {
        var count = projectile.NextBurnSubstep();
        if ((count & 3) != 0) return false;
        if (weapon is null || areaEffects?.TryGet(weapon.AreaEffectTemplateId, out var effect) != true) return true;
        var ending = count > NativeBurnSubsteps;
        var radius = effect.PatternSize / 2;
        var center = projectile.Position.Cell;
        int? team = actorsById.TryGetValue(projectile.SourceActorInstanceId, out var source) ? source.Seed.Team : null;
        for (var x = center.X - radius; x <= center.X + radius; x++)
        for (var z = center.Z - radius; z <= center.Z + radius; z++)
        {
            if (x < 0 || z < 0 || x >= path.Width || z >= path.Height) continue;
            var cell = new CellCoordinate(x, z);
            var occupied = GroundOccupancy.TryGetOwner(cell, out var occupant);
            if (ending)
            {
                if (occupied && occupant == BurningCellOccupant) GroundOccupancy.ReleaseCell(cell);
                continue;
            }
            if (!occupied)
            {
                GroundOccupancy.ReplaceClaims(BurningCellOccupant, [cell]);
                continue;
            }
            if (occupant == BurningCellOccupant || (count & NativeBurnDamageMask) != 0 ||
                !actorsById.TryGetValue(occupant, out var actor) || actor.IsDestroyed) continue;
            // The loader keeps each template weight as (percent << 8) / 100 (0x43B55A).
            var weight = (effect.DamagePattern[z - center.Z + radius][x - center.X + radius] << 8) / 100;
            var damage = damageMatrix is null
                ? weapon.Damage * weight >> 8
                : damageMatrix.CalculateNativeDamage(weapon.Damage, weapon.WeaponClass, EffectiveDefinition(actor).ArmorClass, weight, ArmorFactor(actor));
            ApplyDamage(actor, Math.Max(0, damage), destroyed, team);
        }
        return ending;
    }
}
