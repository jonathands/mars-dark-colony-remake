using DarkColony.Engine.Data;

namespace DarkColony.Engine.Economy;

public enum PurchaseEligibility
{
    Available,
    CatalogUnavailable,
    UnknownItem,
    AlreadyCompleted,
    AlreadyReserved,
    MissingPrerequisite,
    InsufficientP7,
    Disabled,
}

/// <summary>
/// Per-team single-resource account plus completed dependency items. A reserved
/// order is intentionally not a completed building: pedestal delivery owns the
/// later completion transition.
/// </summary>
public sealed class TeamEconomy
{
    private readonly HashSet<int> completedItems = [];
    private readonly List<int> reservedItems = [];
    private readonly HashSet<int> disabledItems = [];

    public TeamEconomy(int p7) => P7 = Math.Max(0, p7);

    public int P7 { get; private set; }
    public IReadOnlySet<int> CompletedItems => completedItems;
    public IReadOnlyList<int> ReservedItems => reservedItems;

    public PurchaseEligibility Evaluate(DependencyCatalog? catalog, int itemId)
    {
        if (catalog is null) return PurchaseEligibility.CatalogUnavailable;
        if (!catalog.TryGet(itemId, out var item)) return PurchaseEligibility.UnknownItem;
        if (disabledItems.Contains(itemId)) return PurchaseEligibility.Disabled;
        // Buildings and upgrades transition into the completed dependency set.
        // Troops deliberately stay repeatable production requests.
        if (item.IsBuilding || item.IsUpgrade)
        {
            if (completedItems.Contains(itemId)) return PurchaseEligibility.AlreadyCompleted;
            if (reservedItems.Contains(itemId)) return PurchaseEligibility.AlreadyReserved;
        }
        if (item.PrerequisiteItemIds.Any(prerequisite => !completedItems.Contains(prerequisite)))
            return PurchaseEligibility.MissingPrerequisite;
        return P7 < item.Cost ? PurchaseEligibility.InsufficientP7 : PurchaseEligibility.Available;
    }

    public PurchaseEligibility TryReserve(DependencyCatalog? catalog, int itemId)
    {
        var eligibility = Evaluate(catalog, itemId);
        if (eligibility != PurchaseEligibility.Available) return eligibility;
        P7 -= catalog!.Items[itemId].Cost;
        reservedItems.Add(itemId);
        return PurchaseEligibility.Available;
    }

    /// <summary>Simulation-owned P7 income; presentation only reads the resulting balance.</summary>
    public void AddP7(int amount)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        P7 = checked(P7 + amount);
    }

    /// <summary>Called only by construction/delivery once a prerequisite is live in the world.</summary>
    public bool MarkCompleted(DependencyCatalog? catalog, int itemId)
    {
        if (catalog?.TryGet(itemId, out var item) != true || !item.IsBuilding) return false;
        if (!reservedItems.Remove(itemId)) return false;
        return completedItems.Add(itemId);
    }

    /// <summary>
    /// Seeds an already-live scenario building into the prerequisite set.
    /// Scenario placement is a different lifecycle from a player reservation:
    /// the building was authored as complete, so no P7 debit or reservation is
    /// involved.
    /// </summary>
    public bool SeedCompletedBuilding(DependencyCatalog? catalog, int itemId)
    {
        if (catalog?.TryGet(itemId, out var item) != true || !item.IsBuilding) return false;
        return completedItems.Add(itemId);
    }

    /// <summary>
    /// <c>dfiddle</c> (player <c>+0x193C + item</c>): a disabled item fails the
    /// dependency checks <c>0x43839C</c>/<c>0x438220</c>.
    /// </summary>
    public void SetItemDisabled(int itemId, bool disabled)
    {
        if (disabled) disabledItems.Add(itemId);
        else disabledItems.Remove(itemId);
    }

    public IReadOnlySet<int> DisabledItems => disabledItems;

    /// <summary>Withdraws a building whose city slot no longer satisfies it.</summary>
    public bool WithdrawCompletedBuilding(DependencyCatalog? catalog, int itemId)
    {
        if (catalog?.TryGet(itemId, out var item) != true || !item.IsBuilding) return false;
        return completedItems.Remove(itemId);
    }

    /// <summary>Consumes a paid non-building production reservation at spawn time.</summary>
    public bool ConsumeReservation(DependencyCatalog? catalog, int itemId)
    {
        if (catalog?.TryGet(itemId, out var item) != true || item.IsBuilding) return false;
        return reservedItems.Remove(itemId);
    }

    /// <summary>Completes a paid research item; stat effects remain a separate combat rule.</summary>
    public bool CompleteResearch(DependencyCatalog? catalog, int itemId)
    {
        if (catalog?.TryGet(itemId, out var item) != true || !item.IsUpgrade) return false;
        if (!reservedItems.Remove(itemId)) return false;
        return completedItems.Add(itemId);
    }

    /// <summary>
    /// Returns the highest completed level for one decoded upgrade target and
    /// category. Weapon upgrades select the corresponding levelled weapon
    /// slot from <c>gamestat.txt</c>; armor's runtime multiplier remains a
    /// separate reconstruction target.
    /// </summary>
    public int CompletedUpgradeLevel(DependencyCatalog? catalog, int targetEntityId, int category)
    {
        if (catalog is null) return 0;
        return completedItems
            .Select(itemId => catalog.TryGet(itemId, out var item) ? item : null)
            .Where(item => item is { IsStatUpgrade: true } && item.UpgradeEntityId == targetEntityId && item.UpgradeCategory == category)
            .Select(item => item!.UpgradeLevel ?? 0)
            .DefaultIfEmpty(0)
            .Max();
    }
}
