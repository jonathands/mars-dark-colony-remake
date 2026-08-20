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

/// <summary>Recovered interaction class for a unit-command slot.</summary>
public enum UnitCommandActivation
{
    Pending,
    Immediate,
    MapTarget,
}

public sealed record UnitSpecialCommandDefinition(
    UnitSpecialCommand Command,
    string Label,
    int InterfaceFrame,
    UnitCommandActivation Activation,
    string PendingReason);

public static class UnitSpecialCommandCatalog
{
    private static readonly IReadOnlyDictionary<string, UnitSpecialCommandDefinition> byEntityCode =
        new Dictionary<string, UnitSpecialCommandDefinition>(StringComparer.Ordinal)
        {
            ["EXPL"] = new(UnitSpecialCommand.HarvestPetra, "DEPLOY", 74, UnitCommandActivation.MapTarget, ""),
            ["SLUG"] = new(UnitSpecialCommand.HarvestPetra, "DEPLOY", 74, UnitCommandActivation.MapTarget, ""),
            ["ENGI"] = new(UnitSpecialCommand.DeployMine, "DEPLOY MINE", 69, UnitCommandActivation.Immediate, ""),
            ["SLOM"] = new(UnitSpecialCommand.DeployMine, "DEPLOY MINE", 69, UnitCommandActivation.Immediate, ""),
            // `maine` supplies the command name/frame. gamestat identifies
            // BEON/ZISP as paired healing units; sound2.dat and mbullet.txt
            // independently contain HEAL.WAV and healing-ray resistance.
            ["BEON"] = new(UnitSpecialCommand.HealUnits, "HEAL", 122, UnitCommandActivation.Immediate, "First damaged same-team actor in the native radius-7 scan; requires charge 4 and drains to zero."),
            ["ZISP"] = new(UnitSpecialCommand.HealUnits, "HEAL", 122, UnitCommandActivation.Immediate, "First damaged same-team actor in the native radius-7 scan; requires charge 4 and drains to zero."),
            ["TURR"] = new(UnitSpecialCommand.DeployTurret, "DEPLOY TURRET", 68, UnitCommandActivation.Immediate, ""),
            ["XENO"] = new(UnitSpecialCommand.DeployTurret, "DEPLOY TURRET", 68, UnitCommandActivation.Immediate, ""),
            // SARG/PSYC own DEPLOY FIN transitions into SARGSTL/PSYCSTL.
            // The static stealing forms are the result, not the command owner.
            ["SARG"] = new(UnitSpecialCommand.StealMoney, "STEAL MONEY", 75, UnitCommandActivation.Immediate, "The 50% transfer is live; exact native range and competing-thief arbitration remain unresolved."),
            ["PSYC"] = new(UnitSpecialCommand.StealMoney, "STEAL MONEY", 75, UnitCommandActivation.Immediate, "The 50% transfer is live; exact native range and competing-thief arbitration remain unresolved."),
        };

    public static bool TryGet(string entityCode, out UnitSpecialCommandDefinition definition) =>
        byEntityCode.TryGetValue(entityCode, out definition!);

    public static bool TryGet(EntityDefinition entity, out UnitSpecialCommandDefinition definition)
    {
        // The Human/Gray commander ranks reuse TRSC/GRAY art codes, so their
        // entity range—not the shared code—is the only safe identity boundary.
        if (entity.Id is >= 69 and <= 76)
        {
            definition = new UnitSpecialCommandDefinition(UnitSpecialCommand.InspireTroops, "INSPIRE", 121, UnitCommandActivation.Immediate,
                "Recovered state-13 cast: after 50 ticks, nearby same-team combat units receive temporary exact-center weapon aim.");
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
    UnitCommandActivation Activation,
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
            // dc.exe's opcode-0x1b/state-18 path reads gamestat value 29 and
            // temporarily installs it as the firing weapon. SARG/PSYC resolve
            // exactly to weapons 50/51; their sound owners map to
            // CY2NDFI.WAV/PSY2NDFI.WAV through slist.dat.
            ["SARG"] = new("NAPALM ATTACK", 72, UnitCommandActivation.MapTarget, "Recovered ground-target state 18; research item 80 enables weapon 50.", RequiredResearchItemId: 80, CandidateEffectWeaponId: 50),
            ["PSYC"] = new("DISEASE ATTACK", 73, UnitCommandActivation.MapTarget, "Recovered ground-target state 18; research item 54 enables weapon 51.", RequiredResearchItemId: 54, CandidateEffectWeaponId: 51),
        };

    private static readonly UnitSecondaryCommandDefinition groundAttack =
        new("GROUND ATTACK", 2, UnitCommandActivation.MapTarget,
            "Native capability 3 selects maine control 146; that control uses generic mainbut frame 2.", CandidateEffectWeaponId: 0);

    private static readonly IReadOnlyDictionary<int, UnitSecondaryCommandDefinition> commanderCommands =
        new Dictionary<int, UnitSecondaryCommandDefinition>
        {
            [70] = new("DROP SHIP", 125, UnitCommandActivation.MapTarget, "Native projectile mode 5 delivers two Security Troops.", CandidateEffectWeaponId: 57),
            [71] = new("DROP SHIP", 125, UnitCommandActivation.MapTarget, "Native projectile mode 6 adds one Reaper to the troop packet.", CandidateEffectWeaponId: 58),
            [72] = new("DROP SHIP", 125, UnitCommandActivation.MapTarget, "Native projectile mode 7 adds one Thunderbolt to the packet.", CandidateEffectWeaponId: 59),
            [74] = new("SAUCER", 126, UnitCommandActivation.MapTarget, "Native projectile mode 8 abducts eligible hostiles within four cells.", CandidateEffectWeaponId: 60),
            [75] = new("SAUCER", 126, UnitCommandActivation.MapTarget, "Native projectile mode 9 abducts eligible hostiles within six cells.", CandidateEffectWeaponId: 63),
            [76] = new("SAUCER", 126, UnitCommandActivation.MapTarget, "Native projectile mode 10 abducts eligible hostiles within eight cells.", CandidateEffectWeaponId: 64),
        };

    public static bool TryGet(string entityCode, out UnitSecondaryCommandDefinition definition) =>
        byEntityCode.TryGetValue(entityCode, out definition!);

    public static bool TryGet(EntityDefinition entity, out UnitSecondaryCommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (byEntityCode.TryGetValue(entity.Code, out definition!)) return true;
        if (commanderCommands.TryGetValue(entity.Id, out definition!)) return true;
        if ((entity.GroundSpecialCapability & 0x3f) == 3 && entity.GroundSpecialWeaponId == 0)
        {
            definition = groundAttack;
            return true;
        }
        definition = null!;
        return false;
    }
}
