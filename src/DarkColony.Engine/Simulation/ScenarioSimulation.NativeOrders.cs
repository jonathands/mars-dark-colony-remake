using DarkColony.Engine.Commands;
using DarkColony.Engine.Data;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// The network commands a computer player sends: 9 (build, <c>0x41C8D4</c>,
/// sent by <c>0x40C13C</c>), 10 (troop order, <c>0x41C7F8</c>, <c>0x40C168</c>),
/// and the unit orders of <c>0x40C414</c> (command 7 waypoints followed by
/// command 5 per unit). The sender deducts any price itself when it sends;
/// the commands run at the next update's command phase, in the order sent,
/// before the local player's commands.
/// </summary>
public sealed partial class ScenarioSimulation
{
    private readonly List<object> pendingNativeOrders = [];

    private readonly record struct NativeBuildOrder(int Team, int ItemId);

    private readonly record struct NativeTroopOrder(int Team, int ItemId, int EntityId, int Count);

    /// <summary>Queues command 9 for a building item.</summary>
    internal void QueueNativeBuild(int team, int itemId) => pendingNativeOrders.Add(new NativeBuildOrder(team, itemId));

    /// <summary>Queues command 10: <paramref name="count"/> copies of the troop's entity type.</summary>
    internal void QueueNativeTroop(int team, int itemId, int entityId, int count = 1) =>
        pendingNativeOrders.Add(new NativeTroopOrder(team, itemId, entityId, count));

    /// <summary>Queues a unit order (commands 7 and 5) as the equivalent port command.</summary>
    internal void QueueNativeUnitOrder(WorldCommand command) => pendingNativeOrders.Add(command);

    /// <summary>Deducts a price the way the planner's purchases do: only when the player can pay it.</summary>
    internal bool TrySpendP7(int team, int amount) =>
        teamEconomies.TryGetValue(team, out var economy) && economy.TrySpend(amount);

    /// <summary>The player's P7 (<c>+0xBAC</c>).</summary>
    internal int P7(int team) => teamEconomies.TryGetValue(team, out var economy) ? economy.P7 : 0;

    /// <summary>
    /// Runs the purchases queued during the previous update, in order, and
    /// returns the unit orders, which the caller dispatches like player commands.
    /// </summary>
    private List<WorldCommand> ApplyNativeOrders(TickEvents events)
    {
        var unitOrders = new List<WorldCommand>();
        if (pendingNativeOrders.Count == 0) return unitOrders;
        var orders = pendingNativeOrders.ToArray();
        pendingNativeOrders.Clear();
        foreach (var order in orders)
        {
            switch (order)
            {
                case WorldCommand command:
                    unitOrders.Add(command);
                    break;
                case NativeTroopOrder troop:
                    // Command 10 appends count copies of the type to its queue
                    // (entity value 20) without any check.
                    if (!productionQueues.TryGetValue((troop.Team, EntityDefinitionFor(troop.EntityId).ProductionQueue), out var queue)) break;
                    for (var copy = 0; copy < troop.Count; copy++) queue.Items.Add((troop.ItemId, troop.EntityId));
                    break;
                case NativeBuildOrder build when dependencyCatalog?.TryGet(build.ItemId, out var item) == true && item.IsBuilding:
                    // Command 9: a slot that already holds this variant at full
                    // health refunds the price; otherwise the slot is rebuilt.
                    var slot = item.BuildingSlot!.Value;
                    if (CityBuilding(build.Team, slot) is { } current && CitySlotVariant(build.Team, slot) == item.BuildingVariant &&
                        current.Health >= current.MaximumHealth)
                    {
                        if (teamEconomies.TryGetValue(build.Team, out var economy)) economy.AddP7(item.Cost);
                        break;
                    }
                    if (BuildCitySlot(build.Team, item) is { } built) events.BuildingPlacements.Add(built);
                    break;
            }
        }
        return unitOrders;
    }

    /// <summary>
    /// <c>0x438220</c>: 0 when the player's city slot already holds a live
    /// building of at least the item's variant, 1 when the item can be
    /// built (slot and variant out), and 2 otherwise: an unknown, disabled,
    /// or non-building item, another race's item, or a prerequisite that is
    /// not itself at 0. A slot whose new building is still rising
    /// (<c>+0xC10</c>) also gives 2; the port builds at once and has no such state.
    /// </summary>
    internal int NativeBuildingStatus(int team, int itemId, out int slot, out int variant)
    {
        slot = 0;
        variant = 0;
        if (dependencyCatalog?.TryGet(itemId, out var item) != true || !teamEconomies.TryGetValue(team, out var economy) ||
            economy.DisabledItems.Contains(itemId) || !item.IsBuilding || teamRaces.GetValueOrDefault(team) != item.BuildingFaction) return 2;
        var itemSlot = item.BuildingSlot!.Value;
        var itemVariant = item.BuildingVariant!.Value;
        if (CityBuilding(team, itemSlot) is not null && CitySlotVariant(team, itemSlot) is { } live && itemVariant <= live) return 0;
        foreach (var prerequisite in item.PrerequisiteItemIds)
            if (prerequisite == itemId || NativeBuildingStatus(team, prerequisite, out _, out _) != 0) return 2;
        slot = itemSlot;
        variant = itemVariant;
        return 1;
    }

    /// <summary>
    /// <c>0x43839C</c>: a known, enabled troop item of the player's race whose
    /// prerequisites are all built (<see cref="NativeBuildingStatus"/> 0).
    /// It does not look at P7.
    /// </summary>
    internal bool NativeTroopBuildable(int team, int itemId, out int entityId, out int cost)
    {
        entityId = 0;
        cost = 0;
        if (dependencyCatalog?.TryGet(itemId, out var item) != true || !teamEconomies.TryGetValue(team, out var economy) ||
            economy.DisabledItems.Contains(itemId) || !item.IsTroop || item.TroopEntityId is not { } troop ||
            (uint)troop >= (uint)entityDefinitions.Count || EntityDefinitionFor(troop).Faction != teamRaces.GetValueOrDefault(team)) return false;
        if (item.PrerequisiteItemIds.Any(prerequisite => NativeBuildingStatus(team, prerequisite, out _, out _) != 0)) return false;
        entityId = troop;
        cost = item.Cost;
        return true;
    }

    /// <summary>The price of an item (<c>0x438074</c>), or 0 for an unknown one.</summary>
    internal int NativeItemCost(int itemId) => dependencyCatalog?.TryGet(itemId, out var item) == true ? item.Cost : 0;

    /// <summary>The build variant of the live building in a city slot (player <c>+0xC5C + slot * 4</c>).</summary>
    internal int? CitySlotVariant(int team, int slot)
    {
        if (footprints is null || CityBuilding(team, slot) is not { } building || teamRaces.GetValueOrDefault(team) is not { } race) return null;
        for (var variant = 0; variant < 2; variant++)
            if (footprints.TryResolveBuildingEntity(race, variant, slot, out var entityId) &&
                entityId == (building.DeployedEntityId ?? building.Seed.EntityId)) return variant;
        return null;
    }
}
