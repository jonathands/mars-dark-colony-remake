namespace DarkColony.Engine.Data;

/// <summary>
/// Identity mapping for the contextual fifth command in <c>intrface/maine</c>
/// group 40.  It deliberately records UI evidence independently from whether
/// a simulation implementation exists.
/// </summary>
public enum UnitSpecialCommand
{
    None,
    HarvestPetra,
    DeployMine,
    HealUnits,
    DeployTurret,
    InspireTroops,
    StealMoney,
}

public sealed record UnitSpecialCommandDefinition(
    UnitSpecialCommand Command,
    string Label,
    int InterfaceFrame,
    string PendingReason);

public static class UnitSpecialCommandCatalog
{
    private static readonly IReadOnlyDictionary<string, UnitSpecialCommandDefinition> byEntityCode =
        new Dictionary<string, UnitSpecialCommandDefinition>(StringComparer.Ordinal)
        {
            ["EXPL"] = new(UnitSpecialCommand.HarvestPetra, "DEPLOY", 74, ""),
            ["SLUG"] = new(UnitSpecialCommand.HarvestPetra, "DEPLOY", 74, ""),
            ["ENGI"] = new(UnitSpecialCommand.DeployMine, "DEPLOY MINE", 69, ""),
            ["SLOM"] = new(UnitSpecialCommand.DeployMine, "DEPLOY MINE", 69, ""),
            // `maine` supplies the command name/frame. gamestat identifies
            // BEON/ZISP as paired healing units; sound2.dat and mbullet.txt
            // independently contain HEAL.WAV and healing-ray resistance.
            ["BEON"] = new(UnitSpecialCommand.HealUnits, "HEAL", 122, "Healing amount is decoded; current range uses sight and cadence remains untraced."),
            ["ZISP"] = new(UnitSpecialCommand.HealUnits, "HEAL", 122, "Healing amount is decoded; current range uses sight and cadence remains untraced."),
            ["TURR"] = new(UnitSpecialCommand.DeployTurret, "DEPLOY TURRET", 68, "Tower deployment state is not decoded yet."),
            ["XENO"] = new(UnitSpecialCommand.DeployTurret, "DEPLOY TURRET", 68, "Tower deployment state is not decoded yet."),
            // SARG/PSYC own DEPLOY FIN transitions into SARGSTL/PSYCSTL.
            // The static stealing forms are the result, not the command owner.
            ["SARG"] = new(UnitSpecialCommand.StealMoney, "STEAL MONEY", 75, "Static stealing stance is decoded; victim targeting and transfer timing are not."),
            ["PSYC"] = new(UnitSpecialCommand.StealMoney, "STEAL MONEY", 75, "Static stealing stance is decoded; victim targeting and transfer timing are not."),
        };

    public static bool TryGet(string entityCode, out UnitSpecialCommandDefinition definition) =>
        byEntityCode.TryGetValue(entityCode, out definition!);

    public static bool TryGet(EntityDefinition entity, out UnitSpecialCommandDefinition definition)
    {
        // The Human/Gray commander ranks reuse TRSC/GRAY art codes, so their
        // entity range—not the shared code—is the only safe identity boundary.
        if (entity.Id is >= 69 and <= 76)
        {
            definition = new UnitSpecialCommandDefinition(UnitSpecialCommand.InspireTroops, "INSPIRE", 121,
                "Inspire target, bonus, and duration are not decoded yet.");
            return true;
        }
        return TryGet(entity.Code, out definition);
    }
}

/// <summary>
/// Identity mapping for the sixth command slot in <c>intrface/maine</c> group
/// 40. This is a contextual special-action slot, separate from the shared
/// fifth-slot command. Its activation and effect rules remain decoded only
/// where explicitly recorded by the catalog.
/// </summary>
public sealed record UnitSecondaryCommandDefinition(
    string Label,
    int InterfaceFrame,
    string PendingReason,
    int? RequiredResearchItemId = null,
    int? CandidateEffectWeaponId = null);

public static class UnitSecondaryCommandCatalog
{
    private static readonly IReadOnlyDictionary<string, UnitSecondaryCommandDefinition> byEntityCode =
        new Dictionary<string, UnitSecondaryCommandDefinition>(StringComparer.Ordinal)
        {
            // `maine` supplies the contextual labels, while bdf.txt names
            // main-button 72 as the Cyborg cruise-missile action and 73 as
            // the Psy-raider virus action. This supersedes the older, weaker
            // inference from the adjacent generic "Second Attack" comment.
            // Weapon 50 is authored as "Napalm effect" and uses CY2NDFI.WAV;
            // 51 is its Psy-raider-paired effect and uses PSY2NDFI.WAV. Neither
            // ID is a normal SARG/PSYC weapon slot, but the executor linkage is
            // still not statically traced, hence the deliberately named candidate.
            ["SARG"] = new("NAPALM ATTACK", 72, "Cyborg cruise-missile targeting, effect, and cooldown rules are not decoded yet.", RequiredResearchItemId: 80, CandidateEffectWeaponId: 50),
            ["PSYC"] = new("DISEASE ATTACK", 73, "Psy-raider virus targeting, effect, and cooldown rules are not decoded yet.", RequiredResearchItemId: 54, CandidateEffectWeaponId: 51),
            // maine's remaining sixth-slot labels map directly to the two
            // shipped dropship entity codes. Their packet/deployment rules
            // require a command-runtime trace, so retain the UI evidence
            // without enabling a guessed transport action.
            ["DROP"] = new("DROP SHIP", 125, "Human dropship packet and deployment behavior are not decoded yet."),
            ["SAUC"] = new("SAUCER", 126, "Gray saucer packet and deployment behavior are not decoded yet."),
        };

    public static bool TryGet(string entityCode, out UnitSecondaryCommandDefinition definition) =>
        byEntityCode.TryGetValue(entityCode, out definition!);

    public static bool TryGet(EntityDefinition entity, out UnitSecondaryCommandDefinition definition) =>
        TryGet(entity.Code, out definition);
}
