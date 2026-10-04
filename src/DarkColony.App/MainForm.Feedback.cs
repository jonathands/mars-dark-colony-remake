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

/// <summary>Presentation feedback (effects, sounds, status) derived from each tick's simulation events.</summary>
public sealed partial class MainForm
{
    private void CaptureDeathEffects()
    {
        if (_scenarioSimulation is null) return;
        var removedSelection = false;
        foreach (var destroyed in _scenarioSimulation.LastDestroyedActors)
        {
            _deathEffects.Add(new DeathEffect(destroyed.EntityId, destroyed.Position, _world.TickCount));
            PlayGameplaySound(destroyed.EntityId, "DEA");
            removedSelection |= _selectedEntityInstanceIds.Remove(destroyed.EntityInstanceId);
        }
        // GameplayEntities already excludes destroyed actors, but retaining
        // their IDs would leave an invisible selection and an armed target
        // cursor after a player squad is eliminated. Selection is UI state,
        // so clean it as part of consuming the authoritative death event.
        if (removedSelection && _selectedEntityInstanceIds.Count == 0)
        {
            _gameplayCommandMode = GameplayCommandMode.MoveOnly;
            _diagnosticMoveTarget = null;
            _diagnosticPathCells = [];
        }
        else if (removedSelection)
        {
            RevalidateGameplayCommandModeForSelection();
        }
    }

    private void CaptureCombatSounds()
    {
        if (_scenarioSimulation is null) return;
        foreach (var fired in _scenarioSimulation.LastWeaponFires)
        {
            var source = _scenarioSimulation.Actor(fired.SourceActorInstanceId);
            if (_weaponCatalog?.TryGet(fired.WeaponId, out var weapon) == true)
                PlayGameplaySound(weapon.SoundId, "GUN");
            else if (source is not null)
                PlayGameplaySound(source.Definition.Id, "GUN");
        }
        foreach (var impact in _scenarioSimulation.LastProjectileImpacts)
        {
            var source = _scenarioSimulation.Actor(impact.SourceActorInstanceId);
            if (_weaponCatalog?.TryGet(impact.WeaponId, out var weapon) == true)
                PlayGameplaySound(weapon.SoundId, "EXP");
            else if (source is not null)
                PlayGameplaySound(source.Definition.Id, "EXP");
        }
    }

    private void CaptureCombatAnimations()
    {
        if (_scenarioSimulation is null) return;
        foreach (var fired in _scenarioSimulation.LastWeaponFires)
        {
            _firingActorStartedAt[fired.SourceActorInstanceId] = _world.TickCount;
            _firingActorVariantRoll[fired.SourceActorInstanceId] = fired.PresentationVariantRoll;
        }
        foreach (var impact in _scenarioSimulation.LastProjectileImpacts)
        {
            _impactEffects.Add(new ImpactEffect(impact.WeaponId, impact.Position, _world.TickCount));
            if (impact.TargetActorInstanceId >= 0)
                _hitActorStartedAt[impact.TargetActorInstanceId] = _world.TickCount;
        }
    }

    private void CaptureBattlefieldTransportFeedback()
    {
        if (_scenarioSimulation is null) return;
        var selectionChanged = false;
        foreach (var transport in _scenarioSimulation.LastBattlefieldTransports)
        {
            if (transport.Kind == BattlefieldTransportEventKind.Started)
            {
                PlayGameplaySound(transport.TransportEntityId, "DPY");
                var inboundSource = _scenarioSimulation.Actor(transport.SourceActorInstanceId);
                if (inboundSource?.Seed.Team == _localPlayerTeam)
                    _status = transport.TransportEntityId == 92 ? "Dropship inbound." : "Saucer inbound.";
                continue;
            }
            if (transport.Kind != BattlefieldTransportEventKind.PayloadResolved) continue;
            foreach (var abducted in transport.AbductedInstanceIds)
                selectionChanged |= _selectedEntityInstanceIds.Remove(abducted);

            var source = _scenarioSimulation.Actor(transport.SourceActorInstanceId);
            if (source?.Seed.Team != _localPlayerTeam) continue;
            if (transport.ReinforcementInstanceIds.Count != 0)
                _status = $"Dropship delivered {transport.ReinforcementInstanceIds.Count} reinforcement(s).";
            else if (transport.AbductedInstanceIds.Count != 0)
                _status = $"Saucer abducted {transport.AbductedInstanceIds.Count} hostile unit(s).";
        }
        if (!selectionChanged) return;
        if (_selectedEntityInstanceIds.Count == 0) _gameplayCommandMode = GameplayCommandMode.MoveOnly;
        else RevalidateGameplayCommandModeForSelection();
    }

