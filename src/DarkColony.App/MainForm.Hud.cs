using DarkColony.App.Diagnostics;
using DarkColony.App.Ui;
using DarkColony.Presentation;
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
using System.Globalization;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Media;

namespace DarkColony.App;

/// <summary>The gameplay HUD: unit, structure, build, research, options, and allies panels.</summary>
public sealed partial class MainForm
{
    // The world view: 0x41ec1e gives the gameplay view object the rectangle
    // (4,6) 512x448, growing with a larger view. The HUD picture's black is
    // see-through only here; the panels' black (the empty command slots, for
    // example) stays black.
    private Rectangle GameplayViewport => _gameplayScreen.Viewport;

    private void DrawGameplayHud(Graphics graphics, Image hud)
    {
        if (_activeCanvas is { } canvas)
        {
            var size = _gameplayScreen.Size;
            var view = GameplayViewport;
            canvas.Fill(new Rectangle(0, 0, size.Width, view.Top), Color.Black);
            canvas.Fill(new Rectangle(0, view.Bottom, size.Width, size.Height - view.Bottom), Color.Black);
            canvas.Fill(new Rectangle(0, view.Top, view.Left, view.Height), Color.Black);
            canvas.Fill(new Rectangle(view.Right, view.Top, size.Width - view.Right, view.Height), Color.Black);
            canvas.Draw(GameplayHudImage(hud), new Rectangle(Point.Empty, size));
            return;
        }
        using var attributes = new ImageAttributes();
        attributes.SetColorKey(Color.Black, Color.Black);
        graphics.DrawImage(
            hud,
            new Rectangle(0, 0, 640, 480),
            0,
            0,
            hud.Width,
            hud.Height,
            GraphicsUnit.Pixel,
            attributes);
    }

    // UI 79's texts for this frame; DrawGameplayPanelIdentityStrip picks one.
    private string? _panelIdentityText;
    private string? _hoveredHudText;

