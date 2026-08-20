namespace DarkColony.Engine.Data;

/// <summary>
/// Data-derived command capabilities for one unit command identity. This is an
/// identity/presentation boundary: it states which recovered command slots may
/// be exposed, but does not grant an untraced command an executor.
/// </summary>
public sealed record UnitCommandProfile(
    bool CanStop,
    bool CanMove,
    bool CanUseWaypoints,
    bool CanAttack,
    UnitSpecialCommandDefinition? ContextualCommand,
    UnitSecondaryCommandDefinition? SecondaryCommand)
{
    public bool HasContextualCommand => ContextualCommand is not null;
    public bool HasSecondaryCommand => SecondaryCommand is not null;
}

/// <summary>Capabilities shared or available across one current unit selection.</summary>
public sealed record UnitSelectionCommandProfile(
    int Count,
    bool AnyCanStop,
    bool AnyCanMove,
    bool AnyCanUseWaypoints,
    bool AnyCanAttack,
    UnitSpecialCommandDefinition? CommonContextualCommand,
    UnitSecondaryCommandDefinition? CommonSecondaryCommand)
{
    /// <summary>
    /// Native gameplay selection storage spans 0x320 actor slots, matching the
    /// recovered maximum world-actor table.
    /// </summary>
    public const int MaximumActors = 0x320;
}

/// <summary>The three actor scan layers recovered from dc.exe's world grid.</summary>
public enum NativeActorSelectionLayer
{
    Ground = 0,
    Air = 1,
    Mine = 2,
}

/// <summary>
/// Native Ctrl/Alt filtering for a selection gesture. Alt skips the ground
/// scan and Ctrl skips the air scan; the dedicated mine scan is never skipped.
/// </summary>
public readonly record struct UnitSelectionLayerFilter(bool ExcludeGround, bool ExcludeAir)
{
    public static UnitSelectionLayerFilter FromModifiers(bool control, bool alt) =>
        new(ExcludeGround: alt, ExcludeAir: control);

    public bool Includes(EntityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return UnitCommandProfiles.NativeSelectionLayer(definition) switch
        {
            NativeActorSelectionLayer.Ground => !ExcludeGround,
            NativeActorSelectionLayer.Air => !ExcludeAir,
            NativeActorSelectionLayer.Mine => true,
            _ => false,
        };
    }
}

/// <summary>
/// Viewport-wide unit-type selections dispatched by native F1-F10 actions
/// 8-17. Every entry pairs equivalent Human/Gray identities; F1 spans all four
/// authored commander ranks for both factions.
/// </summary>
public static class NativeUnitSelectionHotkeys
{
    public static IReadOnlyList<int> EntityIds(int functionKey) => functionKey switch
    {
        1 => [69, 70, 71, 72, 73, 74, 75, 76],
        2 => [0, 8],
        3 => [2, 10],
        4 => [49, 50],
        5 => [5, 13],
        6 => [3, 11],
        7 => [43, 44],
        8 => [4, 12],
        9 => [6, 14],
        10 => [1, 9],
        _ => [],
    };
}

