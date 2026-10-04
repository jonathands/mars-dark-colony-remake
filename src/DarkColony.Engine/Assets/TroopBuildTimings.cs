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
    public static TroopBuildTimings Load(EntityCatalog entities, GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(installation);
        var frames = new Dictionary<string, IReadOnlyList<ushort>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(installation.DataFile("anim.dat")))
        {
            var fileName = line.Trim();
            if (fileName.Length == 0) continue;
            var path = installation.DataFile("animate", fileName);
            if (!File.Exists(path)) continue;
            var definition = AnimationDefinition.Load(path);
            foreach (var animation in definition.Animations)
            {
                if (frames.ContainsKey(animation.Name) || animation.LastFrame < animation.FirstFrame) continue;
                frames[animation.Name] = Enumerable.Range(animation.FirstFrame, animation.LastFrame - animation.FirstFrame + 1)
                    .Select(frame => definition.LogicalFrames[frame].Event)
                    .ToArray();
            }
        }

        var ticks = new Dictionary<int, int>();
        foreach (var entity in entities.Entities)
        {
            var delays = frames.GetValueOrDefault(entity.Code + "BUILDSTAND0") ?? frames.GetValueOrDefault(entity.Code + "BUILD0");
            if (delays is not null) ticks[entity.Id] = PlayOnceTicks(delays);
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
        for (var frame = 1; frame < finDelays.Count; frame++)
        {
            var frameTicks = (byte)(((finDelays[frame] == 0 ? 15 : finDelays[frame]) + 3) * 15 / 100);
            ticks += frameTicks == 0 ? 256 : frameTicks;
        }
        return ticks;
    }
}
