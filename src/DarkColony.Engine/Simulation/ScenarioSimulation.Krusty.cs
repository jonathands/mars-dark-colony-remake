using DarkColony.Engine.Commands;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>The world as the Krusty planner (<see cref="KrustyBrain"/>) reads and commands it.</summary>
public sealed partial class ScenarioSimulation
{
    /// <summary>City slots per player; city buildings take actor slots <c>player * 15 + slot</c> (below <c>0x78</c>).</summary>
    internal const int NativeCitySlotsPerPlayer = 15;

    private NativeKrustyTables? krustyTables;
    private readonly Dictionary<int, CellCoordinate> krustyHomes = [];

    /// <summary>
    /// The global byte at <c>0x489510</c>: set when a guard group picks a new
    /// roaming post, read and cleared by the next army task's post, which then
    /// re-sends every member.
    /// </summary>
    private bool krustyRetarget;

    internal NativeKrustyTables? KrustyTables => krustyTables;
    internal void SetKrustyRetarget() => krustyRetarget = true;
    internal bool TakeKrustyRetarget()
    {
        var value = krustyRetarget;
        krustyRetarget = false;
        return value;
    }
    internal PathRegionMap KrustyRegions => path;
    internal DamageMatrix? KrustyDamage => damageMatrix;
    internal EntityDefinition KrustyEntity(int entityId) => EntityDefinitionFor(entityId);
    internal int KrustyEntityCount => entityDefinitions.Count;
    internal int KrustyRandom() => unchecked((int)NextNativeRandom());
    internal int TeamRace(int team) => teamRaces.GetValueOrDefault(team) ?? 0;
    internal WeaponDefinition? KrustyWeapon(int weaponId) =>
        weaponId >= 0 && weaponCatalog?.TryGet(weaponId, out var weapon) == true ? weapon : null;

    /// <summary>
    /// The actors in the executable's slot order: the city buildings first
    /// (slot <c>player * 15 + slot</c>), then the rest in creation order. The
    /// port appends new actors instead of reusing freed slots.
    /// </summary>
    internal IEnumerable<SimulatedActor> ActorsInNativeSlotOrder()
    {
        var city = new HashSet<int>();
        foreach (var ((team, slot), instanceId) in cityBuildings.OrderBy(pair => pair.Key.Team * NativeCitySlotsPerPlayer + pair.Key.Slot))
        {
            if (!actorsById.TryGetValue(instanceId, out var actor)) continue;
            city.Add(instanceId);
            yield return actor;
        }
        foreach (var actor in actors)
            if (!city.Contains(actor.Seed.InstanceId)) yield return actor;
    }

    internal bool IsCityBuildingActor(SimulatedActor actor) => cityBuildings.ContainsValue(actor.Seed.InstanceId);

    /// <summary>
    /// <c>0x457210</c>: the city origin (player <c>+0xBC4/+0xBC8</c>) when both
    /// coordinates are nonzero, otherwise the start point (<c>+0xBCC/+0xBD0</c>).
    /// </summary>
    internal CellCoordinate KrustyHome(int team) => krustyHomes.GetValueOrDefault(team);

    internal CellCoordinate? KrustyFreeCell(CellCoordinate origin) => FindNativeFreeCell(origin, 0);

    /// <summary>The relation matrix entry (<c>world + 0x46F34</c>): nonzero when cooperative.</summary>
    internal bool KrustyCooperative(int player, int team) => TeamRelations.Relation(player, team) != 0;

    /// <summary>Entity types in the player's four troop queues (player <c>+0xCB0 + queue * 0x320</c>).</summary>
    internal IEnumerable<int> QueuedTroopTypes(int team)
    {
        for (var queue = 0; queue < 4; queue++)
            if (productionQueues.TryGetValue((team, queue), out var state))
                foreach (var (_, entityId) in state.Items) yield return entityId;
    }

    /// <summary>The bottom of the actor's command stack is the idle command (<c>+0x39 == 1</c>).</summary>
    internal static bool KrustyIdle(SimulatedActor actor) => actor.IdleCommandActive;

    private void RecordKrustyHomes(ScenarioDefinition scenario)
    {
        foreach (var team in scenario.Teams)
        {
            if (team.TeamId is < 0 or >= PlayerCount) continue;
            var home = team.CityOrigin is { X: not 0, Z: not 0 } origin ? origin : team.StartPoint ?? default;
            krustyHomes[team.TeamId] = home;
        }
    }
}
