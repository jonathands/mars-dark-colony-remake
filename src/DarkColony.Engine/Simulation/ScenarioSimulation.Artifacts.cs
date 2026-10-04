using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// Artifact sites (POOP, entity 37) and their item containers: the global
/// list at <c>0x4FE454</c> (count <c>0x4796B4</c>), whose 0x34-byte entries
/// hold x, z, an item count, and ten item words.
/// </summary>
public sealed partial class ScenarioSimulation
{
    public const int ArtifactSiteEntity = 37;
    /// <summary>The site's idle record word restarts at 450 (<c>0x413333</c>).</summary>
    public const int NativeArtifactExcavationTicks = 0x1c2;
    /// <summary><c>artifact</c> adds entity <c>0x3F + r % 5</c> (<c>0x43E3E8</c>).</summary>
    private const int FirstArtifactEntity = 0x3f;
    private const int ArtifactSiteTeam = 8;
    /// <summary>Player <c>+0xBE4</c>: the health of city slot 4.</summary>
    private const int ArtifactResearchSlot = 4;
    private const int HumanHarvesterEntity = 6;
    private const int AlienHarvesterEntity = 14;

    private readonly List<ArtifactContainer> artifactContainers = [];

    /// <summary>Item containers in creation order.</summary>
    public IReadOnlyList<ArtifactContainer> ArtifactContainers => artifactContainers;

    /// <summary>
    /// <c>0x440410</c>. The original asserts beyond ten containers and then
    /// writes past the list; no corpus map has more than eight.
    /// </summary>
    private static void AddArtifactContainer(List<ArtifactContainer> containers, CellCoordinate cell) =>
        containers.Add(new ArtifactContainer(cell));

    /// <summary>
    /// <c>0x4404C0</c>: the first container at the cell takes the entity.
    /// Item counts are not bounded in the original either; the corpus puts at
    /// most seven items in one container.
    /// </summary>
    private static bool TryAddToArtifactContainer(List<ArtifactContainer> containers, CellCoordinate cell, int entityId)
    {
        var container = containers.FirstOrDefault(candidate => candidate.Cell == cell);
        if (container is null) return false;
        container.Add(entityId);
        return true;
    }

    /// <summary>
    /// The site's branch of the idle command (<c>0x4131BC</c>). While the
    /// ground grid at its cell holds an EXPL or SLUG whose bottom command is
    /// idle and whose player's city slot 4 is alive, a counter counts down and
    /// the harvester plays its digging animation (sound 0x5F). Otherwise the
    /// counter restarts at 450. At zero, the container's first item appears
    /// as a unit of the harvester's team (<c>0x41B634</c>); an empty or
    /// missing container removes the site instead.
    /// </summary>
    private void UpdateArtifactSite(SimulatedActor site, TickEvents events)
    {
        var cell = site.Movement.OccupiedCell;
        if (!GroundOccupancy.TryGetOwner(cell, out var occupantId) || !actorsById.TryGetValue(occupantId, out var harvester) ||
            !IsArtifactExcavator(harvester))
        {
            site.ArtifactExcavationTicks = NativeArtifactExcavationTicks;
            site.ArtifactExcavatorInstanceId = null;
            return;
        }
        site.ArtifactExcavatorInstanceId = harvester.Seed.InstanceId;
        // The record word is a signed 16-bit counter; the idle push leaves it at -1.
        if (--site.ArtifactExcavationTicks > 0) return;
        site.ArtifactExcavationTicks = NativeArtifactExcavationTicks;
        var container = artifactContainers.FirstOrDefault(candidate => candidate.Cell == cell);
        if (container is null || !container.TryTake(out var entityId))
        {
            site.ArtifactExcavatorInstanceId = null;
            RemoveActorFromWorld(site, carriedOff: false);
            events.ArtifactRecoveries.Add(new ArtifactRecoveryEvent(site.Seed.InstanceId, harvester.Seed.InstanceId, null, SiteDepleted: true));
            return;
        }
        var item = (uint)entityId < (uint)entityDefinitions.Count ? SpawnNativeUnit(entityId, harvester.Seed.Team, cell) : null;
        events.ArtifactRecoveries.Add(new ArtifactRecoveryEvent(site.Seed.InstanceId, harvester.Seed.InstanceId, item?.Seed.InstanceId, SiteDepleted: false));
    }

    /// <summary>
    /// Actor type byte 6 or 0xE (EXPL/SLUG, not their vent-deployed forms),
    /// bottom command idle (<c>+0x39 == 1</c>), and the player's slot 4 alive.
    /// Engine checks without declared cities skip the slot test.
    /// </summary>
    private bool IsArtifactExcavator(SimulatedActor harvester) =>
        EffectiveDefinition(harvester).Id is HumanHarvesterEntity or AlienHarvesterEntity && IsIdle(harvester) &&
        (!citiesDeclared || CityBuilding(harvester.Seed.Team, ArtifactResearchSlot) is not null);

    /// <summary>
    /// <c>artifact x z</c> (<c>0x43E3C7</c>): one draw from the shared stream
    /// picks entity 0x3F-0x43, which joins the container at (x, z). Without a
    /// container the original only reports an assertion.
    /// </summary>
    private void AddScriptArtifact(IReadOnlyList<int> values)
    {
        var entityId = FirstArtifactEntity + (int)(NextNativeRandom() % 5);
        TryAddToArtifactContainer(artifactContainers, new CellCoordinate(values[0], values[1]), entityId);
    }
}

/// <summary>One artifact container: a cell and the entities still buried there.</summary>
public sealed class ArtifactContainer(CellCoordinate cell)
{
    private readonly List<int> items = [];

    public CellCoordinate Cell { get; } = cell;
    public IReadOnlyList<int> Items => items;

    internal void Add(int entityId) => items.Add(entityId);

    /// <summary><c>0x440520</c>: removes and returns the first item.</summary>
    internal bool TryTake(out int entityId)
    {
        if (items.Count == 0)
        {
            entityId = -1;
            return false;
        }
        entityId = items[0];
        items.RemoveAt(0);
        return true;
    }
}

/// <summary>
/// An artifact site's countdown ended: either <see cref="ItemInstanceId"/> was
/// dug out (null when no cell was free), or the container was empty and the
/// site is gone.
/// </summary>
public sealed record ArtifactRecoveryEvent(int SiteInstanceId, int HarvesterInstanceId, int? ItemInstanceId, bool SiteDepleted);
