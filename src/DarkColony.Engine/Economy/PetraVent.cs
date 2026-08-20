using DarkColony.Engine.World;

namespace DarkColony.Engine.Economy;

/// <summary>Decoded SCN vent plus its runtime harvester attachment.</summary>
public sealed class PetraVent
{
    public PetraVent(int id, CellCoordinate position, int scenarioValue, int cycleMilliseconds)
    {
        Id = id;
        Position = position;
        ScenarioValue = scenarioValue;
        CycleMilliseconds = cycleMilliseconds;
    }

    public int Id { get; }
    public CellCoordinate Position { get; }
    /// <summary>SCN fourth field; its native yield/capacity meaning remains unresolved.</summary>
    public int ScenarioValue { get; }
    /// <summary>SCN fifth field; established as the independent vent-cycle interval.</summary>
    public int CycleMilliseconds { get; }
    public int? HarvesterInstanceId { get; internal set; }
    /// <summary>
    /// Harvester currently completing the executable-recovered vent handshake.
    /// Native <c>dc.exe</c> keeps this delay on the vent actor before changing
    /// EXPL/SLUG into EDPLY/SDPL.
    /// </summary>
    public int? PendingHarvesterInstanceId { get; internal set; }
    public int AttachTicksRemaining { get; internal set; }
}

/// <summary>
/// Explicit, replaceable economy timing while native P7 gain constants are
/// still under disassembly. Values are intentionally not encoded in UI code.
/// </summary>
public sealed record PetraFlowRules(int TicksPerPulse, int PassiveP7PerPulse, int AttachedP7PerPulse)
{
    // User-observed behavior: a slow team trickle becomes materially faster
    // when an Exploiter/Slug is deployed on a vent. Exact native values open.
    public static PetraFlowRules ProvisionalDefault => new(15, 1, 4);
    public bool IsValid => TicksPerPulse > 0 && PassiveP7PerPulse >= 0 && AttachedP7PerPulse >= 0;
}

/// <summary>
/// Evidence-backed theft share with an explicit provisional proximity rule.
/// Campaign text establishes 50%, long range, and no sight requirement; the
/// executable's exact distance metric and competing-thief arbitration remain open.
/// </summary>
public sealed record PetraStealRules(int MaximumCellDistance, int Numerator, int Denominator)
{
    public static PetraStealRules ProvisionalDefault => new(12, 1, 2);
    public bool IsValid => MaximumCellDistance >= 0 && Numerator >= 0 && Denominator > 0 && Numerator <= Denominator;

    public int StolenAmount(int attachedIncome) => attachedIncome * Numerator / Denominator;
}