    private void CaptureAttackOrderFeedback()
    {
        if (_scenarioSimulation is null) return;
        foreach (var attack in _scenarioSimulation.LastAttackOrders.Where(attack => attack.Outcome != AttackOrderOutcome.Acquired))
            _status = $"Attack order #{attack.SourceActorInstanceId} → #{attack.TargetActorInstanceId} rejected: {attack.Outcome}.";
        foreach (var attackMove in _scenarioSimulation.LastAttackMoveOrders.Where(order => order.Outcome != AttackMoveOrderOutcome.Accepted))
            _status = $"Attack-move order #{attackMove.SourceActorInstanceId} → ({attackMove.Target.X},{attackMove.Target.Z}) rejected: {attackMove.Outcome}.";
        foreach (var acquisition in _scenarioSimulation.LastAttackMoveAcquisitions)
            _status = $"Attack-move unit #{acquisition.SourceActorInstanceId} acquired target #{acquisition.TargetActorInstanceId}.";
        foreach (var special in _scenarioSimulation.LastGroundSpecialAttacks.Where(order => order.Outcome != GroundSpecialAttackOutcome.Accepted))
            _status = $"Ground-special order #{special.SourceActorInstanceId} at ({special.Target.X},{special.Target.Z}) rejected: {special.Outcome}.";
    }

    private void CaptureHealingFeedback()
    {
        if (_scenarioSimulation is null) return;
        foreach (var group in _scenarioSimulation.LastHeals.GroupBy(heal => heal.SourceActorInstanceId))
        {
            var source = _scenarioSimulation.Actor(group.Key);
            if (source?.Seed.Team != _localPlayerTeam) continue;
            var healed = group.Where(heal => heal.Outcome == HealOutcome.Healed).ToArray();
            if (healed.Length != 0)
            {
                // Healer firing is supplied by the shared glot.fin family,
                // not the units' own FIN files. Reuse the normal recovered
                // directional fire presentation so the command's visual,
                // sound, and authoritative HP event begin together.
                _firingActorStartedAt[group.Key] = _world.TickCount;
                _firingActorVariantRoll[group.Key] = 0;
                PlayGameplaySound(source.Definition.Id, "DPY");
                var target = _scenarioSimulation.Actor(healed[0].TargetActorInstanceId);
                _status = $"{source.Definition.DisplayName} restored {healed[0].Amount} HP to {target?.Definition.DisplayName ?? $"unit #{healed[0].TargetActorInstanceId}"}; heal charge drained.";
            }
            else _status = $"Heal rejected: {group.Last().Outcome}.";
        }
    }

    private void CaptureInspireFeedback()
    {
        if (_scenarioSimulation is null) return;
        foreach (var group in _scenarioSimulation.LastInspires.GroupBy(effect => effect.SourceActorInstanceId))
        {
            var source = _scenarioSimulation.Actor(group.Key);
            if (source?.Seed.Team != _localPlayerTeam) continue;
            var applied = group.Where(effect => effect.Outcome == InspireOutcome.Applied).ToArray();
            if (applied.Length != 0)
                _status = $"{source.Definition.DisplayName} inspired {applied.Select(effect => effect.TargetActorInstanceId).Distinct().Count()} nearby combat unit(s).";
            else if (group.Any(effect => effect.Outcome == InspireOutcome.Preparing))
                _status = $"{source.Definition.DisplayName} is inspiring nearby troops.";
            else _status = $"Inspire completed: {group.Last().Outcome}.";
        }
    }

