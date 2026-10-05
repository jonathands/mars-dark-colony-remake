using System.Collections.Concurrent;
using DarkColony.Engine.Assets;
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
    NativeTargetRings TargetRings,
    TroopBuildTimings? BuildTimings = null,
    NativeVisionTrees? VisionTrees = null,
    NativeKrustyTables? KrustyTables = null,
    NativeFireMuzzles? FireMuzzles = null,
    WeaponExplosionCatalog? Explosions = null)
{
    // The FIN-derived tables are most of the loading time. The files do not
    // change while a process runs, so each installation reads them once.
    private static readonly ConcurrentDictionary<string, Lazy<(LoadedAnimations Animations, NativeFireMuzzles Muzzles)>> AnimationTables =
        new(StringComparer.OrdinalIgnoreCase);

    public static SimulationRules Load(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var gamestat = installation.DataFile("gamestat", "gamestat.txt");
        var entities = EntityCatalog.Load(gamestat);
        var weapons = WeaponCatalog.Load(installation.DataFile("gamestat", "weapstat.txt"));
        var areaEffects = AreaEffectCatalog.Load(installation.DataFile("gamestat", "boomstat.txt"));
        var animDat = installation.DataFile("anim.dat");
        var key = string.Join('|', Path.GetFullPath(installation.DataFile("animate")), File.GetLastWriteTimeUtc(animDat).Ticks,
            Path.GetFullPath(gamestat), File.GetLastWriteTimeUtc(gamestat).Ticks);
        var (animations, muzzles) = AnimationTables.GetOrAdd(key, _ => new Lazy<(LoadedAnimations, NativeFireMuzzles)>(() =>
        {
            var loaded = LoadedAnimations.Load(installation);
            var entityAnimations = EntityAnimationCatalog.Build(entities, installation.DataFile("animate"), EntityAnimationCatalog.LoadOrder(animDat));
            return (loaded, NativeFireMuzzles.Build(entities, entityAnimations, loaded));
        })).Value;
        return new SimulationRules(
            entities,
            weapons,
            areaEffects,
            DependencyCatalog.Load(installation.DataFile("gamestat", "depend.txt")),
            DamageMatrix.Load(installation.DataFile("gamestat", "mbullet.txt")),
            BuildingFootprintCatalog.Load(installation.ExecutablePath),
            NativeRandomTable.Load(installation.ExecutablePath),
            NativeTargetRings.Load(installation.ExecutablePath),
            TroopBuildTimings.From(entities, animations),
            NativeVisionTrees.Load(installation.ExecutablePath),
            NativeKrustyTables.Load(installation.ExecutablePath),
            muzzles,
            WeaponExplosionCatalog.Build(weapons, areaEffects, animations.Contains));
    }
}
