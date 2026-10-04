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

/// <summary>Mouse and keyboard handling: menus, camera, selection, and HUD clicks.</summary>
public sealed partial class MainForm
{
    protected override bool ProcessCmdKey(ref Message message, Keys keyData) =>
        HandleNewGameKey(keyData & Keys.KeyCode) || HandleGameplayKey(keyData) || base.ProcessCmdKey(ref message, keyData);

    private bool HandleGameplayKey(Keys keyData)
    {
        if (_screen != MenuScreenId.Gameplay) return false;
        var key = keyData & Keys.KeyCode;
        var shift = keyData.HasFlag(Keys.Shift);
        var control = keyData.HasFlag(Keys.Control);
        var alt = keyData.HasFlag(Keys.Alt);
        if (key == Keys.Escape && _gameplayCommandMode != GameplayCommandMode.MoveOnly)
        {
            // A paid building reservation has no recovered refund/cancel path;
            // keep its placement mode live rather than silently losing the
            // only way to complete it. Other retained map commands cancel
            // first, leaving the current selection intact for the next order.
            if (_gameplayCommandMode == GameplayCommandMode.PlaceBuilding && _pendingBuildingItemId is not null)
            {
                _status = "Building drop remains pending; right-click a valid map origin to complete the paid reservation.";
                return true;
            }
            _gameplayCommandMode = GameplayCommandMode.MoveOnly;
            _diagnosticMoveTarget = null;
            _diagnosticPathCells = [];
            _status = "Command mode cancelled.";
            return true;
        }
        if (key == Keys.Escape && _selectedEntityInstanceIds.Count != 0)
        {
            _selectedEntityInstanceIds.Clear();
            _gameplayCommandMode = GameplayCommandMode.MoveOnly;
            _diagnosticMoveTarget = null;
            _diagnosticPathCells = [];
            _deathEffects.Clear();
            _status = "Selection cleared.";
            return true;
        }
        // F1-F10 are native unit-type selections. Keep diagnostics in a
        // port-owned F12 namespace so Ctrl/Alt/Shift remain available to the
        // original selection filters rather than being stolen by debug UI.
        if (key == Keys.F12 && !shift)
        {
            _showAssetNames = !_showAssetNames;
            _status = $"Gameplay asset names {(_showAssetNames ? "on" : "off")} (F12).";
            return true;
        }
        if (key == Keys.F12 && shift)
        {
            _showPathRegions = !_showPathRegions;
            _status = $"PTH region diagnostic {(_showPathRegions ? "on" : "off")} (Shift+F12); zero is an unresolved sentinel.";
            return true;
        }
        var functionKey = key switch
        {
            Keys.F1 => 1, Keys.F2 => 2, Keys.F3 => 3, Keys.F4 => 4, Keys.F5 => 5,
            Keys.F6 => 6, Keys.F7 => 7, Keys.F8 => 8, Keys.F9 => 9, Keys.F10 => 10,
            _ => 0,
        };
        if (functionKey != 0)
        {
            SelectGameplayActorsByFunctionKey(functionKey, shift,
                UnitSelectionLayerFilter.FromModifiers(control, alt));
            return true;
        }
        if (key == Keys.S)
        {
            StopSelectedUnits();
            return true;
        }
        if (key is Keys.M or Keys.W)
        {
            if (!SelectedGameplayActors().Any())
            {
                _status = "Select at least one local mobile unit first.";
                return true;
            }
            SetGameplayCommandMode(key == Keys.M ? GameplayCommandMode.MoveOnly : GameplayCommandMode.Waypoints);
            return true;
        }
        if (key == Keys.Enter)
        {
            ExecuteNativeImmediateSpecial();
            return true;
        }
        var delta = key switch
        {
            Keys.Left => new Point(-16, 0),
            Keys.Right => new Point(16, 0),
            Keys.Up => new Point(0, -16),
            Keys.Down => new Point(0, 16),
            _ => Point.Empty,
        };
        if (delta == Point.Empty) return false;
        MoveGameplayCamera(delta.X, delta.Y);
        return true;
    }

    /// <summary>
    /// Screen changes can be initiated by a click, keyboard shortcut, or a
    /// scenario event. Never let a captured drag from the prior screen become
    /// a selection/minimap/scroll gesture in the newly composed screen.
    /// </summary>
    private void ClearTransientInputState()
    {
        _mapDragStart = null;
        _mapDragged = false;
        _minimapDragging = false;
        _selectionDragStart = null;
        _selectionGestureStartedAtTick = 0;
        _selectionGestureToggle = false;
        _selectionGestureLayerFilter = default;
        _singlePlayerScrollDragging = false;
        _gameplayPointer = null;
        _surface.Capture = false;
    }

