using DarkColony.Engine.Data;

namespace DarkColony.Engine.Assets;

/// <summary>
/// How long a building takes to produce each troop. The gamestat loader
/// (<c>0x43C18C</c>) stores the troop's <c>&lt;code&gt;BUILDSTAND</c> animation, or
/// else its <c>&lt;code&gt;BUILD</c> animation, at entity <c>+0x98</c>. Production
/// (<c>0x414314</c>) plays it once on the building, and the troop appears on
/// the tick the animation stops. A troop without one appears immediately.
/// </summary>
public sealed class TroopBuildTimings
{
    private readonly IReadOnlyDictionary<int, int> ticksByEntity;

    private TroopBuildTimings(IReadOnlyDictionary<int, int> ticksByEntity) => this.ticksByEntity = ticksByEntity;

    public static TroopBuildTimings Empty { get; } = new(new Dictionary<int, int>());

    /// <summary>Production ticks for an entity, or null when it has no build animation.</summary>
    public int? BuildTicks(int entityId) => ticksByEntity.TryGetValue(entityId, out var ticks) ? ticks : null;

    public static TroopBuildTimings FromTicks(IReadOnlyDictionary<int, int> ticksByEntity) => new(ticksByEntity);

    /// <summary>
    /// Reads the FIN files in <c>anim.dat</c> order, which is the order the game
    /// loads them; the first animation with a matching name wins.
    /// </summary>
    public static TroopBuildTimings Load(EntityCatalog entities, GameInstallation installation) =>
        From(entities, LoadedAnimations.Load(installation));

    /// <summary>Production ticks from the animations the game loads.</summary>
    public static TroopBuildTimings From(EntityCatalog entities, LoadedAnimations animations)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(animations);
        var ticks = new Dictionary<int, int>();
        foreach (var entity in entities.Entities)
        {
            var found = animations.Find(entity.Code + "BUILDSTAND0") ?? animations.Find(entity.Code + "BUILD0");
            if (found is not { } build || animations.Definition(build.Path) is not { } definition) continue;
            ticks[entity.Id] = PlayOnceTicks([.. Enumerable.Range(build.Animation.FirstFrame, build.Animation.LastFrame - build.Animation.FirstFrame + 1)
                .Select(frame => definition.LogicalFrames[frame].Delay)]);
        }
        return new TroopBuildTimings(ticks);
    }

    /// <summary>
    /// Ticks a one-shot animation runs. The frame loader <c>0x425674</c> turns
    /// each FIN frame delay <c>d</c> (0 meaning 15) into
    /// <c>(d + 3) * 15 / 100</c> ticks. The stepper <c>0x4264C8</c> advances
    /// past frame 0 on the first tick, holds each later frame for its ticks
    /// (a zero byte wraps to 256), and stops one step after the last frame.
    /// </summary>
    public static int PlayOnceTicks(IReadOnlyList<ushort> finDelays)
    {
        var ticks = 1;
        for (var frame = 1; frame < finDelays.Count; frame++) ticks += NativeAnimationTiming.FrameTicks(finDelays[frame]);
        return ticks;
    }
}