    /// <summary>
    /// The HUD picture for the current screen: <c>intrface.gif</c> itself at
    /// 640x480, or composed from it for a larger view, each pixel taken from
    /// the column and row <see cref="GameplayScreen"/> maps it to.
    /// </summary>
    private GpuImage GameplayHudImage(Image hud)
    {
        var classic = GpuColorKeyImage("gameplay-hud", hud);
        var screen = _gameplayScreen;
        if (screen.IsClassic) return classic;
        var key = $"gameplay-hud-{screen.Size.Width}x{screen.Size.Height}";
        if (_gpuColorKeyImages.TryGetValue(key, out var cached)) return cached;
        var (width, height) = (screen.Size.Width, screen.Size.Height);
        var columns = Enumerable.Range(0, width).Select(screen.SourceColumn).ToArray();
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = screen.SourceRow(y) * classic.Width;
            for (var x = 0; x < width; x++)
                Buffer.BlockCopy(classic.Rgba, (sourceRow + columns[x]) * 4, rgba, (y * width + x) * 4, 4);
        }
        var image = new GpuImage(width, height, rgba);
        _gpuColorKeyImages[key] = image;
        return image;
    }

    private void DrawGameplayUnitHud(Graphics graphics)
    {
        _panelIdentityText = null;
        _hoveredHudText = null;
        DrawGameplayTabs(graphics);
        if (_scenarioSimulation is not null)
        {
            CaptureGameplayStatus();
            DrawGameplayPetraCounter(graphics, _scenarioSimulation.ResourceForTeam(_localPlayerTeam));
            // The port funnels authoritative command and simulation outcomes
            // through `_status`. UI 148 is the authored 61-character message
            // line, so keep the adapter within that exact control rather than
            // dividing it into invented P7 and status regions.
            DrawGameplayIconButton(graphics, _gameplayHudLayout.LastMessage.Bounds, _gameplayHudLayout.LastMessage.Frame, false, _gameplayMessageIndex > 0);
            DrawGameplayIconButton(graphics, _gameplayHudLayout.NextMessage.Bounds, _gameplayHudLayout.NextMessage.Frame, false,
                _gameplayMessageIndex >= 0 && _gameplayMessageIndex < _gameplayMessageHistory.Count - 1);
            NoteHoveredHudControl(_gameplayHudLayout.LastMessage, _gameplayHudLayout.NextMessage, _gameplayHudLayout.BuildButton);
            var message = GameplayMessageText();
            DrawGameplayHudText(graphics, FitGameplayReadout(message, _gameplayHudLayout.MessageStatus.CharacterCapacity),
                new Rectangle(_gameplayHudLayout.MessageStatus.Origin, new Size(427, 14)));
            // maine in_text 234: the original HUD's days counter at 613,433.
            // 0x43abb0 formats it with "%3.3d" ("000"); remap 0 draws it red.
            DrawMenuText(graphics, _scenarioSimulation.DayNight.CompletedDays.ToString("000", CultureInfo.InvariantCulture),
                _gameplayScreen.Anchor(new Rectangle(604, 427, 30, 14)), remap: Color.FromArgb(255, 31, 31));
            DrawGameplayClock(graphics, _scenarioSimulation.DayNight);
        }
        var inspected = SelectedInspectableGameplayEntities().ToArray();
        var selected = inspected.Where(IsLocallyControllable).ToArray();
        if (inspected.Length != 0 && selected.Length == 0 && _gameplayHudTab == GameplayHudTab.Build)
        {
            DrawObservedEntityHud(graphics, inspected);
            return;
        }
        var deployedHarvesters = selected.Where(IsDeployedHarvester).ToArray();
        if (deployedHarvesters.Length == selected.Length && selected.Length != 0 && _gameplayHudTab == GameplayHudTab.Build)
        {
            DrawSelectedHarvesterHud(graphics, deployedHarvesters);
            return;
        }
        var staticStealSelected = selected.Where(IsDeployedStealStance).ToArray();
        if (staticStealSelected.Length == selected.Length && selected.Length != 0 && _gameplayHudTab == GameplayHudTab.Build)
        {
            DrawSelectedStealStanceHud(graphics, staticStealSelected);
            return;
        }
        var deployedMines = selected.Where(IsDeployedMine).ToArray();
        if (deployedMines.Length == selected.Length && selected.Length != 0 && _gameplayHudTab == GameplayHudTab.Build)
        {
            DrawSelectedMineHud(graphics, deployedMines);
            return;
        }
        var staticCombatSelected = selected.Where(IsDeployedStaticCombatUnit).ToArray();
        if (staticCombatSelected.Length == selected.Length && selected.Length != 0 && _gameplayHudTab == GameplayHudTab.Build)
        {
            DrawSelectedStaticCombatHud(graphics, staticCombatSelected);
            return;
        }
        var mobileSelected = selected.Where(IsActiveMobileUnit).ToArray();
        if (mobileSelected.Length != 0 && _gameplayHudTab == GameplayHudTab.Build)
        {
            // Movement may apply to a capable subset, but contextual slots are
            // homogeneous-selection commands. Pass the complete local
            // selection so rendering cannot expose an action by silently
            // discarding selected static or differently typed actors.
            DrawSelectedUnitCommands(graphics, selected);
            return;
        }
        if (selected.Length != 0 && _gameplayHudTab == GameplayHudTab.Build)
        {
            DrawSelectedStructureHud(graphics, selected);
            return;
        }

        switch (_gameplayHudTab)
        {
            case GameplayHudTab.Build:
                DrawBuildCatalog(graphics);
                break;
            case GameplayHudTab.Research:
                DrawResearchCatalog(graphics);
                break;
            case GameplayHudTab.Options:
                DrawOptionsCatalog(graphics);
                break;
        }

        if (selected.Length == 0)
        {
            if (_showAssetNames)
            {
                DrawGameplayLowerReadout(graphics, _gameplayHudTab == GameplayHudTab.Build ? "BUILD CATALOG" : _gameplayHudTab.ToString().ToUpperInvariant());
                DrawGameplayLowerReadout(graphics, _gameplayHudTab == GameplayHudTab.Build
                    ? "Choose a structure, then right-click its drop location"
                    : _showAlliesPanel
                        ? "Toggle a team: alliance changes direct attacks and attack-move acquisition."
                        : "UI mapped; engine behavior pending", bottom: true);
            }
            return;
        }

        var lead = selected[0];
        var definition = GameplayDefinition(lead);
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        var name = selected.Length == 1 && definition is not null
            ? definition.DisplayName
            : $"{selected.Length} Units";
        SetGameplayPanelIdentity(name);
        DrawGameplaySelectionDiagnostics(graphics, actor, definition);
    }

    private void DrawGameplayTabs(Graphics graphics)
    {
        // `maine` draws the three tabs as one 110x12 picture: frame 77, 78
        // or 79 shows the build, research or options tab pressed.
        var active = _gameplayHudTab switch
        {
            GameplayHudTab.Research => _gameplayHudLayout.ResearchTab,
            GameplayHudTab.Options => _gameplayHudLayout.OptionsTab,
            _ => _gameplayHudLayout.BuildTab,
        };
        DrawGameplayHudPicture(graphics, _gameplayHudLayout.TabStrip.Location, active.Frame);
        NoteHoveredHudControl(_gameplayHudLayout.BuildTab, _gameplayHudLayout.ResearchTab, _gameplayHudLayout.OptionsTab);
    }

    private void DrawSelectedUnitCommands(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        // Group 40 has common actions plus one shared, contextual ability
        // slot. Keep its label/frame tied to the original `maine` controls.
        var commandState = GameplayCommandState(selected);
        DrawGameplayCommandButton(graphics, _gameplayHudLayout.Stop, commandState.AnyCanStop);
        // checkb 33 and 35 show which of the pair is checked (+0x46A7).
        DrawGameplayCommandButton(graphics, _gameplayHudLayout.MoveOnly, commandState.AnyCanMove, isChecked: !_attackMoveMode);
        DrawGameplayCommandButton(graphics, _gameplayHudLayout.MoveAndAttack, commandState.AnyCanAttack, isChecked: _attackMoveMode);
        DrawGameplayCommandButton(graphics, _gameplayHudLayout.Waypoints, commandState.AnyCanUseWaypoints, GameplayCommandMode.Waypoints);
        var special = SelectedUnitSpecial(selected);
        DrawGameplayCommandButton(graphics, _gameplayHudLayout.Contextual with { Frame = special.Frame, Label = special.Label },
            special.EngineBacked, special.SelectedMode);
        var secondary = SelectedUnitSecondary(selected);
        var displayedButtons = secondary is { } mappedSecondary
            ? new[]
            {
                _gameplayHudLayout.Stop, _gameplayHudLayout.MoveOnly, _gameplayHudLayout.MoveAndAttack,
                _gameplayHudLayout.Waypoints, _gameplayHudLayout.Contextual with { Frame = special.Frame, Label = special.Label },
                _gameplayHudLayout.Secondary with { Frame = mappedSecondary.Frame, Label = mappedSecondary.Label },
            }
            : new[]
            {
                _gameplayHudLayout.Stop, _gameplayHudLayout.MoveOnly, _gameplayHudLayout.MoveAndAttack,
                _gameplayHudLayout.Waypoints, _gameplayHudLayout.Contextual with { Frame = special.Frame, Label = special.Label },
            };
        if (secondary is not null)
            DrawGameplayCommandButton(graphics, _gameplayHudLayout.Secondary with { Frame = secondary.Value.Frame, Label = secondary.Value.Label },
                SecondaryCommandAvailable(secondary.Value), GameplayCommandMode.GroundSpecialTarget);

        var lead = selected[0];
        var definition = GameplayDefinition(lead);
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        var name = selected.Count == 1 && definition is not null ? definition.DisplayName : $"{selected.Count} Units";
        SetGameplayPanelIdentity(name);
        DrawGameplaySelectionDiagnostics(graphics, actor, definition);
        NoteHoveredHudControl(displayedButtons);
    }

    private void DrawObservedEntityHud(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        var lead = selected[0];
        var definition = GameplayDefinition(lead);
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        var name = selected.Count == 1 && definition is not null
            ? definition.DisplayName
            : $"{selected.Count} Units";
        SetGameplayPanelIdentity(name);
        var relation = lead.Team == _localPlayerTeam
            ? "LOCAL / NOT CONTROLLABLE"
            : _scenarioSimulation?.TeamRelations.IsHostile(_localPlayerTeam, lead.Team) == true
                ? "HOSTILE"
                : "ALLY";
        if (_showAssetNames)
        {
            DrawGameplayLowerReadout(graphics, $"{relation} TEAM {lead.Team + 1} - INSPECTION ONLY");
            if (definition is not null)
                DrawGameplayLowerReadout(graphics, GameplayStatsLine(actor, definition), bottom: true);
        }
    }

    private void DrawSelectedStructureHud(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        var lead = selected[0];
        var definition = _entityCatalog is not null && (uint)lead.EntityId < (uint)_entityCatalog.Entities.Count
            ? _entityCatalog[lead.EntityId] : null;
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        SetGameplayPanelIdentity(
            selected.Count == 1 && definition is not null ? definition.DisplayName : $"{selected.Count} Structures");
        // The original has no production per building: a selected structure
        // leaves the build catalog in place.
        DrawBuildCatalogButtons(graphics);
        // Structures use the same live actor state as units. Showing their
        // health and decoded stats here keeps production selection from
        // hiding damage state behind a generic instruction.
        if (_showAssetNames)
            DrawGameplayLowerReadout(graphics, definition is null
                ? "SELECT UNIT"
                : GameplayStatsLine(actor, definition), bottom: true);
    }

    private void DrawSelectedStaticCombatHud(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        // A deployed TURR/XENO is an active static weapon form, not a
        // production building and not its original mobile builder. Its only
        // recovered common controls are Stop (clear target) and direct Attack.
        DrawGameplayCommandButton(graphics, _gameplayHudLayout.Stop, true);
        DrawGameplayCommandButton(graphics, _gameplayHudLayout.MoveAndAttack, selected.Any(HasWeapon), GameplayCommandMode.AttackTarget);
        var lead = selected[0];
        var definition = GameplayDefinition(lead);
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        var name = selected.Count == 1 && definition is not null ? definition.DisplayName : $"{selected.Count} Static";
        SetGameplayPanelIdentity(name);
        DrawGameplaySelectionDiagnostics(graphics, actor, definition);
        NoteHoveredHudControl(_gameplayHudLayout.Stop, _gameplayHudLayout.MoveAndAttack);
    }

    private void DrawSelectedStealStanceHud(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        DrawGameplayCommandButton(graphics,
            _gameplayHudLayout.Contextual with { Frame = 75, Label = "STEAL MONEY" }, true);
        var lead = selected[0];
        var definition = GameplayDefinition(lead);
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        var name = selected.Count == 1 && definition is not null ? definition.DisplayName : $"{selected.Count} Stealing";
        SetGameplayPanelIdentity(name);
        DrawGameplaySelectionDiagnostics(graphics, actor, definition, "STEALING: 50% NEARBY MINER P7");
        NoteHoveredHudControl(_gameplayHudLayout.Contextual with { Frame = 75, Label = "STEAL MONEY" });
    }

    private void DrawSelectedHarvesterHud(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        DrawGameplayCommandButton(graphics,
            _gameplayHudLayout.Contextual with { Frame = 74, Label = "DEPLOY" }, true);
        var lead = selected[0];
        var definition = GameplayDefinition(lead);
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        var name = selected.Count == 1 && definition is not null ? definition.DisplayName : $"{selected.Count} Miners";
        SetGameplayPanelIdentity(name);
        DrawGameplaySelectionDiagnostics(graphics, actor, definition,
            actor?.HarvestVentId is { } ventId ? $"P7 VENT {ventId + 1}: ATTACHED" : "P7 MINER: DEPLOYED");
        NoteHoveredHudControl(_gameplayHudLayout.Contextual with { Frame = 74, Label = "DEPLOY" });
    }

    private void DrawSelectedMineHud(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        var lead = selected[0];
        var definition = GameplayDefinition(lead);
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        var name = selected.Count == 1 && definition is not null ? definition.DisplayName : $"{selected.Count} Mines";
        SetGameplayPanelIdentity(name);
        var triggerCount = actor is null ? 0 : _scenarioSimulation?.MineTriggersRemainingFor(actor) ?? 0;
        var readiness = actor?.CooldownTicks > 0 ? $"REARM {actor.CooldownTicks}" : "READY";
        DrawGameplaySelectionDiagnostics(graphics, actor, definition,
            $"ARMED - {triggerCount} TRIGGER{(triggerCount == 1 ? string.Empty : "S")} - {readiness}");
    }

    private void DrawBuildCatalog(Graphics graphics)
    {
        DrawBuildCatalogButtons(graphics);
        SetGameplayPanelIdentity("Build");
    }

    private void DrawBuildCatalogButtons(Graphics graphics)
    {
        foreach (var (item, position) in CatalogButtons())
            DrawCatalogItem(graphics, item, position);
    }

    /// <summary>
    /// The build catalog shows while no mobile unit of the player is
    /// selected; the research catalog always.
    /// </summary>
    private bool CatalogShown =>
        _gameplayHudTab == GameplayHudTab.Research || _gameplayHudTab == GameplayHudTab.Build && !SelectedGameplayActors().Any();

    /// <summary>
    /// The offered gadgets of the current tab's catalog and their places:
    /// <c>0x437EA0</c> shows only records in state 1. Gadgets that share a
    /// place (a building and its upgrade) are never offered together.
    /// </summary>
    private IEnumerable<(DependencyDefinition Item, Point Position)> CatalogButtons()
    {
        if (_dependencyCatalog is null || _scenarioSimulation is not { } simulation) yield break;
        var research = _gameplayHudTab == GameplayHudTab.Research;
        foreach (var item in _dependencyCatalog.Items.Values.OrderBy(item => item.Id))
        {
            if (item.IsUpgrade != research) continue;
            var slots = item.IsTroop ? TroopSlots() : item.IsBuilding ? BuildingSlots() : _grayRace ? GrayResearchSlots : HumanResearchSlots;
            if (slots.TryGetValue(item.UiId, out var position) && simulation.CatalogState(_localPlayerTeam, item.Id) == CatalogItemState.Offered)
                yield return (item, position);
        }
    }

    /// <summary>
    /// An offered gadget and its count, which <c>maine</c> writes at the
    /// gadget's <c>offset</c> in font 0 on the <c>erase</c> colour. An
    /// unaffordable item is drawn like any other: P7 is only checked on the click.
    /// </summary>
    private void DrawCatalogItem(Graphics graphics, DependencyDefinition item, Point position)
    {
        DrawCatalogButton(graphics, item.UiId, position, available: true);
        var count = _scenarioSimulation?.EconomyForTeam(_localPlayerTeam)?.CatalogCount(item.Id) ?? 0;
        if (count <= 0 || _gameplayHudLayout.CatalogCountOrigin(item.UiId) is not { } origin || _installation is null) return;
        var font = LoadFont("mfonto7");
        var text = count.ToString(CultureInfo.InvariantCulture);
        var cell = font.Sprite.Frames[0].Width + 1;
        FillNative(graphics, new Rectangle(origin, new Size(text.Length * cell, font.Sprite.Frames[0].Height)), Color.Black);
        DrawCellText(graphics, text, origin.X, origin.Y, font, colour: 4, palette: "intrface");
    }

    private IReadOnlyDictionary<int, Point> TroopSlots() => _grayRace ? GrayTroopSlots : HumanTroopSlots;

    private IReadOnlyDictionary<int, Point> BuildingSlots() => _grayRace ? GrayBuildingSlots : HumanBuildingSlots;

    private Rectangle CatalogBounds(int uiId, Point fallbackPosition) =>
        _gameplayHudLayout.CatalogBounds(uiId, new Rectangle(fallbackPosition, new Size(59, 41)));

    private string HarvesterHudStatus(SimulatedActor actor, int ventId)
    {
        var vent = _scenarioSimulation?.PetraVents.ElementAtOrDefault(ventId);
        if (vent?.HarvesterInstanceId == actor.Seed.InstanceId)
            return $"P7 VENT {ventId + 1}: ATTACHED";
        if (vent?.PendingHarvesterInstanceId == actor.Seed.InstanceId)
            return $"P7 VENT {ventId + 1}: DEPLOYING {vent.AttachTicksRemaining}";
        return $"P7 VENT {ventId + 1}: EN ROUTE";
    }

    private string? ActiveOrderHudStatus(SimulatedActor actor)
    {
        if (actor.InspireCastTicksRemaining > 0)
            return $"INSPIRE: CASTING {actor.InspireCastTicksRemaining}";
        if (actor.GroundSpecialAttackTarget is { } specialTarget)
        {
            var label = UnitSecondaryCommandCatalog.TryGet(_scenarioSimulation!.EffectiveDefinition(actor), out var special)
                ? special.Label.Replace(" ATTACK", string.Empty, StringComparison.Ordinal)
                : "SPECIAL";
            if (_scenarioSimulation.IsGroundSpecialTargetInRange(actor))
            {
                if (actor.Facing.Current != actor.Facing.Target) return $"{label} ({specialTarget.X},{specialTarget.Z}): TURNING";
                if (actor.CooldownTicks > 0) return $"{label} ({specialTarget.X},{specialTarget.Z}): WAIT {actor.CooldownTicks}";
                return $"{label} ({specialTarget.X},{specialTarget.Z}): READY";
            }
            return $"{label} ({specialTarget.X},{specialTarget.Z}): APPROACHING";
        }
        if (actor.AttackTargetInstanceId is { } targetId)
        {
            if (_scenarioSimulation?.IsAttackTargetInRange(actor) != true)
                return $"TARGET #{targetId}: OUT OF RANGE";
            var weapon = _scenarioSimulation.EffectiveWeaponFor(actor);
            if (actor.CooldownTicks > 0)
            {
                var burst = weapon?.BurstShotLimit > 0 ? $" B{actor.BurstShotCount}/{weapon.BurstShotLimit}" : string.Empty;
                return $"TARGET #{targetId}: RELOAD {actor.CooldownTicks}{burst}";
            }
            return $"TARGET #{targetId}: READY";
        }
        if (actor.HarvestVentId is { } ventId) return HarvesterHudStatus(actor, ventId);
        if (actor.AttackMoveDestination is { } attackMove) return $"ATTACK MOVE → ({attackMove.X},{attackMove.Z})";
        if (actor.MoveOrder is { } move)
        {
            var queued = move.PendingWaypointCount == 0 ? string.Empty : $" +{move.PendingWaypointCount} WP";
            return $"MOVE → ({move.Target.X},{move.Target.Z}){queued}";
        }
        return null;
    }

    private string BuildingLabel(DependencyDefinition item)
    {
        if (_buildingFootprints?.TryResolveBuildingEntity(item.BuildingFaction!.Value, item.BuildingVariant!.Value, item.BuildingSlot!.Value, out var entityId) == true &&
            _entityCatalog is not null && (uint)entityId < (uint)_entityCatalog.Entities.Count)
            return _entityCatalog[entityId].DisplayName.ToUpperInvariant();
        return $"BUILD {item.Id}";
    }

    private string GameplayWeaponLabel(SimulatedActor? actor, EntityDefinition definition)
    {
        var weapon = actor is null ? null : _scenarioSimulation?.EffectiveWeaponFor(actor);
        if (weapon is not null) return weapon.DisplayName.ToUpperInvariant();
        var baseWeaponId = definition.WeaponSlots.FirstOrDefault(slot => slot >= 0);
        return baseWeaponId >= 0 && _weaponCatalog?.TryGet(baseWeaponId, out var baseWeapon) == true
            ? baseWeapon.DisplayName.ToUpperInvariant()
            : "UNARMED";
    }

    private string GameplayStatsLine(SimulatedActor? actor, EntityDefinition definition)
    {
        var weapon = actor is null ? null : _scenarioSimulation?.EffectiveWeaponFor(actor);
        if (weapon is null)
        {
            var baseWeaponId = definition.WeaponSlots.FirstOrDefault(slot => slot >= 0);
            if (baseWeaponId >= 0) _weaponCatalog?.TryGet(baseWeaponId, out weapon);
        }
        var weaponLevel = actor is null ? 0 : _scenarioSimulation?.WeaponUpgradeLevel(actor) ?? 0;
        var armorLevel = actor is null ? 0 : _scenarioSimulation?.ArmorUpgradeLevel(actor) ?? 0;
        var sight = actor is null ? definition.DayObservation : _scenarioSimulation?.ObservationRange(actor) ?? definition.DayObservation;
        var otherSight = actor is null ? definition.NightObservation :
            (_scenarioSimulation?.DayNight.Phase == DayNightPhase.Day ? definition.NightObservation : definition.DayObservation);
        var weaponStats = weapon is null ? "UNARMED" :
            $"DMG {weapon.Damage} RNG {weapon.Range}" + (weapon.AreaEffectTemplateId > 0 ? $" AOE {weapon.AreaEffectTemplateId}" : string.Empty);
        var specialTechnology = GameplaySpecialTechnologyStatus(actor);
        return $"HP {actor?.Health ?? definition.Health}/{actor?.MaximumHealth ?? definition.Health} SPD {definition.MovementSpeed} {weaponStats} SIGHT {sight}/{otherSight} WPN+{weaponLevel} ARM+{armorLevel}{specialTechnology}";
    }

    private string GameplaySpecialTechnologyStatus(SimulatedActor? actor)
    {
        var status = actor?.InspirationTicksRemaining > 0
            ? $" INSPIRED:{actor.InspirationTicksRemaining}"
            : string.Empty;
        if (actor?.Definition.Code is "BEON" or "ZISP")
            status += $" HEAL:{actor.AbilityCharge}/{SimulatedActor.NativeMaximumAbilityCharge}";
        if (actor is null || !UnitSecondaryCommandCatalog.TryGet(actor.Definition, out var special) ||
            special.RequiredResearchItemId is not { } itemId) return status;
        var researched = _scenarioSimulation?.EconomyForTeam(actor.Seed.Team)?.CompletedItems.Contains(itemId) == true;
        // This is capability state only; it deliberately does not claim that
        // the untraced command executor itself is available.
        return status + $" {special.Label.Replace(" ATTACK", string.Empty, StringComparison.Ordinal)} TECH:{(researched ? "ON" : "OFF")}";
    }

    /// <summary>
    /// The research catalog: like the build catalog, every offered upgrade
    /// of the race. Command 12 needs no building.
    /// </summary>
    private void DrawResearchCatalog(Graphics graphics)
    {
        foreach (var (item, position) in CatalogButtons())
            DrawCatalogItem(graphics, item, position);
        SetGameplayPanelIdentity("Research");
    }

    private void DrawOptionsCatalog(Graphics graphics)
    {
        if (_showAlliesPanel)
        {
            DrawAlliesPanel(graphics);
            return;
        }
        var entries = new[]
        {
            _gameplayHudLayout.Quit,
            _gameplayHudLayout.SaveGame,
            _gameplayHudLayout.Options,
            _gameplayHudLayout.Allies,
            _gameplayHudLayout.Pause with { Label = _gameplayPaused ? "RESUME" : "PAUSE" },
            _gameplayHudLayout.Objectives,
        };
        foreach (var entry in entries)
            DrawGameplayCommandButton(graphics, entry, entry.UiId is 62 or 196);
        NoteHoveredHudControl(entries);
        SetGameplayPanelIdentity(_gameplayPaused ? "PAUSED" : "GAME OPTIONS");
    }

    private void DrawAlliesPanel(Graphics graphics)
    {
        var slots = AllianceSlots();
        DrawMappedCatalogButton(graphics, slots[0], 117, "BACK", true);
        var simulation = _scenarioSimulation;
        if (simulation is not null)
        {
            var teams = AllianceTargetTeams();
            for (var index = 0; index < teams.Count && index + 1 < slots.Length; index++)
            {
                var team = teams[index];
                var allied = !simulation.TeamRelations.IsHostile(_localPlayerTeam, team);
                DrawMappedCatalogButton(graphics, slots[index + 1], 117, $"T{team + 1} {(allied ? "ALLY" : "FOE")}", true);
            }
        }
        SetGameplayPanelIdentity("ALLIES");
    }

    private Rectangle[] AllianceSlots() =>
    [
        .. new Rectangle[]
        {
            new(518, 112, 59, 41), new(577, 112, 59, 41),
            new(518, 153, 59, 41), new(577, 153, 59, 41),
            new(518, 194, 59, 41), new(577, 194, 59, 41),
            new(518, 235, 59, 41), new(577, 235, 59, 41),
        }.Select(_gameplayScreen.Anchor),
    ];

    private IReadOnlyList<int> AllianceTargetTeams() => _scenarioSimulation?.TeamResources.Keys
        .Where(team => team != _localPlayerTeam)
        .OrderBy(team => team)
        .Take(AllianceSlots().Length - 1)
        .ToArray() ?? [];

    /// <summary>
    /// Draws a production or research gadget with its authored <c>mainbut</c>
    /// picture and no caption: the original names a hovered gadget (name and
    /// price, its <c>textmsg</c>) in UI 79 instead.
    /// </summary>
    private void DrawCatalogButton(Graphics graphics, int uiId, Point fallbackPosition, bool available)
    {
        var bounds = CatalogBounds(uiId, fallbackPosition);
        var hovered = _gameplayPointer is { } pointer && bounds.Contains(pointer);
        DrawGameplayIconButton(graphics, bounds, _gameplayHudLayout.CatalogFrame(uiId) ?? -1, hovered, available);
        if (hovered) _hoveredHudText = _gameplayHudLayout.ControlText(uiId);
    }

    private void DrawGameplayHudPicture(Graphics graphics, Point location, int frame)
    {
        if (SpriteFrameBitmap("mainbut", frame) is not { } bitmap) return;
        if (_activeCanvas is { } canvas) canvas.DrawForeground(GpuBitmap(bitmap), location.X, location.Y);
        else graphics.DrawImageUnscaled(bitmap, location);
    }

    /// <summary>Records the hovered control's text for UI 79 (0x4337c8).</summary>
    private void NoteHoveredHudControl(params GameplayHudButton[] buttons)
    {
        if (HoveredGameplayCommandLabel(buttons) is { } label) _hoveredHudText = label;
    }

    private void DrawMappedCatalogButton(Graphics graphics, Rectangle bounds, int frame, string label, bool available = false)
    {
        DrawGameplayIconButton(graphics, bounds, frame, false, available);
        DrawGameplayHudText(graphics, label, new Rectangle(bounds.X + 2, bounds.Y + 27, bounds.Width - 4, 12));
    }

    private void DrawGameplayCommandButton(
        Graphics graphics,
        GameplayHudButton button,
        bool available,
        GameplayCommandMode? selectedMode = null,
        bool isChecked = false)
    {
        var hovered = _gameplayPointer is { } pointer && button.Bounds.Contains(pointer);
        DrawGameplayIconButton(graphics, button.Bounds, button.Frame, isChecked || selectedMode == _gameplayCommandMode || hovered, available);
    }

    private string? HoveredGameplayCommandLabel(IReadOnlyList<GameplayHudButton> buttons)
    {
        if (_gameplayPointer is not { } pointer) return null;
        return buttons.FirstOrDefault(button => button.Bounds.Contains(pointer))?.Label;
    }

    /// <summary>
    /// The HUD clock (<c>0x43A9F8</c>): a frame of <c>sprites/cloc.spr</c> at
    /// (608, 450), its hand going round once by day and once by night
    /// (<see cref="DayNightPresentation.ClockFrame"/>).
    /// </summary>
    private void DrawGameplayClock(Graphics graphics, DayNightCycle cycle)
    {
        int frames;
        try { frames = LoadSprite("cloc").Frames.Count; }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException) { return; }
        if (frames < 2 || SpriteFrameBitmap("cloc", DayNightPresentation.ClockFrame(cycle, frames)) is not { } bitmap) return;
        var bounds = _gameplayScreen.Anchor(new Rectangle(608, 450, bitmap.Width, bitmap.Height));
        if (_activeCanvas is { } canvas) canvas.DrawForeground(GpuBitmap(bitmap), bounds.X, bounds.Y);
        else graphics.DrawImageUnscaled(bitmap, bounds.Location);
    }

    private void DrawGameplayIconButton(Graphics graphics, Rectangle bounds, int frame, bool selected, bool available)
    {
        var bitmap = SpriteFrameBitmap("mainbut", frame);
        if (_activeCanvas is { } canvas)
        {
            if (bitmap is not null) canvas.DrawForeground(GpuBitmap(bitmap), bounds.X, bounds.Y);
            else canvas.FillForeground(bounds, Color.FromArgb(70, 90, 105));
            var borderColor = selected ? Color.FromArgb(130, 255, 244, 85) : Color.FromArgb(80, 8, 12, 8);
            var thickness = selected ? 2 : 1;
            canvas.FillForeground(new Rectangle(bounds.X, bounds.Y, bounds.Width, thickness), borderColor);
            canvas.FillForeground(new Rectangle(bounds.X, bounds.Bottom - thickness, bounds.Width, thickness), borderColor);
            canvas.FillForeground(new Rectangle(bounds.X, bounds.Y, thickness, bounds.Height), borderColor);
            canvas.FillForeground(new Rectangle(bounds.Right - thickness, bounds.Y, thickness, bounds.Height), borderColor);
            if (!available) canvas.FillForeground(bounds, Color.FromArgb(150, 0, 0, 0));
            return;
        }
        if (bitmap is not null) graphics.DrawImageUnscaled(bitmap, bounds.Location);
        else { using var fill = new SolidBrush(Color.FromArgb(70, 90, 105)); graphics.FillRectangle(fill, bounds); }
        using var border = new Pen(selected ? Color.FromArgb(130, 255, 244, 85) : Color.FromArgb(80, 8, 12, 8), selected ? 2 : 1);
        graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        if (!available)
        {
            using var disabled = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            graphics.FillRectangle(disabled, bounds);
        }
    }

    private void DrawGameplayPetraCounter(Graphics graphics, int amount)
    {
        var counter = _gameplayHudLayout.PetraCounter;
        var text = Math.Clamp(amount, 0, 999_999).ToString();
        const int digitWidth = 12;
        var x = counter.Bounds.Right - text.Length * digitWidth;
        foreach (var digit in text)
        {
            var bitmap = SpriteFrameBitmap("mainbut", counter.Frame + digit - '0');
            if (bitmap is not null)
            {
                if (_activeCanvas is { } canvas) canvas.DrawForeground(GpuBitmap(bitmap), x, counter.Bounds.Y);
                else graphics.DrawImageUnscaled(bitmap, x, counter.Bounds.Y);
            }
            x += digitWidth;
        }
    }

    private void DrawGameplayHudText(Graphics graphics, string text, Rectangle bounds)
    {
        if (DrawMenuText(graphics, text, bounds, center: false, remap: Color.FromArgb(205, 225, 190))) return;
        using var font = new Font(FontFamily.GenericMonospace, 10, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(220, 230, 205));
        graphics.DrawString(text, font, brush, bounds.Location);
    }

    /// <summary>Sets the idle text of UI 79; a later call replaces an earlier one.</summary>
    private void SetGameplayPanelIdentity(string text) => _panelIdentityText = text;

    /// <summary>
    /// Draws UI 79 once per frame. Hovering a control shows its text
    /// (0x4337c8); otherwise a waypoint/target prompt (0x4096f6), otherwise
    /// the selection or panel name.
    /// </summary>
    private void DrawGameplayPanelIdentityStrip(Graphics graphics)
    {
        var prompt = _gameplayCommandMode switch
        {
            GameplayCommandMode.Waypoints => _gameplayHudLayout.SetWaypointsMessage,
            GameplayCommandMode.AttackTarget or GameplayCommandMode.GroundSpecialTarget or GameplayCommandMode.HarvestVent => _gameplayHudLayout.SelectTargetMessage,
            _ => null,
        };
        if ((_hoveredHudText ?? prompt ?? _panelIdentityText) is { Length: > 0 } text) DrawGameplayPanelIdentityText(graphics, text);
    }

    private void DrawGameplayPanelIdentityText(Graphics graphics, string text)
    {
        var readout = _gameplayHudLayout.PanelIdentity;
        var fitted = FitGameplayReadout(text, readout.CharacterCapacity);
        var bounds = new Rectangle(readout.Origin, new Size(112, 14));
        // `maine` control 79 uses remap 2. The original selected-Trooper
        // capture renders this strip in a warm yellow, distinct from the pale
        // green lower message readouts.
        if (DrawMenuText(graphics, fitted, bounds, center: false, remap: Color.FromArgb(250, 225, 95))) return;
        using var font = new Font(FontFamily.GenericMonospace, 10, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(250, 225, 95));
        graphics.DrawString(fitted, font, brush, bounds.Location);
    }

    private void DrawGameplayLowerReadout(Graphics graphics, string text, bool bottom = false)
    {
        var readout = bottom ? _gameplayHudLayout.LowerReadoutBottom : _gameplayHudLayout.LowerReadoutTop;
        DrawGameplayHudText(graphics, FitGameplayReadout(text, readout.CharacterCapacity),
            new Rectangle(readout.Origin, new Size(500, 14)));
    }

    private void DrawGameplaySelectionDiagnostics(
        Graphics graphics,
        SimulatedActor? actor,
        EntityDefinition? definition,
        string? context = null)
    {
        if (!_showAssetNames) return;
        var state = context ?? (actor is null ? null : ActiveOrderHudStatus(actor)) ?? GameplayCommandModeLabel();
        DrawGameplayLowerReadout(graphics, state);
        if (definition is not null)
            DrawGameplayLowerReadout(graphics, GameplayStatsLine(actor, definition), bottom: true);
    }

    private static string FitGameplayReadout(string text, int capacity) =>
        text.Length <= capacity ? text : text[..capacity];

    private string GameplayCommandModeLabel() => _gameplayCommandMode switch
    {
        GameplayCommandMode.Waypoints => "WAYPOINT MODE",
        GameplayCommandMode.AttackTarget => "MOVE & ATTACK",
        GameplayCommandMode.GroundSpecialTarget => "SPECIAL GROUND TARGET",
        GameplayCommandMode.HarvestVent => "DEPLOY ON VENT",
        GameplayCommandMode.PlaceBuilding => "DROP BUILDING",
        _ => "MOVE MODE",
    };

    private string SecondaryCommandHudStatus(UnitSecondary command)
    {
        if (command.RequiredResearchItemId is { } itemId &&
            _scenarioSimulation?.EconomyForTeam(_localPlayerTeam)?.CompletedItems.Contains(itemId) != true)
            return "TECH REQUIRED";
        return command.WeaponId is null ? "EXECUTOR PENDING" : "GROUND TARGET READY";
    }
}