    private void HandleBuildCatalogPurchase(Point point)
    {
        var item = BuildCatalogItemAt(point);
        if (item is null) return;
        var eligibility = PurchaseEligibilityFor(item);
        if (eligibility != PurchaseEligibility.Available)
        {
            var label = item.IsBuilding ? BuildingLabel(item) : TroopLabel(item);
            _status = $"{label} unavailable: {eligibility}.";
            return;
        }
        if (item.IsBuilding)
        {
            _pendingBuildingItemId = item.Id;
            _gameplayCommandMode = GameplayCommandMode.PlaceBuilding;
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new PurchaseIntent(_localPlayerTeam, item.Id));
            _status = $"{BuildingLabel(item)} reserved if P7 and prerequisites allow; right-click a clear drop location.";
            return;
        }

        var anchor = ProductionAnchorFor(item);
        if (anchor is null)
        {
            _status = $"{TroopLabel(item)} requires one completed prerequisite structure before this port can place its spawn.";
            return;
        }
        _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new PurchaseIntent(_localPlayerTeam, item.Id));
        _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new ProduceUnitIntent(_localPlayerTeam, item.Id, anchor.InstanceId));
        _status = $"{TroopLabel(item)} ordered if P7 and tech-tree requirements allow.";
    }

    /// <summary>
    /// Adapts one selected source structure's recovered production buttons to
    /// the command queue. This deliberately does not use the global fallback
    /// anchor: the player selected this building, and the engine must validate
    /// that exact instance against the troop's dependency record.
    /// </summary>
    private void HandleProductionPurchase(Point point, WorldEntity structure)
    {
        var item = TroopItemsForStructure(structure)
            .FirstOrDefault(candidate => TroopSlots().TryGetValue(candidate.UiId, out var position) &&
                CatalogBounds(candidate.UiId, position).Contains(point));
        if (item is null) return;
        var eligibility = PurchaseEligibilityFor(item);
        if (eligibility != PurchaseEligibility.Available)
        {
            _status = $"{TroopLabel(item)} unavailable: {eligibility}.";
            return;
        }
        _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new PurchaseIntent(_localPlayerTeam, item.Id));
        _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1,
            new ProduceUnitIntent(_localPlayerTeam, item.Id, structure.InstanceId));
        _status = $"{TroopLabel(item)} ordered from structure #{structure.InstanceId} if P7 and tech-tree requirements allow.";
    }

    private void SetGameplayCommandMode(GameplayCommandMode mode)
    {
        _gameplayCommandMode = mode;
        // Native mode prompts are continuously projected through UI 79 by
        // the gameplay input loop; they are not appended to message history.
        _surface.Invalidate();
    }

    private void SurfaceMouseMove(object? sender, MouseEventArgs eventArgs)
    {
        if (_screen == MenuScreenId.Gameplay)
        {
            _gameplayPointer = eventArgs.Location;
        }
        if (_screen == MenuScreenId.Gameplay && _minimapDragging &&
            (eventArgs.Button & (MouseButtons.Left | MouseButtons.Right)) != 0)
        {
            SetGameplayCameraFromMinimap(eventArgs.Location);
            return;
        }
        if (_screen == MenuScreenId.Gameplay && _selectionDragStart is not null && eventArgs.Button.HasFlag(MouseButtons.Left))
        {
            _selectionDragCurrent = eventArgs.Location;
            _surface.Invalidate();
            return;
        }
        if (_screen == MenuScreenId.Gameplay && _mapDragStart is { } start && eventArgs.Button.HasFlag(MouseButtons.Middle))
        {
            var dx = eventArgs.X - start.X;
            var dy = eventArgs.Y - start.Y;
            if (_mapDragged || Math.Abs(dx) > 8 || Math.Abs(dy) > 8)
            {
                _mapDragged = true;
                SetGameplayCamera(_mapDragCamera.X - dx, _mapDragCamera.Y - dy);
            }
            return;
        }
        if (_screen == MenuScreenId.SinglePlayer && _singlePlayerScrollDragging && eventArgs.Button.HasFlag(MouseButtons.Left))
        {
            SelectSinglePlayerMapFromScroll(eventArgs.Location);
            _surface.Invalidate();
            return;
        }
        SetHover(_buttons.LastOrDefault(button => button.Bounds.Contains(eventArgs.Location))?.Id);
    }

    private void SetHover(int? id)
    {
        if (_hoveredButton == id) return;
        _hoveredButton = id;
        _surface.Invalidate();
    }

    private void SurfaceMouseDown(object? sender, MouseEventArgs eventArgs)
    {
        _surface.Focus();
        if (_screen == MenuScreenId.SinglePlayer && eventArgs.Button == MouseButtons.Left &&
            new Rectangle(596, 227, 10, 61).Contains(eventArgs.Location))
        {
            _singlePlayerScrollDragging = true;
            SelectSinglePlayerMapFromScroll(eventArgs.Location);
            _surface.Capture = true;
            _pressedButton = null;
            _surface.Invalidate();
            return;
        }
        if (_screen == MenuScreenId.Gameplay && eventArgs.Button is MouseButtons.Left or MouseButtons.Right && GameplayMinimapBounds.Contains(eventArgs.Location))
        {
            _minimapDragging = true;
            SetGameplayCameraFromMinimap(eventArgs.Location);
            _surface.Capture = true;
            _pressedButton = null;
            _surface.Invalidate();
            return;
        }
        if (_screen == MenuScreenId.Gameplay && eventArgs.Button == MouseButtons.Left && eventArgs.X < 516 && eventArgs.Y < 458)
        {
            _selectionDragStart = eventArgs.Location;
            _selectionDragCurrent = eventArgs.Location;
            _selectionGestureStartedAtTick = _world.TickCount;
            _selectionGestureToggle = ModifierKeys.HasFlag(Keys.Shift);
            _selectionGestureLayerFilter = UnitSelectionLayerFilter.FromModifiers(
                ModifierKeys.HasFlag(Keys.Control),
                ModifierKeys.HasFlag(Keys.Alt));
            _surface.Capture = true;
        }
        if (_screen == MenuScreenId.Gameplay && eventArgs.Button == MouseButtons.Middle && eventArgs.X < 516 && eventArgs.Y < 458)
        {
            _mapDragStart = eventArgs.Location;
            _mapDragCamera = new Point(_cameraX, _cameraY);
            _mapDragged = false;
            _surface.Capture = true;
        }
        if (eventArgs.Button != MouseButtons.Left) return;
        _pressedButton = _buttons.LastOrDefault(button => button.Bounds.Contains(eventArgs.Location))?.Id;
        _surface.Invalidate();
    }

    private void SurfaceMouseUp(object? sender, MouseEventArgs eventArgs)
    {
        var wasMapDrag = _mapDragged;
        var wasMinimapDrag = _minimapDragging;
        var wasSinglePlayerScrollDrag = _singlePlayerScrollDragging;
        var selectionStart = _selectionDragStart;
        var selectionGestureStartedAtTick = _selectionGestureStartedAtTick;
        var selectionToggle = _selectionGestureToggle;
        var selectionLayerFilter = _selectionGestureLayerFilter;
        var selectionBounds = selectionStart is { } start
            ? GameplaySelectionBounds(start, eventArgs.Location)
            : Rectangle.Empty;
        var wasSelectionDrag = selectionStart is { } dragStart && IsSelectionBoxGesture(dragStart, eventArgs.Location, selectionGestureStartedAtTick);
        _mapDragStart = null;
        _mapDragged = false;
        _minimapDragging = false;
        _selectionDragStart = null;
        _selectionGestureStartedAtTick = 0;
        _selectionGestureToggle = false;
        _selectionGestureLayerFilter = default;
        _singlePlayerScrollDragging = false;
        _surface.Capture = false;
        if (wasSinglePlayerScrollDrag)
        {
            _pressedButton = null;
            _surface.Invalidate();
            return;
        }
        if (wasMinimapDrag)
        {
            _pressedButton = null;
            _surface.Invalidate();
            return;
        }
        var pressed = _pressedButton;
        _pressedButton = null;
        var button = _buttons.LastOrDefault(candidate => candidate.Id == pressed && candidate.Bounds.Contains(eventArgs.Location));
        button?.Action();
        if (button is null && _screen == MenuScreenId.Gameplay && eventArgs.Button == MouseButtons.Left && HandleGameplayHudClick(eventArgs.Location))
        {
            _surface.Invalidate();
            return;
        }
        if (button is null && wasSelectionDrag && _screen == MenuScreenId.Gameplay && eventArgs.Button == MouseButtons.Left)
        {
            SelectGameplayActorsInRectangle(selectionBounds, selectionToggle, selectionLayerFilter);
        }
        else if (button is null && !wasMapDrag && _screen == MenuScreenId.Gameplay && eventArgs.Button == MouseButtons.Left)
        {
            SelectGameplayActor(eventArgs.Location, selectionToggle, selectionLayerFilter);
        }
        if (button is null && _screen == MenuScreenId.Gameplay && eventArgs.Button == MouseButtons.Right)
        {
            QueueDiagnosticMove(eventArgs.Location);
        }
        if (button is null && _screen == MenuScreenId.SinglePlayer && eventArgs.Button == MouseButtons.Left)
        {
            if (HandleWarLobbyClick(eventArgs.Location))
            {
                _surface.Invalidate();
                return;
            }
            SelectSinglePlayerMapAt(eventArgs.Location);
        }
        _surface.Invalidate();
    }

    private void SetGameplayCamera(int x, int y)
    {
        if (_gameplayMap is null) return;
        var previousX = _cameraX;
        var previousY = _cameraY;
        _cameraX = x;
        _cameraY = y;
        ClampGameplayCamera();
        if (_cameraX == previousX && _cameraY == previousY) return;
        _surface.Invalidate();
    }

    private void SetGameplayCameraFromMinimap(Point point)
    {
        if (_gameplayMap is null) return;
        var x = Math.Clamp(point.X - GameplayMinimapBounds.X, 0, GameplayMinimapBounds.Width - 1);
        var y = Math.Clamp(point.Y - GameplayMinimapBounds.Y, 0, GameplayMinimapBounds.Height - 1);
        // Same centred odd-numerator scaling as `0x409c94`; our camera is a
        // top-left viewport origin, so convert the requested world centre.
        var worldX = ((x * 2 + 1) * _gameplayMap.Width * TerrainRasterizer.TileSize) / (GameplayMinimapBounds.Width * 2);
        var worldY = (((GameplayMinimapBounds.Height - 1 - y) * 2 + 1) * _gameplayMap.Height * TerrainRasterizer.TileSize) / (GameplayMinimapBounds.Height * 2);
        SetGameplayCamera(worldX - 516 / 2, worldY - 458 / 2);
    }

    private void SetGameplayCursorVisibility(bool visible)
    {
        if (visible && _gameplayCursorHidden)
        {
            Cursor.Show();
            _gameplayCursorHidden = false;
        }
        else if (!visible && !_gameplayCursorHidden)
        {
            Cursor.Hide();
            _gameplayCursorHidden = true;
        }
    }

    private void UpdateGameplayEdgeScroll()
    {
        if (_screen != MenuScreenId.Gameplay || _gameplayPointer is not { } pointer || _mapDragStart is not null || _selectionDragStart is not null) return;
        if (pointer.X is < 0 or >= 516 || pointer.Y is < 0 or >= 458) return;
        var x = pointer.X <= 8 ? -16 : pointer.X >= 507 ? 16 : 0;
        var y = pointer.Y <= 8 ? -16 : pointer.Y >= 449 ? 16 : 0;
        if (x != 0 || y != 0) MoveGameplayCamera(x, y);
    }

    // `0x4096e8` promotes the ordinary left-button gesture at more than 45
    // Manhattan pixels, or after 1,500 ms. Direct camera dragging is retained
    // on the middle button as a port convenience; edge, keyboard, and minimap
    // navigation remain available through their own recovered paths.
    private bool IsSelectionBoxGesture(Point start, Point current, ulong startedAtTick) =>
        Math.Abs(current.X - start.X) + Math.Abs(current.Y - start.Y) > 45 ||
        _world.TickCount - startedAtTick >= 23; // 23 * 66 ms = 1,518 ms

    private static Rectangle GameplaySelectionBounds(Point start, Point current) => Rectangle.FromLTRB(
        Math.Min(start.X, current.X),
        Math.Min(start.Y, current.Y),
        Math.Max(start.X, current.X) + 1,
        Math.Max(start.Y, current.Y) + 1);

    private void SelectGameplayActor(Point point, bool toggle, UnitSelectionLayerFilter layerFilter)
    {
        if (_scenarioSimulation is null || _entityCatalog is null || _entityAnimations is null || point.X >= 516 || point.Y >= 458)
        {
            _selectedEntityInstanceIds.Clear();
            _status = "Selection cleared.";
            RevalidateGameplayCommandModeForSelection();
            return;
        }

        var selected = FindGameplayActorAt(point, locallyControllableOnly: false, layerFilter);
        var updated = UnitCommandProfiles.ApplyActorSelection(
            _selectedEntityInstanceIds,
            selected is null ? [] : [selected.InstanceId],
            toggle);
        updated = PreferLocallyOwnedSelection(updated);
        _selectedEntityInstanceIds.Clear();
        _selectedEntityInstanceIds.UnionWith(updated);
        var selectedDefinition = selected is null ? null : GameplayDefinition(selected);
        _status = selected is null
            ? toggle && _selectedEntityInstanceIds.Count != 0 ? $"{_selectedEntityInstanceIds.Count} selected." : "Selection cleared."
            : $"{_selectedEntityInstanceIds.Count} selected · #{selectedDefinition?.Id ?? selected.EntityId} {selectedDefinition?.DisplayName ?? _entityCatalog[selected.EntityId].DisplayName} · team {selected.Team}" +
              (IsLocallyControllable(selected) ? "." : " · inspection only.");
        RevalidateGameplayCommandModeForSelection();
        if (selected is not null && IsLocallyControllable(selected)) PlayGameplaySound(selectedDefinition?.Id ?? selected.EntityId, "SEL");
    }

    private WorldEntity? FindGameplayActorAt(
        Point point,
        bool locallyControllableOnly,
        UnitSelectionLayerFilter? selectionLayerFilter = null)
    {
        if (_scenarioSimulation is null || _entityCatalog is null || _entityAnimations is null || point.X >= 516 || point.Y >= 458) return null;
        WorldEntity? hit = null;
        foreach (var entity in GameplayEntities().OrderBy(entity => ActorPosition(entity).ZRaw).ThenBy(entity => ActorPosition(entity).XRaw))
        {
            if (locallyControllableOnly && !IsLocallyControllable(entity)) continue;
            if (selectionLayerFilter is { } filter && GameplayDefinition(entity) is { } definition && !filter.Includes(definition)) continue;
            var actorState = _scenarioSimulation.Actor(entity.InstanceId);
            if (!locallyControllableOnly && entity.Team != _localPlayerTeam &&
                (actorState is null || !_scenarioSimulation.IsActorVisibleToTeam(_localPlayerTeam, actorState))) continue;
            if (!TryGameplayActorVisual(entity, out var visual) || !visual.CanvasBounds.Contains(point)) continue;
            var localX = point.X - visual.CanvasBounds.X;
            var localY = point.Y - visual.CanvasBounds.Y;
            if (visual.Bitmap.GetPixel(localX, localY).A != 0) hit = entity;
        }
        return hit;
    }

    private static bool SelectionMaskIntersects(Rectangle selection, GameplayActorVisual visual)
    {
        if (!selection.IntersectsWith(visual.OpaqueBounds)) return false;
        var overlap = Rectangle.Intersect(selection, visual.CanvasBounds);
        if (overlap.Width <= 0 || overlap.Height <= 0) return false;
        for (var y = overlap.Top; y < overlap.Bottom; y++)
        for (var x = overlap.Left; x < overlap.Right; x++)
        {
            if (visual.Bitmap.GetPixel(x - visual.CanvasBounds.Left, y - visual.CanvasBounds.Top).A != 0)
                return true;
        }
        return false;
    }

    private void SelectGameplayActorsInRectangle(
        Rectangle bounds,
        bool toggle,
        UnitSelectionLayerFilter layerFilter)
    {
        if (_scenarioSimulation is null) return;
        var candidates = GameplayEntities()
            .Where(IsVisibleToLocalTeam)
            .Where(entity => GameplayDefinition(entity) is { } definition && layerFilter.Includes(definition))
            // Native box selection works from the selected body's mask, not
            // merely its transparent FIN canvas or its opaque bounding box.
            // Keep it on the exact same composed frame and alpha predicate as
            // click selection so a selection edge cannot catch empty pixels.
            .Where(entity => TryGameplayActorVisual(entity, out var visual) && SelectionMaskIntersects(bounds, visual))
            .Select(entity => entity.InstanceId)
            .OrderBy(instanceId => instanceId)
            .ToArray();
        var updated = UnitCommandProfiles.ApplyActorSelection(_selectedEntityInstanceIds, candidates, toggle);
        updated = PreferLocallyOwnedSelection(updated);
        _selectedEntityInstanceIds.Clear();
        _selectedEntityInstanceIds.UnionWith(updated);
        var locallyOwnedCount = SelectedInspectableGameplayEntities().Count(entity => entity.Team == _localPlayerTeam);
        _status = candidates.Length == 0
            ? "No visible units in selection box."
            : candidates.Length > UnitSelectionCommandProfile.MaximumActors
                ? _gameplayHudLayout.TooManyUnitsMessage
            : locallyOwnedCount != 0
                ? $"{_selectedEntityInstanceIds.Count} local unit(s) selected."
                : $"{_selectedEntityInstanceIds.Count} remote unit(s) selected for inspection.";
        RevalidateGameplayCommandModeForSelection();
        var localLead = SelectedGameplayEntities().FirstOrDefault();
        if (localLead is not null) PlayGameplaySound(GameplayDefinition(localLead)?.Id ?? localLead.EntityId, "SEL");
    }

    private void SelectGameplayActorsByFunctionKey(
        int functionKey,
        bool toggle,
        UnitSelectionLayerFilter layerFilter)
    {
        if (_scenarioSimulation is null || _entityCatalog is null) return;
        var entityIds = NativeUnitSelectionHotkeys.EntityIds(functionKey);
        var viewport = new Rectangle(0, 0, 516, 458);
        var candidates = GameplayEntities()
            .Where(IsVisibleToLocalTeam)
            .Where(entity => GameplayDefinition(entity) is { } definition && entityIds.Contains(definition.Id))
            .Where(entity => GameplayDefinition(entity) is { } definition && layerFilter.Includes(definition))
            .Where(entity => TryGameplayActorVisual(entity, out var visual) && viewport.IntersectsWith(visual.OpaqueBounds))
            .Select(entity => entity.InstanceId)
            .Order()
            .ToArray();
        var updated = UnitCommandProfiles.ApplyActorSelection(_selectedEntityInstanceIds, candidates, toggle);
        updated = PreferLocallyOwnedSelection(updated);
        _selectedEntityInstanceIds.Clear();
        _selectedEntityInstanceIds.UnionWith(updated);
        var localLead = SelectedGameplayEntities().FirstOrDefault();
        var mode = toggle ? "toggle" : "select";
        _status = candidates.Length == 0
            ? $"F{functionKey} {mode}: no matching visible units in the viewport."
            : $"F{functionKey} {mode}: {_selectedEntityInstanceIds.Count} unit(s).";
        RevalidateGameplayCommandModeForSelection();
        if (localLead is not null) PlayGameplaySound(GameplayDefinition(localLead)?.Id ?? localLead.EntityId, "SEL");
        _surface.Invalidate();
    }

    private IReadOnlyList<int> PreferLocallyOwnedSelection(IEnumerable<int> selectedInstanceIds)
    {
        var owners = GameplayEntities().ToDictionary(entity => entity.InstanceId, entity => entity.Team);
        return UnitCommandProfiles.PreferLocalOwnerSelection(selectedInstanceIds, owners, _localPlayerTeam);
    }

    private bool HandleGameplayHudClick(Point point)
    {
        if (_gameplayHudLayout.LastMessage.Bounds.Contains(point))
        {
            BrowseGameplayMessages(-1);
            return true;
        }
        if (_gameplayHudLayout.NextMessage.Bounds.Contains(point))
        {
            BrowseGameplayMessages(1);
            return true;
        }
        if (_gameplayHudLayout.BuildTab.Bounds.Contains(point))
        {
            _gameplayHudTab = GameplayHudTab.Build;
            _showAlliesPanel = false;
            _status = "Build tab: select a local unit for orders, or deselect to inspect the faction roster.";
            return true;
        }
        if (_gameplayHudLayout.ResearchTab.Bounds.Contains(point))
        {
            _gameplayHudTab = GameplayHudTab.Research;
            _showAlliesPanel = false;
            _status = "Research tab: select a completed structure to inspect its supported upgrades.";
            return true;
        }
        if (_gameplayHudLayout.OptionsTab.Bounds.Contains(point))
        {
            _gameplayHudTab = GameplayHudTab.Options;
            _showAlliesPanel = false;
            _status = "Game options tab.";
            return true;
        }
        if (point.X < 518 || point.X >= 638 || point.Y < 112 || point.Y >= 399) return false;
        if (_gameplayHudTab == GameplayHudTab.Options)
        {
            if (_showAlliesPanel)
            {
                var slots = AllianceSlots();
                if (slots[0].Contains(point))
                {
                    _showAlliesPanel = false;
                    _status = "Game options tab.";
                    return true;
                }
                var slot = Array.FindIndex(slots, bounds => bounds.Contains(point));
                var teams = AllianceTargetTeams();
                if (slot > 0 && slot - 1 < teams.Count && _scenarioSimulation is { } simulation)
                {
                    var otherTeam = teams[slot - 1];
                    var ally = simulation.TeamRelations.IsHostile(_localPlayerTeam, otherTeam);
                    // The original table is directional. A local UI alliance is
                    // intentionally reciprocal so combat behavior is clear in
                    // this single-player port before diplomacy is recovered.
                    simulation.TeamRelations.SetRelation(_localPlayerTeam, otherTeam, ally ? (byte)1 : (byte)0);
                    simulation.TeamRelations.SetRelation(otherTeam, _localPlayerTeam, ally ? (byte)1 : (byte)0);
                    _status = $"Team {_localPlayerTeam + 1} and team {otherTeam + 1} are now {(ally ? "allies" : "hostile")}.";
                }
                return true;
            }
            if (_gameplayHudLayout.Quit.Bounds.Contains(point))
            {
                ShowScreen(MenuScreenId.Main);
                return true;
            }
            if (_gameplayHudLayout.Pause.Bounds.Contains(point))
            {
                _gameplayPaused = !_gameplayPaused;
                _status = _gameplayPaused ? "Simulation paused." : "Simulation resumed.";
                return true;
            }
            if (_gameplayHudLayout.SaveGame.Bounds.Contains(point))
            {
                _status = "Save games have not been decoded yet.";
                return true;
            }
            if (_gameplayHudLayout.Allies.Bounds.Contains(point))
            {
                if (_scenarioSimulation is null || AllianceTargetTeams().Count == 0)
                {
                    _status = "No other enabled teams are available for an alliance.";
                    return true;
                }
                _showAlliesPanel = true;
                _status = "Allies: toggle a team to update the deterministic relation matrix.";
                return true;
            }
            if (_gameplayHudLayout.Objectives.Bounds.Contains(point))
            {
                if (_missionText is not { Briefing.Length: > 0 })
                {
                    _status = "This scenario has no recovered briefing text.";
                    return true;
                }
                _storyReturnsToGameplay = true;
                _gameplayPausedBeforeStory = _gameplayPaused;
                _gameplayPaused = true;
                _briefingScrollLine = 0;
                ShowScreen(MenuScreenId.Story);
                return true;
            }
            _status = "This original game-options control is mapped but its subsystem is not implemented yet.";
            return true;
        }
        var selectedEntities = SelectedGameplayEntities().ToArray();
        var deployedHarvesters = selectedEntities.Where(IsDeployedHarvester).ToArray();
        if (_gameplayHudTab == GameplayHudTab.Build && deployedHarvesters.Length != 0 && deployedHarvesters.Length == selectedEntities.Length)
        {
            if (_gameplayHudLayout.Contextual.Bounds.Contains(point))
                QueueHarvesterRetraction(deployedHarvesters);
            else
                _status = "Deployed harvester is attached; use its contextual control or Enter to retract.";
            return true;
        }
        var staticSteal = selectedEntities.Where(IsDeployedStealStance).ToArray();
        if (_gameplayHudTab == GameplayHudTab.Build && staticSteal.Length != 0 && staticSteal.Length == selectedEntities.Length)
        {
            if (_gameplayHudLayout.Contextual.Bounds.Contains(point))
                QueueStealToggle(staticSteal);
            else
                _status = "Stealing stance is static; use its contextual control or Enter to retract.";
            return true;
        }
        var deployedMines = selectedEntities.Where(IsDeployedMine).ToArray();
        if (_gameplayHudTab == GameplayHudTab.Build && deployedMines.Length != 0 && deployedMines.Length == selectedEntities.Length)
        {
            _status = "Deployed mines are armed and acquire nearby hostile units automatically.";
            return true;
        }
        var staticCombat = selectedEntities.Where(IsDeployedStaticCombatUnit).ToArray();
        if (_gameplayHudTab == GameplayHudTab.Build && staticCombat.Length != 0 && staticCombat.Length == selectedEntities.Length)
        {
            // The static-combat HUD deliberately has only Stop and Attack.
            // Route it before the normal mobile/build-catalog branches, which
            // otherwise see zero mobile actors and swallow the buttons.
            if (_gameplayHudLayout.Stop.Bounds.Contains(point))
            {
                StopSelectedUnits();
                return true;
            }
            if (_gameplayHudLayout.MoveAndAttack.Bounds.Contains(point))
            {
                SetGameplayCommandMode(GameplayCommandMode.AttackTarget);
                return true;
            }
            return true;
        }
        if (_gameplayHudTab == GameplayHudTab.Build && !SelectedGameplayEntities().Any())
        {
            HandleBuildCatalogPurchase(point);
            return true;
        }
        if (_gameplayHudTab == GameplayHudTab.Build && !SelectedGameplayActors().Any())
        {
            // Selection rendering uses the first deterministic structure as
            // the source too. A mixed static selection is therefore never
            // silently redirected to an unrelated global production anchor.
            var structure = SelectedGameplayEntities().FirstOrDefault(entity =>
                _scenarioSimulation?.Actor(entity.InstanceId) is { } actor &&
                actor.Definition.MovementSpeed <= 0);
            if (structure is not null) HandleProductionPurchase(point, structure);
            return true;
        }
        if (_gameplayHudTab == GameplayHudTab.Research)
        {
            var structure = SelectedGameplayEntities().FirstOrDefault(entity => _scenarioSimulation?.Actor(entity.InstanceId)?.Definition.MovementSpeed <= 0);
            if (structure is null)
            {
                _status = "Select one local research structure first.";
                return true;
            }
            var upgrade = ResearchButtonsForStructure(structure)
                .FirstOrDefault(button => CatalogBounds(button.Item.UiId, button.Position).Contains(point)).Item;
            if (upgrade is null) return true;
            var eligibility = PurchaseEligibilityFor(upgrade);
            if (eligibility != PurchaseEligibility.Available)
            {
                _status = $"{UpgradeLabel(upgrade)} unavailable: {eligibility}.";
                return true;
            }
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new PurchaseIntent(_localPlayerTeam, upgrade.Id));
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new ResearchIntent(_localPlayerTeam, upgrade.Id, structure.InstanceId));
            _status = $"{UpgradeLabel(upgrade)} ordered if P7 and all research requirements allow.";
            return true;
        }
        if (_gameplayHudTab != GameplayHudTab.Build || !SelectedGameplayActors().Any())
        {
            _status = _gameplayHudTab == GameplayHudTab.Build
                ? "Production requires the later pedestal and production engine."
                : "Upgrade behavior has not been recovered into the engine yet.";
            return true;
        }
        if (_gameplayHudLayout.Stop.Bounds.Contains(point))
        {
            if (!GameplayCommandState(SelectedGameplayEntities().ToArray()).AnyCanStop)
            {
                _status = "No selected actor can receive a Stop order.";
                return true;
            }
            StopSelectedUnits();
            return true;
        }
        if (_gameplayHudLayout.MoveOnly.Bounds.Contains(point))
        {
            if (!GameplayCommandState(SelectedGameplayEntities().ToArray()).AnyCanMove)
            {
                _status = "That deployed unit is static and cannot receive move orders.";
                return true;
            }
            SetGameplayCommandMode(GameplayCommandMode.MoveOnly);
            return true;
        }
        if (_gameplayHudLayout.MoveAndAttack.Bounds.Contains(point))
        {
            if (!GameplayCommandState(SelectedGameplayEntities().ToArray()).AnyCanAttack)
            {
                _status = "Select a unit with a resolved weapon before entering attack mode.";
                return true;
            }
            SetGameplayCommandMode(GameplayCommandMode.AttackTarget);
            return true;
        }
        if (_gameplayHudLayout.Waypoints.Bounds.Contains(point))
        {
            if (!GameplayCommandState(SelectedGameplayEntities().ToArray()).AnyCanUseWaypoints)
            {
                _status = "That deployed unit is static and cannot receive waypoint orders.";
                return true;
            }
            SetGameplayCommandMode(GameplayCommandMode.Waypoints);
            return true;
        }
        if (_gameplayHudLayout.Contextual.Bounds.Contains(point))
        {
            var selectedActors = SelectedGameplayEntities().ToArray();
            var contextual = SelectedCommonContextualCommand(selectedActors);
            if (contextual is null)
            {
                _status = SelectedUnitSpecial(selectedActors).PendingReason ??
                    "The selected units do not share one recovered contextual action.";
                return true;
            }
            if (contextual.Command == UnitSpecialCommand.HealUnits)
            {
                QueueAreaHeal(selectedActors);
                return true;
            }
            if (contextual.Command == UnitSpecialCommand.DeployTurret)
            {
                QueueTowerDeployment(selectedActors);
                return true;
            }
            if (contextual.Command == UnitSpecialCommand.StealMoney)
            {
                QueueStealToggle(selectedActors);
                return true;
            }
            if (contextual.Command == UnitSpecialCommand.DeployMine)
            {
                QueueMineDeployment(selectedActors);
                return true;
            }
            if (contextual.Command == UnitSpecialCommand.InspireTroops)
            {
                QueueInspire(selectedActors);
                return true;
            }
            if (contextual.Command == UnitSpecialCommand.HarvestPetra)
            {
                SetGameplayCommandMode(GameplayCommandMode.HarvestVent);
                return true;
            }
            _status = contextual.PendingReason;
            return true;
        }
        if (_gameplayHudLayout.Secondary.Bounds.Contains(point))
        {
            var secondary = SelectedUnitSecondary(SelectedGameplayEntities().ToArray());
            if (secondary is not null)
            {
                var requiredResearch = secondary.Value.RequiredResearchItemId;
                var researched = requiredResearch is null ||
                    _scenarioSimulation?.EconomyForTeam(_localPlayerTeam)?.CompletedItems.Contains(requiredResearch.Value) == true;
                if (!researched)
                    _status = $"{secondary.Value.Label} requires research item {requiredResearch}.";
                else if (secondary.Value.WeaponId is null)
                    _status = secondary.Value.PendingReason;
                else
                    SetGameplayCommandMode(GameplayCommandMode.GroundSpecialTarget);
                return true;
            }
        }
        return false;
    }
}
