using DarkColony.Engine.World;

namespace DarkColony.Engine.Economy;

/// <summary>Decoded SCN vent plus its runtime harvester attachment.</summary>
public sealed class PetraVent
{
    public PetraVent(int id, CellCoordinate position, int initialState, int initialReservoir)
    {
        Id = id;
        Position = position;
        InitialState = initialState;
        InitialReservoir = Math.Max(0, initialReservoir);
        RemainingReservoir = InitialReservoir;
        Rate = initialState;
    }

    public int Id { get; }
    public CellCoordinate Position { get; }
    /// <summary>
    /// SCN fourth field. The SCN loader reads it from the placement's team
    /// column and stores it, times the session rate option (player 1 stat 0,
    /// 256 = x1) &gt;&gt; 8, as the vent's per-pulse amount (<c>+0x32</c>).
    /// </summary>
    public int InitialState { get; }
    /// <summary>
    /// Per-pulse amount (vent word <c>+0x32</c>): the SCN rate, later changed by
    /// a mission's <c>newrate</c>/<c>newrate2</c>. Zero pays nothing.
    /// </summary>
    public int Rate { get; internal set; }
    /// <summary>SCN fifth field, initialized into the native source actor's <c>+0x0c</c> reservoir.</summary>
    public int InitialReservoir { get; }
    /// <summary>Authoritative remaining source quantity; a native pulse requires a strictly positive remainder.</summary>
    public int RemainingReservoir { get; internal set; }
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
public sealed record PetraFlowRules(
    int TicksPerPulse,
    int PassiveP7PerPulse,
    int AttachedP7PerPulse,
    int OwnerP7Multiplier8_8 = 0x100,
    bool ApplyOwnerP7Multiplier = false,
    bool UseVentRates = false)
{
    public const int NativeHarvesterPulseTicks = 16;
    // dc.exe 0x4139d7 tests the low four bits of the authoritative world
    // counter, so a deployed harvester gets exactly one payout opportunity
    // every sixteen fixed steps. That path has no global per-team payout:
    // income is conditional on an attached source. The attached amount remains
    // provisional until source initialization is recovered.
    /// <summary>
    /// The native rules: 16-tick pulses paying each vent's own rate. The 4
    /// stays only as the synthetic amount for rules without vent rates.
    /// </summary>
    public static PetraFlowRules ProvisionalDefault => new(NativeHarvesterPulseTicks, 0, 4, UseVentRates: true);
    public bool IsValid => TicksPerPulse > 0 && PassiveP7PerPulse >= 0 &&
        AttachedP7PerPulse >= 0;

    /// <summary>
    /// Applies the native optional signed 8.8 player multiplier to the
    /// source's base pulse. The default 0x100 is the neutral multiplier used
    /// until the executable's player-field initialization is recovered.
    /// </summary>
    public int EffectiveAttachedP7
    {
        get
        {
            if (!IsValid) throw new InvalidOperationException("Invalid Petra flow rules.");
            return !ApplyOwnerP7Multiplier
                ? AttachedP7PerPulse
                : NativeSigned8_8Multiply(AttachedP7PerPulse, OwnerP7Multiplier8_8);
        }
    }

    /// <summary>
    /// Matches the executable's signed multiply followed by the corrective
    /// arithmetic shift used at <c>0x413a36</c>--<c>0x413a4a</c>.
    /// </summary>
    internal static int NativeSigned8_8Multiply(int value, int multiplier)
    {
        var product = checked((long)value * multiplier);
        if (product >= 0) return checked((int)(product >> 8));
        return checked((int)(-((-product + 0xff) >> 8)));
    }

    /// <summary>
    /// Mirrors the native producer's strict <c>remaining - rate &gt; 0</c>
    /// source check. An exact-rate remainder is not paid or consumed.
    /// </summary>
    public bool CanCreditReservoir(int remainingReservoir, int rate) =>
        rate > 0 && remainingReservoir > rate;
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