/// <summary>
/// Single mapping point for the shared `maine` unit-command slots. The source
/// entity definition supplies movement; resolved weapon availability is passed
/// in by simulation because upgrades and deployed forms determine whether the
/// normal attack command is actually usable.
/// </summary>
public static class UnitCommandProfiles
{
    /// <summary>
    /// Reproduces the executable's actor-grid priority: field-14 mines first,
    /// then movement-class-zero ground actors, then nonzero-class air actors.
    /// </summary>
    public static NativeActorSelectionLayer NativeSelectionLayer(EntityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.UsesNativeMineLayer) return NativeActorSelectionLayer.Mine;
        return definition.MovementClass == 0
            ? NativeActorSelectionLayer.Ground
            : NativeActorSelectionLayer.Air;
    }

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
            CanStop: canMove || hasResolvedWeapon,
            CanMove: canMove,
            CanUseWaypoints: canMove,
            CanAttack: hasResolvedWeapon,
            ContextualCommand: contextual,
            SecondaryCommand: secondary);
    }

    /// <summary>
    /// Returns the shared contextual action only when every selected profile
    /// owns the same recovered command. A mixed selection must not expose an
    /// action that would silently apply to only part of the selection.
    /// </summary>
    public static UnitSpecialCommandDefinition? CommonContextualCommand(IEnumerable<UnitCommandProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var selected = profiles.ToArray();
        if (selected.Length == 0 || selected.Any(profile => profile.ContextualCommand is null)) return null;
        var first = selected[0].ContextualCommand!;
        return selected.All(profile => profile.ContextualCommand == first) ? first : null;
    }

    /// <summary>Equivalent homogeneous-selection rule for the sixth command slot.</summary>
    public static UnitSecondaryCommandDefinition? CommonSecondaryCommand(IEnumerable<UnitCommandProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var selected = profiles.ToArray();
        if (selected.Length == 0 || selected.Any(profile => profile.SecondaryCommand is null)) return null;
        var first = selected[0].SecondaryCommand!;
        return selected.All(profile => profile.SecondaryCommand == first) ? first : null;
    }

    /// <summary>
    /// Aggregates one selection without losing the distinction between common
    /// contextual slots and ordinary commands that can apply to a capable
    /// subset, such as attack in a mixed armed/unarmed squad.
    /// </summary>
    public static UnitSelectionCommandProfile DescribeSelection(IEnumerable<UnitCommandProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var selected = profiles.ToArray();
        return new UnitSelectionCommandProfile(
            selected.Length,
            selected.Any(profile => profile.CanStop),
            selected.Any(profile => profile.CanMove),
            selected.Any(profile => profile.CanUseWaypoints),
            selected.Any(profile => profile.CanAttack),
            CommonContextualCommand(selected),
            CommonSecondaryCommand(selected));
    }

    /// <summary>
    /// Applies the recovered replace/Shift-toggle selection rule with stable
    /// instance ordering and the native actor-list capacity. This remains an
    /// input boundary; it creates no simulation command by itself.
    /// </summary>
    public static IReadOnlyList<int> ApplyActorSelection(
        IEnumerable<int> currentInstanceIds,
        IEnumerable<int> candidateInstanceIds,
        bool toggle)
    {
        ArgumentNullException.ThrowIfNull(currentInstanceIds);
        ArgumentNullException.ThrowIfNull(candidateInstanceIds);
        var result = toggle
            ? new SortedSet<int>(currentInstanceIds.Take(UnitSelectionCommandProfile.MaximumActors))
            : [];
        foreach (var instanceId in candidateInstanceIds.Distinct().Order())
        {
            if (toggle && result.Remove(instanceId)) continue;
            if (result.Count < UnitSelectionCommandProfile.MaximumActors) result.Add(instanceId);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Reproduces the post-scan compaction at 0x4371e0: when the resulting
    /// selection contains any actor owned by the local team, discard actors
    /// owned by other teams. A remote-only selection remains available for
    /// inspection, but cannot be mistaken for a local command selection.
    /// </summary>
    public static IReadOnlyList<int> PreferLocalOwnerSelection(
        IEnumerable<int> selectedInstanceIds,
        IReadOnlyDictionary<int, int> ownerByInstanceId,
        int localOwner)
    {
        ArgumentNullException.ThrowIfNull(selectedInstanceIds);
        ArgumentNullException.ThrowIfNull(ownerByInstanceId);
        var selected = selectedInstanceIds
            .Distinct()
            .Where(ownerByInstanceId.ContainsKey)
            .Order()
            .Take(UnitSelectionCommandProfile.MaximumActors)
            .ToArray();
        var local = selected.Where(instanceId => ownerByInstanceId[instanceId] == localOwner).ToArray();
        return local.Length == 0 ? selected : local;
    }
}
