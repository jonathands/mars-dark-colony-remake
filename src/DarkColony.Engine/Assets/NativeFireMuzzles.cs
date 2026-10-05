using DarkColony.Engine.Data;

namespace DarkColony.Engine.Assets;

/// <summary>Where one projectile of a shot leaves: an 8.8 offset from the shooter, and the updates it waits first.</summary>
public readonly record struct FireMuzzle(int XRaw, int ZRaw, int DelayTicks);

/// <summary>
/// The launch points of each fire animation. Common fire (<c>0x412E13</c>)
/// puts the chosen fire family on the shooter for its direction, then
/// <c>0x4263D8</c> walks that animation's frames: every frame whose hotspot 7
/// names a loaded animation adds a projectile leaving at
/// <c>(x * 8, -y * 8)</c> from the shooter, after the ticks of the frames
/// before it. With none, one projectile leaves the shooter's centre at once.
/// The shipped animations have at most one such frame (ATRIL, BARR, SCYT,
/// TURR and the deployed XENO).
/// </summary>
public sealed class NativeFireMuzzles
{
    /// <summary>The hotspot slot common fire asks for (<c>0x412E66</c> pushes 7).</summary>
    public const int MuzzleHotspot = 7;

    private readonly IReadOnlyDictionary<int, int> variantCounts;
    private readonly IReadOnlyDictionary<(int EntityId, int Sector, int Variant), FireMuzzle[]> muzzles;

    private NativeFireMuzzles(
        IReadOnlyDictionary<int, int> variantCounts,
        IReadOnlyDictionary<(int EntityId, int Sector, int Variant), FireMuzzle[]> muzzles)
    {
        this.variantCounts = variantCounts;
        this.muzzles = muzzles;
    }

    public static NativeFireMuzzles Empty { get; } = new(new Dictionary<int, int>(), new Dictionary<(int, int, int), FireMuzzle[]>());

    /// <summary>The entity's fire families (runtime <c>+0xE4</c>); 0 without a fire animation.</summary>
    public int VariantCount(int entityId) => variantCounts.GetValueOrDefault(entityId);

    /// <summary>
    /// The launch points of a shot by an entity facing <paramref name="sector"/>
    /// whose presentation draw is <paramref name="variantRoll"/> (taken modulo
    /// its family count). Empty when the animation has none.
    /// </summary>
    public IReadOnlyList<FireMuzzle> Muzzles(int entityId, int sector, int variantRoll)
    {
        var count = VariantCount(entityId);
        return count == 0 ? [] : muzzles.GetValueOrDefault((entityId, sector, Math.Abs(variantRoll % count)), []);
    }

    /// <summary>Reads the fire animations the way <see cref="EntityAnimationCatalog.PreferredFire(int, int, int)"/> selects them.</summary>
    public static NativeFireMuzzles Build(EntityCatalog entities, EntityAnimationCatalog animations, LoadedAnimations loaded)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(animations);
        ArgumentNullException.ThrowIfNull(loaded);
        var counts = new Dictionary<int, int>();
        var result = new Dictionary<(int, int, int), FireMuzzle[]>();
        var unloaded = new Dictionary<string, AnimationDefinition?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entities.Entities)
        {
            var count = animations.FireVariantCount(entity.Id);
            if (count == 0) continue;
            counts[entity.Id] = count;
            for (var variant = 0; variant < count; variant++)
            for (var sector = 0; sector < 16; sector++)
            {
                if (animations.PreferredFire(entity.Id, sector, variant) is not { } selection ||
                    Definition(selection.Candidate.FinPath) is not { } definition) continue;
                var found = new List<FireMuzzle>();
                var delay = 0;
                for (var frame = selection.Candidate.FirstFrame; frame <= selection.Candidate.LastFrame && frame < definition.LogicalFrames.Count; frame++)
                {
                    var logical = definition.LogicalFrames[frame];
                    if (logical.Hotspots.Count > MuzzleHotspot && logical.Hotspots[MuzzleHotspot] is { } muzzle && loaded.Contains(muzzle.Name))
                        found.Add(new FireMuzzle(muzzle.X * 8, -muzzle.Y * 8, delay));
                    delay += NativeAnimationTiming.FrameTicks(logical.Delay);
                }
                if (found.Count != 0) result[(entity.Id, sector, variant)] = [.. found];
            }
        }
        return new NativeFireMuzzles(counts, result);

        AnimationDefinition? Definition(string path)
        {
            if (loaded.Definition(path) is { } definition) return definition;
            if (unloaded.TryGetValue(path, out var cached)) return cached;
            try { cached = AnimationDefinition.Load(path); }
            catch (InvalidDataException) { cached = null; }
            catch (IOException) { cached = null; }
            unloaded[path] = cached;
            return cached;
        }
    }
}
