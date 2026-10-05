using DarkColony.App.Diagnostics;
using DarkColony.App.Ui;
using DarkColony.App.Rendering;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.Time;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;
using DarkColony.Engine.Commands;
using DarkColony.Engine.Movement;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Media;

namespace DarkColony.App;

/// <summary>Selection queries and the translation of player orders into engine intents.</summary>
public sealed partial class MainForm
{
    private bool IsVisibleToLocalTeam(WorldEntity entity)
    {
        if (entity.Team == _localPlayerTeam) return true;
        var actor = _scenarioSimulation?.Actor(entity.InstanceId);
        return actor is not null && _scenarioSimulation!.IsActorVisibleToTeam(_localPlayerTeam, actor);
    }

    /// <summary>
    /// A map order at a point, like <c>0x4094F4</c>: a visible hostile under it
    /// is attacked (<c>0x409358</c>), anywhere else the units move there or
    /// attack-move (<c>0x4092AC</c>); a waiting map command takes the point.
    /// </summary>
    private void QueueOrderAt(Point point)
    {
        if (_gameplayMap is null || _gameplayPath is null || _scenarioSimulation is null ||
            _entityCatalog is null || _groundOccupancy is null || _alternateOccupancy is null || point.X >= GameplayWorldArea.Width || point.Y >= GameplayWorldArea.Height) return;
        if (_gameplayCommandMode is GameplayCommandMode.AttackTarget or GameplayCommandMode.MoveOnly)
        {
            var targetActor = FindGameplayActorAt(point, locallyControllableOnly: false);
            if (targetActor is not null && _scenarioSimulation.TeamRelations.IsHostile(_localPlayerTeam, targetActor.Team))
            {
                QueueAttackTarget(targetActor);
                return;
            }
        }
        var target = CellAtPixel(point.X + _cameraX, point.Y + _cameraY);
        if (target.X < 0 || target.Z < 0 || target.X >= _gameplayMap.Width || target.Z >= _gameplayMap.Height) return;
        if (_gameplayCommandMode == GameplayCommandMode.PlaceBuilding)
        {
            if (_pendingBuildingItemId is not { } itemId)
            {
                _status = "Choose a building in the Build catalog first.";
                _gameplayCommandMode = GameplayCommandMode.MoveOnly;
                return;
            }
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new PlaceBuildingIntent(_localPlayerTeam, itemId, target));
            _status = $"Building drop requested at ({target.X},{target.Z}).";
            _pendingBuildingItemId = null;
            _gameplayCommandMode = GameplayCommandMode.MoveOnly;
            return;
        }
        if (_selectedEntityInstanceIds.Count == 0) return;
        if (_gameplayCommandMode == GameplayCommandMode.GroundSpecialTarget)
        {
            var attackers = SelectedGameplayActors()
                .Where(entity => SelectedUnitSecondary([entity]) is { } command && SecondaryCommandAvailable(command))
                .OrderBy(entity => entity.InstanceId)
                .ToArray();
            if (attackers.Length == 0)
            {
                _status = "The selected unit has no researched ground-special attack.";
                _gameplayCommandMode = GameplayCommandMode.MoveOnly;
                return;
            }
            foreach (var attacker in attackers)
                _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1,
                    new GroundSpecialAttackIntent(attacker.InstanceId, target));
            _status = $"{SelectedUnitSecondary(attackers)!.Value.Label} ordered at ({target.X},{target.Z}).";
            PlayGameplaySound(attackers[0].EntityId, "ACK");
            _gameplayCommandMode = GameplayCommandMode.MoveOnly;
            return;
        }
        if (_gameplayCommandMode == GameplayCommandMode.HarvestVent)
        {
            var vent = _scenarioSimulation.PetraVents.FirstOrDefault(candidate =>
                Math.Abs(candidate.Position.X - target.X) <= 1 && Math.Abs(candidate.Position.Z - target.Z) <= 1);
            var harvesters = SelectedGameplayActors().Where(IsHarvester).ToArray();
            if (vent is null)
            {
                _status = "Deploy mode: right-click a visible Petra-7 vent.";
                return;
            }
            if (harvesters.Length == 0)
            {
                _status = "Deploy mode requires a selected Exploiter or Gray Slug.";
                return;
            }
            foreach (var harvester in harvesters)
                _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new HarvestVentIntent(harvester.InstanceId, vent.Id));
            _status = $"Deploy ordered for {harvesters.Length} harvester(s) at vent {vent.Id + 1}.";
            PlayGameplaySound(harvesters[0].EntityId, "ACK");
            return;
        }
        QueueMoveOrders(target);
    }

    /// <summary>
    /// Orders the selected mobile units to a cell: an attack move in Move &amp;
    /// Attack mode, a waypoint with Shift or in waypoint mode, otherwise a move.
    /// </summary>
    private void QueueMoveOrders(CellCoordinate target)
    {
        if (_gameplayPath is null || _scenarioSimulation is null || _entityCatalog is null ||
            _groundOccupancy is null || _alternateOccupancy is null) return;
        var selected = GameplayEntities()
            .Where(entity => _selectedEntityInstanceIds.Contains(entity.InstanceId) && IsLocallyControllable(entity))
            .Where(entity => _scenarioSimulation.Actor(entity.InstanceId) is { } actor &&
                _scenarioSimulation.EffectiveDefinition(actor).MovementSpeed > 0)
            .OrderBy(entity => entity.InstanceId)
            .ToArray();
        if (selected.Length == 0) { _status = "Select at least one mobile local team-0 unit."; return; }
        if (_gameplayCommandMode == GameplayCommandMode.AttackTarget || _attackMoveMode && _gameplayCommandMode == GameplayCommandMode.MoveOnly)
        {
            var attackers = selected.Where(HasWeapon).ToArray();
            if (attackers.Length == 0)
            {
                _status = "Attack-move requires a selected unit with a resolved weapon.";
                return;
            }
            var attackDestinations = FormationDestinations(target, attackers.Length).ToArray();
            for (var index = 0; index < attackers.Length; index++)
                _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1,
                    new AttackMoveIntent(attackers[index].InstanceId, attackDestinations[index]));
            _status = $"Attack-move ordered for {attackers.Length} unit(s) toward ({target.X},{target.Z}); hostile scan follows day/night sight.";
            PlayGameplaySound(attackers[0].EntityId, "ACK");
            return;
        }
        var appendWaypoint = ModifierKeys.HasFlag(Keys.Shift) || _gameplayCommandMode == GameplayCommandMode.Waypoints;
        if (appendWaypoint && selected.All(entity =>
                (_scenarioSimulation.Actor(entity.InstanceId)?.MoveOrder?.PendingWaypointCount ?? ActiveMoveOrder.MaximumWaypoints) >= ActiveMoveOrder.MaximumWaypoints))
        {
            _status = "Waypoint list is full (8 destinations). Use Stop or issue a new Move order.";
            return;
        }
        var destinations = FormationDestinations(target, selected.Length).ToArray();
        for (var index = 0; index < selected.Length; index++)
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1,
                new MoveIntent(selected[index].InstanceId, destinations[index], appendWaypoint));
        _diagnosticMoveTarget = target;
        var instanceId = selected[0].InstanceId;
        var leader = selected[0];
        var source = _scenarioSimulation.Actor(instanceId)?.Movement.OccupiedCell ?? leader.SpawnCell;
        var sourceRegion = _gameplayPath.RegionAt(source);
        var targetRegion = _gameplayPath.RegionAt(target);
        var coarse = _gameplayPath.BuildCoarseRoute(sourceRegion, targetRegion);
        var local = new DiagnosticLocalPathfinder(_gameplayPath, _groundOccupancy, _alternateOccupancy).Find(
            source, target, _entityCatalog[leader.EntityId].MovementClass, instanceId);
        _diagnosticPathCells = local.Cells;
        _status = $"{(appendWaypoint ? "Queued" : "Move")} {selected.Length} unit(s): lead ({source.X},{source.Z}) → ({target.X},{target.Z}); local {local.Termination}, {local.Steps.Count} steps.";
        PlayGameplaySound(leader.EntityId, "ACK");
    }

    private void QueueAttackTarget(WorldEntity target)
    {
        if (_scenarioSimulation is null || _entityCatalog is null) return;
        if (!_scenarioSimulation.TeamRelations.IsHostile(_localPlayerTeam, target.Team))
        {
            _status = "Attack mode: that team is cooperative, not hostile.";
            return;
        }
        // Explicit attack targets are valid for armed deployed towers too;
        // movement-only orders continue to use SelectedGameplayActors.
        var attackers = SelectedGameplayEntities().Where(HasWeapon).ToArray();
        if (attackers.Length == 0)
        {
            _status = "No selected unit has a resolved weapon.";
            return;
        }
        foreach (var attacker in attackers)
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new AttackIntent(attacker.InstanceId, target.InstanceId));
        var targetName = (uint)target.EntityId < (uint)_entityCatalog.Entities.Count ? _entityCatalog[target.EntityId].DisplayName : $"#{target.EntityId}";
        _status = $"Attack target acquired: {attackers.Length} unit(s) → {targetName} (team {target.Team}).";
        PlayGameplaySound(attackers[0].EntityId, "ACK");
    }

    private void QueueAreaHeal(IReadOnlyList<WorldEntity> healers)
    {
        if (healers.Count == 0)
        {
            _status = "Select a Human Medi-craft or Gray Zisp before healing.";
            return;
        }
        foreach (var healer in healers)
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new HealAreaIntent(healer.InstanceId));
        _status = $"Area heal ordered for {healers.Count} healer(s).";
        PlayGameplaySound(healers[0].EntityId, "ACK");
    }

    private void StopSelectedUnits()
    {
        if (_scenarioSimulation is null || _selectedEntityInstanceIds.Count == 0) return;
        var stopped = GameplayEntities()
            .Where(entity => _selectedEntityInstanceIds.Contains(entity.InstanceId) && IsLocallyControllable(entity))
            // Stop cancels explicit attack targets as well as movement. This
            // includes deployed static towers, which cannot move but do own a
            // real combat target in the simulation.
            .Where(entity => _scenarioSimulation.Actor(entity.InstanceId) is { } actor &&
                (_scenarioSimulation.EffectiveDefinition(actor).MovementSpeed > 0 || IsDeployedStaticCombatUnit(entity)))
            .OrderBy(entity => entity.InstanceId)
            .ToArray();
        foreach (var entity in stopped)
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new StopIntent(entity.InstanceId));
        // Stop is also the player-visible cancellation path for a retained
        // map-targeting mode. Without this, a subsequent right-click could
        // unexpectedly issue another attack/heal/deploy order after units had
        // visibly been told to stop.
        _gameplayCommandMode = GameplayCommandMode.MoveOnly;
        _diagnosticMoveTarget = null;
        _diagnosticPathCells = [];
        _status = stopped.Length == 0 ? "No local units selected." : $"Stop ordered for {stopped.Length} unit(s).";
    }

    private IEnumerable<CellCoordinate> FormationDestinations(CellCoordinate center, int count)
    {
        yield return center;
        for (var radius = 1; count > 1; radius++)
        for (var z = -radius; z <= radius; z++)
        for (var x = -radius; x <= radius; x++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius) continue;
            var candidate = new CellCoordinate(center.X + x, center.Z + z);
            if (candidate.X < 0 || candidate.Z < 0 || candidate.X >= _gameplayMap!.Width || candidate.Z >= _gameplayMap.Height) continue;
            yield return candidate;
            if (--count == 1) yield break;
        }
    }

    private IEnumerable<WorldEntity> GameplayEntities()
    {
        if (_scenarioSimulation is null) yield break;
        foreach (var actor in _scenarioSimulation.Actors.Where(actor => !actor.IsDestroyed)) yield return actor.Seed;
    }

    private IEnumerable<WorldEntity> SelectedGameplayActors() => GameplayEntities()
        .Where(entity => _selectedEntityInstanceIds.Contains(entity.InstanceId))
        .Where(IsLocallyControllable)
        .Where(IsActiveMobileUnit)
        .OrderBy(entity => entity.InstanceId);

    private IEnumerable<WorldEntity> SelectedInspectableGameplayEntities() => GameplayEntities()
        .Where(entity => _selectedEntityInstanceIds.Contains(entity.InstanceId))
        .OrderBy(entity => entity.InstanceId);

    private IEnumerable<WorldEntity> SelectedGameplayEntities() => GameplayEntities()
        .Where(entity => _selectedEntityInstanceIds.Contains(entity.InstanceId))
        .Where(IsLocallyControllable)
        .OrderBy(entity => entity.InstanceId);

    private EntityDefinition? GameplayDefinition(WorldEntity entity) =>
        _scenarioSimulation?.Actor(entity.InstanceId) is { } actor
            ? _scenarioSimulation.EffectiveDefinition(actor)
            : _entityCatalog is not null && (uint)entity.EntityId < (uint)_entityCatalog.Entities.Count
                ? _entityCatalog[entity.EntityId]
                : null;

    private UnitCommandProfile? GameplayCommandProfile(WorldEntity entity) =>
        _scenarioSimulation?.Actor(entity.InstanceId) is { } actor
            ? UnitCommandProfiles.Describe(
                actor.Definition,
                _scenarioSimulation.EffectiveDefinition(actor).MovementSpeed,
                _scenarioSimulation.EffectiveWeaponFor(actor) is not null)
            : null;

    private bool HasWeapon(WorldEntity entity) => GameplayCommandProfile(entity)?.CanAttack == true;

    private bool IsActiveMobileUnit(WorldEntity entity) => GameplayCommandProfile(entity)?.CanMove == true;

    private bool IsDeployedStaticCombatUnit(WorldEntity entity) => _scenarioSimulation?.Actor(entity.InstanceId) is { } actor &&
        actor.DeployedEntityId is not null && _scenarioSimulation.EffectiveDefinition(actor).MovementSpeed <= 0 &&
        _scenarioSimulation.EffectiveDefinition(actor).Code != "HMINE" && HasWeapon(entity);

    private bool HasContextualCommand(WorldEntity entity, UnitSpecialCommand command) =>
        GameplayCommandProfile(entity)?.ContextualCommand?.Command == command;

    private bool IsHarvester(WorldEntity entity) => HasContextualCommand(entity, UnitSpecialCommand.HarvestPetra);

    private bool IsMineLayer(WorldEntity entity) => HasContextualCommand(entity, UnitSpecialCommand.DeployMine);

    private bool IsHealer(WorldEntity entity) => HasContextualCommand(entity, UnitSpecialCommand.HealUnits);

    private bool IsDeployedStealStance(WorldEntity entity) =>
        _scenarioSimulation?.Actor(entity.InstanceId) is { DeployedEntityId: not null } actor &&
        _scenarioSimulation.EffectiveDefinition(actor).Code is "SARGSTL" or "PSYCSTL";

    private bool IsDeployedHarvester(WorldEntity entity) =>
        _scenarioSimulation?.Actor(entity.InstanceId) is { DeployedEntityId: not null } actor &&
        _scenarioSimulation.EffectiveDefinition(actor).Code is "EDPLY" or "SDPL";

    private bool IsDeployedMine(WorldEntity entity) =>
        _scenarioSimulation?.Actor(entity.InstanceId) is { DeployedEntityId: not null } actor &&
        _scenarioSimulation.EffectiveDefinition(actor).Code == "HMINE";

    private readonly record struct UnitSpecial(
        string Label,
        int Frame,
        bool EngineBacked,
        string? PendingReason = null,
        GameplayCommandMode? SelectedMode = null);

    private readonly record struct UnitSecondary(string Label, int Frame, UnitCommandActivation Activation, string PendingReason, int? RequiredResearchItemId, int? WeaponId);

    private UnitSpecial SelectedUnitSpecial(IReadOnlyList<WorldEntity> selected)
    {
        // This uses engine-owned recovery data so menu rendering cannot drift
        // from command identities as further abilities are implemented.
        var definition = SelectedCommonContextualCommand(selected);
        if (definition is null)
            return new UnitSpecial("DEPLOY", 74, false, "This unit has no recovered shared-slot action.");

        var towerAlreadyDeployed = definition.Command == UnitSpecialCommand.DeployTurret && selected.Any(entity =>
            _scenarioSimulation?.Actor(entity.InstanceId)?.DeployedEntityId is not null);
        var engineBacked = definition.Activation != UnitCommandActivation.Pending && !towerAlreadyDeployed;
        GameplayCommandMode? selectedMode = definition.Activation == UnitCommandActivation.MapTarget &&
            definition.Command == UnitSpecialCommand.HarvestPetra ? GameplayCommandMode.HarvestVent : null;
        return new UnitSpecial(definition.Label, definition.InterfaceFrame, engineBacked, definition.PendingReason, selectedMode);
    }

    private UnitSecondary? SelectedUnitSecondary(IReadOnlyList<WorldEntity> selected)
    {
        var definition = SelectedCommonSecondaryCommand(selected);
        if (definition is null) return null;
        return new UnitSecondary(definition.Label, definition.InterfaceFrame, definition.Activation, definition.PendingReason,
            definition.RequiredResearchItemId, definition.CandidateEffectWeaponId);
    }

    private UnitSpecialCommandDefinition? SelectedCommonContextualCommand(IReadOnlyList<WorldEntity> selected)
    {
        var profiles = selected.Select(GameplayCommandProfile).ToArray();
        return profiles.Length == selected.Count && profiles.All(profile => profile is not null)
            ? UnitCommandProfiles.CommonContextualCommand(profiles.Select(profile => profile!))
            : null;
    }

    private UnitSecondaryCommandDefinition? SelectedCommonSecondaryCommand(IReadOnlyList<WorldEntity> selected)
    {
        var profiles = selected.Select(GameplayCommandProfile).ToArray();
        return profiles.Length == selected.Count && profiles.All(profile => profile is not null)
            ? UnitCommandProfiles.CommonSecondaryCommand(profiles.Select(profile => profile!))
            : null;
    }

    private UnitSelectionCommandProfile GameplayCommandState(IEnumerable<WorldEntity> selected)
    {
        var profiles = selected
            .Select(GameplayCommandProfile)
            .Where(profile => profile is not null)
            .Select(profile => profile!)
            .ToArray();
        return UnitCommandProfiles.DescribeSelection(profiles);
    }

    private UnitSelectionCommandProfile SelectedGameplayCommandState() =>
        GameplayCommandState(SelectedGameplayEntities());

    /// <summary>
    /// A targeting cursor belongs to the selection that activated it. When
    /// selection changes, keep the mode only if the new actors expose the same
    /// authoritative capability. Paid building placement is intentionally
    /// exempt because its reservation must remain completable.
    /// </summary>
    private void RevalidateGameplayCommandModeForSelection()
    {
        if (_gameplayCommandMode is GameplayCommandMode.MoveOnly or GameplayCommandMode.PlaceBuilding) return;
        var selection = SelectedGameplayCommandState();
        var supported = _gameplayCommandMode switch
        {
            GameplayCommandMode.AttackTarget => SelectedGameplayEntities().Any(HasWeapon),
            GameplayCommandMode.GroundSpecialTarget => SelectedUnitSecondary(SelectedGameplayEntities().ToArray()) is { } secondary && SecondaryCommandAvailable(secondary),
            GameplayCommandMode.Waypoints => selection.AnyCanUseWaypoints,
            GameplayCommandMode.HarvestVent => selection.CommonContextualCommand is { Command: UnitSpecialCommand.HarvestPetra, Activation: UnitCommandActivation.MapTarget },
            _ => true,
        };
        if (supported) return;
        var cancelled = GameplayCommandModeLabel();
        _gameplayCommandMode = GameplayCommandMode.MoveOnly;
        _diagnosticMoveTarget = null;
        _diagnosticPathCells = [];
        _status = $"{_status} {cancelled} cancelled for the new selection.".Trim();
    }

    private bool SecondaryCommandAvailable(UnitSecondary command) =>
        command.Activation == UnitCommandActivation.MapTarget && command.WeaponId is not null &&
        (command.RequiredResearchItemId is not { } itemId ||
         _scenarioSimulation?.EconomyForTeam(_localPlayerTeam)?.CompletedItems.Contains(itemId) == true);

    private bool IsLocallyControllable(WorldEntity entity)
    {
        if (entity.Team != _localPlayerTeam) return false;
        // A free-War roster identifies the local player twice: first by the
        // selected SCN team, then by its selected faction. Requiring both for
        // player input keeps an unconverted/shared commander placeholder or
        // malformed mixed roster from becoming controllable.
        var war = _selectedScenario?.WarLaunch;
        return war is null ||
            _entityCatalog is not null &&
            (uint)entity.EntityId < (uint)_entityCatalog.Entities.Count &&
            _entityCatalog[entity.EntityId].Faction == war.Race;
    }

    private void ExecuteNativeImmediateSpecial()
    {
        var selected = SelectedGameplayEntities().ToArray();
        if (selected.Length == 0)
        {
            _status = "Select at least one local unit before pressing Enter.";
            return;
        }
        // Enter is the keyboard route to the same contextual slot. It must
        // preserve that slot's whole-selection ownership rule rather than
        // filtering a mixed selection down to whichever actor happens to pass
        // the native immediate-special gate.
        var contextual = SelectedCommonContextualCommand(selected);
        if (contextual is null)
        {
            _status = "The selected units do not share one native immediate-special action.";
            return;
        }
        var eligible = selected.Where(entity =>
            _scenarioSimulation?.Actor(entity.InstanceId) is { } actor &&
            _scenarioSimulation.EffectiveDefinition(actor).ImmediateSpecialCode != 0).ToArray();
        if (eligible.Length != selected.Length)
        {
            _status = $"{contextual.Label} is unavailable for one or more selected units.";
            return;
        }
        if (contextual?.Command == UnitSpecialCommand.DeployTurret)
        {
            QueueTowerDeployment(eligible);
            return;
        }
        if (contextual?.Command == UnitSpecialCommand.StealMoney)
        {
            QueueStealToggle(eligible);
            return;
        }
        if (contextual?.Command == UnitSpecialCommand.DeployMine)
        {
            QueueMineDeployment(eligible);
            return;
        }
        if (contextual?.Command == UnitSpecialCommand.InspireTroops)
        {
            QueueInspire(eligible);
            return;
        }
        if (contextual?.Command == UnitSpecialCommand.HarvestPetra && eligible.All(IsDeployedHarvester))
        {
            QueueHarvesterRetraction(eligible);
            return;
        }
        _status = "The selected unit has no executable native immediate-special adapter yet.";
    }

    private void QueueTowerDeployment(IReadOnlyList<WorldEntity> builders)
    {
        if (builders.Any(builder => _scenarioSimulation?.Actor(builder.InstanceId)?.DeployedEntityId is not null))
        {
            _status = "That tower builder is already deployed.";
            return;
        }
        foreach (var builder in builders)
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new DeployTowerIntent(builder.InstanceId));
        _status = $"Tower deployment ordered for {builders.Count} builder(s).";
        PlayGameplaySound(builders[0].EntityId, "ACK");
    }

    private void QueueStealToggle(IReadOnlyList<WorldEntity> thieves)
    {
        var deployed = thieves.Where(IsDeployedStealStance).ToArray();
        if (deployed.Length != 0 && deployed.Length != thieves.Count)
        {
            _status = "Mixed mobile/deployed stealing selections cannot share one transition.";
            return;
        }
        if (deployed.Length != 0)
        {
            foreach (var thief in deployed)
                _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new RetractStealIntent(thief.InstanceId));
            _status = $"Stealing-stance retraction ordered for {deployed.Length} unit(s).";
            PlayGameplaySound(deployed[0].EntityId, "ACK");
            return;
        }
        foreach (var thief in thieves)
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new DeployStealIntent(thief.InstanceId));
        _status = $"Stealing stance ordered for {thieves.Count} unit(s); nearby hostile miners will lose half their vent income.";
        PlayGameplaySound(thieves[0].EntityId, "ACK");
    }

    private void QueueHarvesterRetraction(IReadOnlyList<WorldEntity> harvesters)
    {
        foreach (var harvester in harvesters)
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new RetractHarvesterIntent(harvester.InstanceId));
        _status = $"Harvester retraction ordered for {harvesters.Count} unit(s).";
        PlayGameplaySound(harvesters[0].EntityId, "ACK");
    }

    private void QueueMineDeployment(IReadOnlyList<WorldEntity> layers)
    {
        foreach (var layer in layers)
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new DeployMineIntent(layer.InstanceId));
        _status = $"In-place mine deployment ordered for {layers.Count} unit(s).";
        PlayGameplaySound(layers[0].EntityId, "ACK");
    }

    private void QueueInspire(IReadOnlyList<WorldEntity> commanders)
    {
        foreach (var commander in commanders)
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new InspireTroopsIntent(commander.InstanceId));
        _status = $"Inspire ordered for {commanders.Count} commander(s); effect follows the native 50-tick cast.";
        PlayGameplaySound(commanders[0].EntityId, "ACK");
    }
}
