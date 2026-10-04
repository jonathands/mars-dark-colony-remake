using DarkColony.Engine.Commands;
using DarkColony.Engine.Data;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// Network commands 9 (build, <c>0x41C8D4</c>) and 10 (troop order,
/// <c>0x41C7F8</c>) as computer players send them (<c>0x40C13C</c>,
/// <c>0x40C168</c>), and the dependency checks their planner uses. The
/// sender deducts the price itself when it sends; the command runs at the
/// next update's command phase.
/// </summary>
public sealed partial class ScenarioSimulation
{
    private readonly List<NativeOrder> pendingNativeOrders = [];

    private readonly record struct NativeOrder(int Team, int ItemId, int Count);

    /// <summary>Queues a command 9 or 10 for the next update.</summary>
    internal void QueueNativeOrder(int team, int itemId, int count = 1) => pendingNativeOrders.Add(new NativeOrder(team, itemId, count));

    /// <summary>Deducts a price the way <c>0x4566AC</c> and its siblings do: only when the player can pay it.</summary>
    internal bool TrySpendP7(int team, int amount) =>
        teamEconomies.TryGetValue(team, out var economy) && economy.TrySpend(amount);

    /// <summary>The player's P7 (<c>+0xBAC</c>).</summary>
    internal int P7(int team) => teamEconomies.TryGetValue(team, out var economy) ? economy.P7 : 0;

    /// <summary>Runs the commands queued during the previous update, in order.</summary>
    private void ApplyNativeOrders(TickEvents events)
    {
        if (pendingNativeOrders.Count == 0) return;
        var orders = pendingNativeOrders.ToArray();
        pendingNativeOrders.Clear();
        foreach (var order in orders)
        {
            if (dependencyCatalog?.TryGet(order.ItemId, out var item) != true) continue;
            if (item.IsTroop && item.TroopEntityId is { } entityId)
            {
                // Command 10: count copies join the troop's queue (entity value 21).
                if (!productionQueues.TryGetValue((order.Team, EntityDefinitionFor(entityId).ProductionQueue), out var queue)) continue;
                for (var copy = 0; copy < order.Count; copy++) queue.Items.Add((item.Id, entityId));
            }
            else if (item.IsBuilding)
            {
                // Command 9: a slot that already holds this variant at full
                // health refunds the price; otherwise the slot is rebuilt.
                var slot = item.BuildingSlot!.Value;
                if (CityBuilding(order.Team, slot) is { } current && CitySlotVariant(order.Team, slot) == item.BuildingVariant &&
                    current.Health >= current.MaximumHealth)
                {
                    if (teamEconomies.TryGetValue(order.Team, out var economy)) economy.AddP7(item.Cost);
                    continue;
                }
                if (BuildCitySlot(order.Team, item) is { } built) events.BuildingPlacements.Add(built);
            }
        }
    }

    /// <summary>
    /// <c>0x438220</c>: 0 when the player's city slot already holds a live
    /// building of at least the item's variant, 1 when the item can be
    /// built (slot and variant out), and 2 otherwise: an unknown, disabled,
    /// or non-building item, another race's item, or a prerequisite that is
    /// not itself at 0. A slot that a transport is using (<c>+0xC10</c>)
    /// also gives 2; the port does not model those transport slots.
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
