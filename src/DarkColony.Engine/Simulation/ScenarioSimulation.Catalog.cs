using DarkColony.Engine.Commands;
using DarkColony.Engine.Data;

namespace DarkColony.Engine.Simulation;

/// <summary>The state <c>0x437BC4</c> gives a dependency record; the catalog shows only <see cref="Offered"/> items.</summary>
public enum CatalogItemState
{
    /// <summary>0: the building stands in that variant or a higher one, or the research level is reached.</summary>
    Done,
    /// <summary>1: the gadget is shown and can be counted.</summary>
    Offered,
    /// <summary>2: a prerequisite is not done, the item is disabled, or it belongs to no player city.</summary>
    Unavailable,
}

/// <summary>
/// The build and research catalogs: the counts on their <c>count</c> gadgets
/// (<c>0x433124</c>) and the BUILD button (<c>0x437F3C</c>). A count is paid
/// when it is added; BUILD sends the counted items as the native commands
/// 9 (building), 10 (troops) and 12 (research). See
/// <c>docs/GAMEPLAY_FIXES_PLAN.md</c>.
/// </summary>
public sealed partial class ScenarioSimulation
{
    /// <summary>A troop gadget counts up to 50 (<c>0x43317D</c>); buildings and research to 1.</summary>
    public const int MaximumTroopCount = 50;

    /// <summary>
    /// <c>0x437BC4</c>: a building is done while its city slot holds that
    /// variant or a higher one, and research while the player's level for
    /// its entity and category is at least the item's. Anything else is
    /// offered unless a prerequisite is not done or the item is disabled.
    /// P7 plays no part. A team without a city has nothing to offer.
    /// </summary>
    public CatalogItemState CatalogState(int team, int itemId) => CatalogState(team, itemId, []);

    private CatalogItemState CatalogState(int team, int itemId, HashSet<int> visiting)
    {
        if (dependencyCatalog?.TryGet(itemId, out var item) != true || !teamEconomies.TryGetValue(team, out var economy) ||
            (!UsesPortConstructionAdapters() && !HasCity(team)) || !OfferedToRace(team, item) || !visiting.Add(itemId))
            return CatalogItemState.Unavailable;
        try
        {
            if (item.IsBuilding && CityBuilding(team, item.BuildingSlot!.Value) is not null &&
                CitySlotVariant(team, item.BuildingSlot.Value) is { } variant && item.BuildingVariant <= variant)
                return CatalogItemState.Done;
            if (item.IsUpgrade && economy.ResearchLevel(dependencyCatalog, item.UpgradeEntityId!.Value, item.UpgradeCategory!.Value) >= item.UpgradeLevel)
                return CatalogItemState.Done;
            // 0x437DD8 / 0x437E19: a prerequisite building, or the item's own
            // slot, still rising after its delivery also makes it unavailable.
            if (item.PrerequisiteItemIds.Any(prerequisite => CatalogState(team, prerequisite, visiting) != CatalogItemState.Done ||
                    dependencyCatalog.TryGet(prerequisite, out var needed) && needed.IsBuilding && IsSlotRising(team, needed.BuildingSlot!.Value)) ||
                item.IsBuilding && IsSlotRising(team, item.BuildingSlot!.Value) ||
                economy.DisabledItems.Contains(itemId))
                return CatalogItemState.Unavailable;
            return CatalogItemState.Offered;
        }
        finally
        {
            visiting.Remove(itemId);
        }
    }

    /// <summary>The race's own gadgets: a building's faction, or a troop's or upgrade's entity faction.</summary>
    private bool OfferedToRace(int team, DependencyDefinition item)
    {
        var race = teamRaces.GetValueOrDefault(team);
        if (item.IsBuilding) return item.BuildingFaction == race;
        var entityId = item.IsTroop ? item.TroopEntityId!.Value : item.UpgradeEntityId!.Value;
        return (uint)entityId < (uint)entityDefinitions.Count && EntityDefinitionFor(entityId).Faction == race;
    }