    private void CaptureConstructionFeedback()
    {
        if (_scenarioSimulation is null) return;
        foreach (var purchase in _scenarioSimulation.LastPurchaseReservations)
        {
            if (purchase.TeamId != _localPlayerTeam) continue;
            if (_dependencyCatalog?.TryGet(purchase.DependencyItemId, out var item) != true || !item.IsBuilding) continue;
            if (purchase.Eligibility != PurchaseEligibility.Available)
                _status = $"{BuildingLabel(item)} not reserved: {purchase.Eligibility}.";
        }
        foreach (var placement in _scenarioSimulation.LastBuildingPlacements)
        {
            if (placement.TeamId != _localPlayerTeam) continue;
            if (placement.Outcome == BuildingDropOutcome.Placed)
            {
                var name = _entityCatalog is not null && (uint)placement.EntityId < (uint)_entityCatalog.Entities.Count
                    ? _entityCatalog[placement.EntityId].DisplayName : $"entity {placement.EntityId}";
                _status = $"{name} dropped; tech-tree item {placement.DependencyItemId} is now complete.";
                RuntimeLog.Info($"Built {name} (item {placement.DependencyItemId}, entity {placement.EntityId}) at tick {_scenarioSimulation.TickCount}.");
            }
            else if (placement.TeamId == _localPlayerTeam &&
                     placement.Outcome is (BuildingDropOutcome.OutOfBounds or BuildingDropOutcome.Occupied or BuildingDropOutcome.InvalidFootprint) &&
                     _scenarioSimulation.EconomyForTeam(placement.TeamId)?.ReservedItems.Contains(placement.DependencyItemId) == true)
            {
                // Failed map validation does not consume a reserved tech-tree
                // purchase. Keep the pending item/live ghost so the player
                // can choose a new origin instead of silently stranding P7.
                _pendingBuildingItemId = placement.DependencyItemId;
                _gameplayCommandMode = GameplayCommandMode.PlaceBuilding;
                _status = $"Building drop blocked: {placement.Outcome}. Choose another location; P7 remains reserved.";
            }
            else _status = $"Building drop rejected: {placement.Outcome}.";
        }
        foreach (var message in _scenarioSimulation.LastMissionMessages)
        {
            // msg: line N of the scenario's .msg text.
            _status = _missionText?.Messages.TryGetValue(message.Index, out var line) == true ? line : $"Mission message {message.Index}.";
        }
        // The diagnostic ends the mission on this peer only, so not in a network game.
        if (ForcedOutcomeAfterSeconds is { } forcedAfter && !IsNetworkGame && _scenarioSimulation.Outcome is null &&
            Environment.TickCount64 - _missionStartedAtMilliseconds >= forcedAfter * 1000L)
            _scenarioSimulation.EndMission(ForcedVictory ? 0 : 1, ForcedVictory ? 1 : 2);
        if (_scenarioSimulation.Outcome is { } outcome && !ReferenceEquals(outcome, _bailOutcome))
        {
            // Each bail restarts the 10 s wall-clock countdown (0x43D973).
            _bailOutcome = outcome;
            _bailRequestedAtMilliseconds = Environment.TickCount64;
        }
        if (_bailOutcome is { } bail && !_missionOutcomeReported &&
            Environment.TickCount64 - _bailRequestedAtMilliseconds >= ScenarioSimulation.BailDelayMilliseconds)
        {
            _missionOutcomeReported = true;
            PlayMissionEndVideo(bail, () => ShowMissionDebrief(bail));
        }
        foreach (var production in _scenarioSimulation.LastUnitProductions)
        {
            if (production.TeamId != _localPlayerTeam) continue;
            if (production.Outcome == UnitProductionOutcome.Produced)
            {
                var produced = _scenarioSimulation.Actor(production.EntityInstanceId);
                var source = _scenarioSimulation.Actor(production.SourceBuildingInstanceId);
                var name = produced?.Definition.DisplayName ??
                    (_entityCatalog is not null && (uint)production.EntityId < (uint)_entityCatalog.Entities.Count
                        ? _entityCatalog[production.EntityId].DisplayName : $"entity {production.EntityId}");
                var sourceName = source?.Definition.DisplayName ?? $"structure #{production.SourceBuildingInstanceId}";
                var spawn = produced is null ? string.Empty : $" at {produced.Movement.OccupiedCell.X},{produced.Movement.OccupiedCell.Z}";
                _status = $"{name} #{production.EntityInstanceId} produced by {sourceName}{spawn}.";
                RuntimeLog.Info($"Trained {name} #{production.EntityInstanceId} (item {production.DependencyItemId}, entity {production.EntityId}){spawn} at tick {_scenarioSimulation.TickCount}.");
            }
            else if (production.Outcome == UnitProductionOutcome.Queued)
            {
                var name = _entityCatalog is not null && (uint)production.EntityId < (uint)_entityCatalog.Entities.Count
                    ? _entityCatalog[production.EntityId].DisplayName : $"entity {production.EntityId}";
                _status = $"{name} queued.";
            }
            else _status = $"Unit production rejected: {production.Outcome}.";
        }
        foreach (var research in _scenarioSimulation.LastResearchCompletions)
        {
            if (research.TeamId != _localPlayerTeam) continue;
            _status = research.Outcome == ResearchOutcome.Completed
                ? $"Research item {research.DependencyItemId} complete."
                : $"Research rejected: {research.Outcome}.";
            RuntimeLog.Info($"Research item {research.DependencyItemId}: {research.Outcome} at tick {_scenarioSimulation.TickCount}.");
        }
    }

