using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// Every installation-wide rule table a scenario simulation consumes. The game
/// host and the engine checks both build simulations from this one bundle, so
/// tests exercise exactly the catalogs the playable port runs with.
/// </summary>
public sealed record SimulationRules(
    EntityCatalog Entities,
    WeaponCatalog Weapons,
    AreaEffectCatalog AreaEffects,
    DependencyCatalog Dependencies,
    DamageMatrix DamageMatrix,
    BuildingFootprintCatalog Footprints,
    NativeRandomTable RandomTable,
    NativeTargetRings TargetRings)
{
    public static SimulationRules Load(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        return new SimulationRules(
            EntityCatalog.Load(installation.DataFile("gamestat", "gamestat.txt")),
            WeaponCatalog.Load(installation.DataFile("gamestat", "weapstat.txt")),
            AreaEffectCatalog.Load(installation.DataFile("gamestat", "boomstat.txt")),
            DependencyCatalog.Load(installation.DataFile("gamestat", "depend.txt")),
            DamageMatrix.Load(installation.DataFile("gamestat", "mbullet.txt")),
            BuildingFootprintCatalog.Load(installation.ExecutablePath),
            NativeRandomTable.Load(installation.ExecutablePath),
            NativeTargetRings.Load(installation.ExecutablePath));
    }
}