    /// <summary>
    /// <c>0x433124</c>. Event 4 (left button) adds one to an offered item's
    /// count when the price fits P7, which pays it at once: a troop up to 50,
    /// a building or research only from 0. Event 5 (right button) takes one
    /// back and refunds its price.
    /// </summary>
    private void ApplyCatalogCount(CatalogCountIntent intent)
    {
        if (dependencyCatalog?.TryGet(intent.DependencyItemId, out var item) != true ||
            !teamEconomies.TryGetValue(intent.TeamId, out var economy)) return;
        var count = economy.CatalogCount(item.Id);
        if (intent.Remove)
        {
            if (count <= 0) return;
            economy.AddP7(item.Cost);
            economy.SetCatalogCount(item.Id, count - 1);
            return;
        }
        if (CatalogState(intent.TeamId, item.Id) != CatalogItemState.Offered) return;
        if (count >= (item.IsTroop ? MaximumTroopCount : 1)) return;
        if (!economy.TrySpend(item.Cost)) return;
        economy.SetCatalogCount(item.Id, count + 1);
    }

    /// <summary>
    /// <c>0x437F3C</c>: every offered item with a count, in record order,
    /// loses its count and is ordered. Its price was paid with the count.
    /// <list type="bullet">
    /// <item>Command 9 (<c>0x41C8D4</c>) rebuilds the building's city slot,
    /// or refunds when the slot already holds that variant at full health.</item>
    /// <item>Command 10 (<c>0x41C7F8</c>) appends the troops to their queue.</item>
    /// <item>Command 12 (<c>0x41CA04</c>) sets the research level at once,
    /// and refunds level × 1000 when it is already set.</item>
    /// </list>
    /// </summary>
    private void ApplyBuild(BuildIntent intent, TickEvents events)
    {
        if (dependencyCatalog is null || !teamEconomies.TryGetValue(intent.TeamId, out var economy)) return;
        // The native loop only sends commands, so every state is the one
        // before BUILD: collect first, then carry the orders out.
        var orders = dependencyCatalog.Items.Values
            .OrderBy(item => item.Id)
            .Select(item => (Item: item, Count: economy.CatalogCount(item.Id)))
            .Where(order => order.Count != 0 && CatalogState(intent.TeamId, order.Item.Id) == CatalogItemState.Offered)
            .ToArray();
        foreach (var (item, count) in orders)
        {
            economy.SetCatalogCount(item.Id, 0);
            if (item.IsBuilding)
            {
                var slot = item.BuildingSlot!.Value;
                if (CityBuilding(intent.TeamId, slot) is { } current && CitySlotVariant(intent.TeamId, slot) == item.BuildingVariant &&
                    current.Health >= current.MaximumHealth)
                {
                    economy.AddP7(item.Cost);
                    continue;
                }
                if (BuildCitySlot(intent.TeamId, item) is { } built) events.BuildingPlacements.Add(built);
            }
            else if (item.IsTroop)
            {
                var entityId = item.TroopEntityId!.Value;
                if (!productionQueues.TryGetValue((intent.TeamId, EntityDefinitionFor(entityId).ProductionQueue), out var queue))
                {
                    events.UnitProductions.Add(new UnitProducedEvent(intent.TeamId, item.Id, 0, entityId, 0, UnitProductionOutcome.NoProductionQueue));
                    continue;
                }
                for (var copy = 0; copy < count; copy++) queue.Items.Add((item.Id, entityId));
                events.UnitProductions.Add(new UnitProducedEvent(intent.TeamId, item.Id, 0, entityId, 0, UnitProductionOutcome.Queued));
            }
            else
            {
                if (economy.ApplyResearch(dependencyCatalog, item.Id))
                {
                    events.ResearchCompletions.Add(new ResearchCompletedEvent(intent.TeamId, item.Id, 0, ResearchOutcome.Completed));
                    continue;
                }
                economy.AddP7(item.UpgradeLevel!.Value * 1000);
                events.ResearchCompletions.Add(new ResearchCompletedEvent(intent.TeamId, item.Id, 0, ResearchOutcome.Refunded));
            }
        }
    }
}