    private void CaptureHarvesterFeedback()
    {
        if (_scenarioSimulation is null) return;
        foreach (var deployment in _scenarioSimulation.LastHarvesterDeployments)
        {
            var actor = _scenarioSimulation.Actor(deployment.EntityInstanceId);
            var name = actor?.Definition.DisplayName ??
                $"Harvester #{deployment.EntityInstanceId}";
            // `slist.dat` has a distinct DPY category for both resource
            // harvesters. Play it when the simulation confirms attachment,
            // not when the player merely requests a vent.
            if (deployment.Outcome == HarvesterDeploymentOutcome.Attached && actor is not null)
                PlayGameplaySound(actor.Definition.Id, "DPY");
            if (deployment.Outcome == HarvesterDeploymentOutcome.Retracted && actor is not null)
                _formRetractionStartedAt[deployment.EntityInstanceId] = _world.TickCount;
            _status = deployment.Outcome switch
            {
                HarvesterDeploymentOutcome.Attached => $"{name} attached to Petra-7 vent {deployment.VentId + 1}; P7 flow increased.",
                HarvesterDeploymentOutcome.Retracted => $"{name} retracted from Petra-7 vent {deployment.VentId + 1} and is mobile.",
                HarvesterDeploymentOutcome.Preparing => $"{name} is deploying on Petra-7 vent {deployment.VentId + 1}.",
                HarvesterDeploymentOutcome.EnRoute => $"{name} is moving to Petra-7 vent {deployment.VentId + 1}.",
                HarvesterDeploymentOutcome.VentUnavailable => $"Petra-7 vent {deployment.VentId + 1} already has a harvester.",
                HarvesterDeploymentOutcome.NoApproach => $"Petra-7 vent {deployment.VentId + 1} cannot be entered.",
                HarvesterDeploymentOutcome.UndeployLocked => $"{name} cannot leave Petra-7 vent {deployment.VentId + 1} in this mission.",
                _ => "Deploy rejected: select a live Exploiter or Gray Slug.",
            };
        }
        foreach (var deployment in _scenarioSimulation.LastMineDeployments)
        {
            if (deployment.Outcome == MineDeploymentOutcome.Preparing)
            {
                _status = $"Mine deployment preparing ({ScenarioSimulation.NativeImmediateSpecialTicks} ticks).";
            }
            else if (deployment.Outcome == MineDeploymentOutcome.Deployed)
            {
                var source = _scenarioSimulation.Actor(deployment.SourceActorInstanceId);
                if (source is not null)
                {
                    _formDeploymentStartedAt[deployment.EntityInstanceId] = _world.TickCount;
                    PlayGameplaySound(source.Definition.Id, "DPY");
                }
                _status = $"Mine unit #{deployment.EntityInstanceId} deployed in place at ({deployment.Target.X},{deployment.Target.Z}).";
            }
            else _status = $"Mine deployment rejected: {deployment.Outcome}.";
        }
        foreach (var deployment in _scenarioSimulation.LastTowerDeployments)
        {
            var source = _scenarioSimulation.Actor(deployment.EntityInstanceId);
            if (deployment.Outcome == TowerDeploymentOutcome.Deployed && source is not null)
            {
                _formDeploymentStartedAt[deployment.EntityInstanceId] = _world.TickCount;
                PlayGameplaySound(source.Definition.Id, "DPY");
                _status = $"{source.Definition.DisplayName} deployed as a static tower.";
            }
            else _status = $"Tower deployment rejected: {deployment.Outcome}.";
        }
        foreach (var deployment in _scenarioSimulation.LastStealDeployments)
        {
            var source = _scenarioSimulation.Actor(deployment.EntityInstanceId);
            if (source is null) continue;
            if (deployment.Outcome is StealDeploymentOutcome.Deployed or StealDeploymentOutcome.NoVictim or StealDeploymentOutcome.VictimTaken)
            {
                _formDeploymentStartedAt[deployment.EntityInstanceId] = _world.TickCount;
                PlayGameplaySound(source.Definition.Id, "DPY");
            }
            else if (deployment.Outcome == StealDeploymentOutcome.Retracted)
                _formRetractionStartedAt[deployment.EntityInstanceId] = _world.TickCount;
            if (source.Seed.Team != _localPlayerTeam) continue;
            _status = deployment.Outcome switch
            {
                StealDeploymentOutcome.Preparing => $"{source.Definition.DisplayName} changing stance ({ScenarioSimulation.NativeImmediateSpecialTicks} ticks).",
                StealDeploymentOutcome.Deployed => $"{source.Definition.DisplayName} is draining miner #{source.StealVictimInstanceId}.",
                StealDeploymentOutcome.NoVictim => $"{source.Definition.DisplayName} sees no deployed miner in reach and retracts.",
                StealDeploymentOutcome.VictimTaken => $"{source.Definition.DisplayName}: that miner is already drained; retracting.",
                StealDeploymentOutcome.VictimLost => $"{source.Definition.DisplayName} lost its miner and retracts.",
                StealDeploymentOutcome.Retracted => $"{source.Definition.DisplayName} retracted from its stealing stance and is mobile.",
                _ => $"Steal deployment rejected: {deployment.Outcome}.",
            };
        }
        foreach (var contact in _scenarioSimulation.LastContactResolutions)
        {
            var name = _scenarioSimulation.Actor(contact.ActorInstanceId)?.Definition.DisplayName ?? $"Unit #{contact.ActorInstanceId}";
            if (contact.Role == ContactRole.Rescue && _localPlayerTeam == 0)
                _status = $"{name} rescued; it joins your forces.";
            else if (contact.Role == ContactRole.Pickup && contact.Team == _localPlayerTeam)
                _status = $"Picked up {name}: +{contact.Amount} P7.";
        }
        foreach (var theft in _scenarioSimulation.LastP7Thefts)
        {
            if (theft.ThiefTeamId == _localPlayerTeam)
                _status = $"Stole {theft.Amount} P7 from enemy miner #{theft.VictimHarvesterInstanceId}.";
            else if (theft.VictimTeamId == _localPlayerTeam)
                _status = $"Enemy unit #{theft.ThiefInstanceId} intercepted {theft.Amount} P7.";
        }
    }

