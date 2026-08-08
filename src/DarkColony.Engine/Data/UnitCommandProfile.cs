namespace DarkColony.Engine.Data;

/// <summary>
/// Data-derived command capabilities for one unit command identity. This is an
/// identity/presentation boundary: it states which recovered command slots may
/// be exposed, but does not grant an untraced command an executor.
/// </summary>
public sealed record UnitCommandProfile(
    bool CanMove,
    bool CanUseWaypoints,
    bool CanAttack,
    UnitSpecialCommandDefinition? ContextualCommand,
    UnitSecondaryCommandDefinition? SecondaryCommand)
{
    public bool HasContextualCommand => ContextualCommand is not null;
    public bool HasSecondaryCommand => SecondaryCommand is not null;
}

/// <summary>
/// Single mapping point for the shared `maine` unit-command slots. The source
/// entity definition supplies movement; resolved weapon availability is passed
/// in by simulation because upgrades and deployed forms determine whether the
/// normal attack command is actually usable.
/// </summary>
public static class UnitCommandProfiles
{
    public static UnitCommandProfile Describe(EntityDefinition definition, bool hasResolvedWeapon)
        => Describe(definition, definition.MovementSpeed, hasResolvedWeapon);

    /// <summary>
    /// Describes an actor whose command identity is retained from its source
    /// entity while its active form may replace movement characteristics (for
    /// example a deployed mobile builder becoming a static tower).
    /// </summary>
    public static UnitCommandProfile Describe(EntityDefinition commandIdentity, int effectiveMovementSpeed, bool hasResolvedWeapon)
    {
        ArgumentNullException.ThrowIfNull(commandIdentity);
        var canMove = effectiveMovementSpeed > 0;
        UnitSpecialCommandCatalog.TryGet(commandIdentity, out var contextual);
        UnitSecondaryCommandCatalog.TryGet(commandIdentity, out var secondary);
        return new UnitCommandProfile(
            CanMove: canMove,
            CanUseWaypoints: canMove,
            CanAttack: hasResolvedWeapon,
            ContextualCommand: contextual,
            SecondaryCommand: secondary);
    }
}