    private void PlayGameplaySound(int ownerId, string category)
    {
        if (_installation is null) return;
        try
        {
            _soundCatalog ??= SoundCatalog.Load(_installation.DataFile("sound"));
            var soundId = _soundCatalog.For(ownerId, category).FirstOrDefault(-1);
            if (soundId < 0 || !_soundCatalog.Sounds.TryGetValue(soundId, out var sound)) return;
            var path = Path.Combine(_installation.RootPath, sound.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) new SoundPlayer(path).Play();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException)
        {
            _status = $"Sound error: {error.Message}";
        }
    }

    private void CaptureGameplayStatus()
    {
        if (string.IsNullOrWhiteSpace(_status) || string.Equals(_status, _lastRecordedGameplayStatus, StringComparison.Ordinal)) return;
        _gameplayMessageHistory.Add(_status);
        // Native message state initializes indexes 0..15 at 0x44da88.
        if (_gameplayMessageHistory.Count > 16) _gameplayMessageHistory.RemoveAt(0);
        _lastRecordedGameplayStatus = _status;
        _gameplayMessageIndex = _gameplayMessageHistory.Count - 1;
    }

    private string GameplayMessageText()
    {
        CaptureGameplayStatus();
        return _gameplayMessageIndex is >= 0 && _gameplayMessageIndex < _gameplayMessageHistory.Count
            ? _gameplayMessageHistory[_gameplayMessageIndex]
            : _status;
    }

    private void BrowseGameplayMessages(int direction)
    {
        CaptureGameplayStatus();
        if (_gameplayMessageHistory.Count == 0) return;
        _gameplayMessageIndex = Math.Clamp(_gameplayMessageIndex + direction, 0, _gameplayMessageHistory.Count - 1);
        Invalidate();
    }
}
