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
using DarkColony.Engine.Missions;
using DarkColony.Engine.Movement;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Media;

namespace DarkColony.App;

/// <summary>Gameplay scenario loading, camera, and world drawing: terrain, actors, effects, and overlays.</summary>
public sealed partial class MainForm
{
    private void ReleaseTerrainGpuTiles()
    {
        foreach (var image in _terrainGpuTiles.Values) _surface.ReleaseGpuImage(image);
        _terrainGpuTiles.Clear();
    }

    private void DisposeGameplayMinimapPreview()
    {
        if (_minimapPreview is null) return;
        if (_gpuBitmaps.Remove(_minimapPreview, out var image)) _surface.ReleaseGpuImage(image);
        _minimapPreview.Dispose();
        _minimapPreview = null;
    }

    private void DrawGameplayTerrain(GameCanvas canvas)
    {
        if (_installation is null) return;
        try
        {
            if (_gameplayMap is null || _gameplayTileset is null || _gameplayPath is null || _scenarioWorld is null) return;

            var firstCellX = Math.Max(0, _cameraX / TerrainRasterizer.TileSize);
            var firstCellY = Math.Max(0, _cameraY / TerrainRasterizer.TileSize);
            var lastCellX = Math.Min(_gameplayMap.Width - 1, (_cameraX + 516 - 1) / TerrainRasterizer.TileSize);
            var lastCellY = Math.Min(_gameplayMap.Height - 1, (_cameraY + 458 - 1) / TerrainRasterizer.TileSize);
            for (var cellY = firstCellY; cellY <= lastCellY; cellY++)
            {
                for (var cellX = firstCellX; cellX <= lastCellX; cellX++)
                {
                    var cell = _gameplayMap[cellX, cellY];
                    DrawGameplayTerrainTile(canvas, cell.BaseTileId, cellX, cellY, cell.FlipBaseHorizontally, transparentZero: false);
                    if (cell.OverlayTileId != 0)
                        DrawGameplayTerrainTile(canvas, cell.OverlayTileId, cellX, cellY, cell.FlipOverlayHorizontally, transparentZero: true);
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Terrain error: {error.Message}";
        }
    }

    private void DrawGameplayTerrainTile(GameCanvas canvas, ushort tileId, int cellX, int cellY, bool flipHorizontally, bool transparentZero)
    {
        if (_gameplayTileset is null || !_gameplayTileset.TilesById.TryGetValue(tileId, out var tile)) return;
        var key = (tile.FrameId, flipHorizontally, transparentZero);
        if (!_terrainGpuTiles.TryGetValue(key, out var image))
        {
            var rgba = new byte[TerrainTile.Width * TerrainTile.Height * 4];
            for (var y = 0; y < TerrainTile.Height; y++)
            for (var x = 0; x < TerrainTile.Width; x++)
            {
                var sourceX = flipHorizontally ? TerrainTile.Width - 1 - x : x;
                var paletteIndex = tile.PaletteIndices[y * TerrainTile.Width + sourceX];
                var destination = (y * TerrainTile.Width + x) * 4;
                if (transparentZero && paletteIndex == 0) continue;
                var color = _gameplayTileset.Palette[paletteIndex];
                rgba[destination] = color.Red;
                rgba[destination + 1] = color.Green;
                rgba[destination + 2] = color.Blue;
                rgba[destination + 3] = 255;
            }
            image = new GpuImage(TerrainTile.Width, TerrainTile.Height, rgba);
            _terrainGpuTiles.Add(key, image);
        }
        canvas.Draw(image,
            cellX * TerrainRasterizer.TileSize - _cameraX,
            cellY * TerrainRasterizer.TileSize - _cameraY);
    }

    private void DrawGameplayMinimap(Graphics graphics)
    {
        if (_gameplayMap is null || _gameplayTileset is null) return;
        _minimapPreview ??= BuildGameplayMinimap(_gameplayMap, _gameplayTileset);
        var state = graphics.Save();
        graphics.SetClip(GameplayMinimapBounds);
        if (_activeCanvas is { } canvas)
            canvas.Draw(GpuBitmap(_minimapPreview), GameplayMinimapBounds.X, GameplayMinimapBounds.Y);
        else
            graphics.DrawImageUnscaled(_minimapPreview, GameplayMinimapBounds.Location);

        if (_scenarioSimulation is not null)
        {
            foreach (var actor in _scenarioSimulation.Actors.Where(actor => !actor.IsDestroyed)
                         .Where(actor => actor.Seed.Team == _localPlayerTeam || _scenarioSimulation.IsActorVisibleToTeam(_localPlayerTeam, actor)))
            {
                var position = RenderActorPosition(actor.Seed.InstanceId, actor.Movement.VisualPosition);
                var x = GameplayMinimapBounds.X + position.XRaw / 256d / _gameplayMap.Width * GameplayMinimapBounds.Width;
                var y = GameplayMinimapBounds.Bottom - 1 - position.ZRaw / 256d / _gameplayMap.Height * GameplayMinimapBounds.Height;
                var marker = actor.Seed.Team == _localPlayerTeam
                    ? Color.FromArgb(235, 85, 235, 240)
                    : Color.FromArgb(235, 240, 85, 70);
                if (_activeCanvas is { } minimapCanvas) minimapCanvas.FillForeground(new Rectangle((int)Math.Round(x) - 1, (int)Math.Round(y) - 1, 3, 3), marker);
                else { using var brush = new SolidBrush(marker); graphics.FillRectangle(brush, (int)Math.Round(x) - 1, (int)Math.Round(y) - 1, 3, 3); }
            }
        }

        var worldWidth = _gameplayMap.Width * TerrainRasterizer.TileSize;
        var worldHeight = _gameplayMap.Height * TerrainRasterizer.TileSize;
        var viewport = new Rectangle(
            GameplayMinimapBounds.X + (int)Math.Round(_cameraX / (double)worldWidth * GameplayMinimapBounds.Width),
            // The camera is in screen pixels, which run top-down like the minimap.
            GameplayMinimapBounds.Y + (int)Math.Round(_cameraY / (double)worldHeight * GameplayMinimapBounds.Height),
            Math.Max(1, (int)Math.Ceiling(516d / worldWidth * GameplayMinimapBounds.Width)),
            Math.Max(1, (int)Math.Ceiling(458d / worldHeight * GameplayMinimapBounds.Height)));
        var camera = Color.FromArgb(240, 225, 245, 210);
        if (_activeCanvas is { } foreground)
        {
            foreground.FillForeground(new Rectangle(viewport.X, viewport.Y, viewport.Width, 1), camera);
            foreground.FillForeground(new Rectangle(viewport.X, viewport.Bottom - 1, viewport.Width, 1), camera);
            foreground.FillForeground(new Rectangle(viewport.X, viewport.Y, 1, viewport.Height), camera);
            foreground.FillForeground(new Rectangle(viewport.Right - 1, viewport.Y, 1, viewport.Height), camera);
        }
        else { using var pen = new Pen(camera); graphics.DrawRectangle(pen, viewport.X, viewport.Y, viewport.Width, viewport.Height); }
        graphics.Restore(state);
    }

    private static Bitmap BuildGameplayMinimap(TerrainMap map, BtsTileset tileset)
    {
        var image = new Bitmap(GameplayMinimapBounds.Width, GameplayMinimapBounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            // Match the native handler's centred odd numerator. Its vertical
            // inversion maps world Z; MAP rows are already in screen order.
            var mapX = Math.Min(map.Width - 1, ((x * 2 + 1) * map.Width) / (image.Width * 2));
            var mapY = Math.Min(map.Height - 1, ((y * 2 + 1) * map.Height) / (image.Height * 2));
            var cell = map[mapX, mapY];
            var tileId = cell.OverlayTileId != 0 ? cell.OverlayTileId : cell.BaseTileId;
            if (!tileset.TilesById.TryGetValue(tileId, out var tile)) continue;
            var paletteIndex = tile.PaletteIndices[16 * TerrainTile.Width + 16];
            var color = tileset.Palette[paletteIndex];
            image.SetPixel(x, y, Color.FromArgb(color.Red, color.Green, color.Blue));
        }
        return image;
    }

    private ScenarioChoice GameplayScenario() => _selectedScenario ?? CampaignScenario(_campaignMission);

    /// <summary>Campaign missions are human01-15 / alien01-15; training is htrain1-7 / atrain1-7.</summary>
    private ScenarioChoice CampaignScenario(int mission) => _training
        ? new ScenarioChoice("test", $"{(_grayRace ? "atrain" : "htrain")}{mission}")
        : new ScenarioChoice(_grayRace ? "alien" : "human", $"{(_grayRace ? "alien" : "human")}{mission:00}");

    private bool LoadGameplayScenario()
    {
        if (_installation is null) return false;
        try
        {
            var scenario = GameplayScenario();
            var scenarioFile = _installation.DataFile("scenario", scenario.Directory, $"{scenario.Name}.scn");
            var definition = ScenarioDefinition.Load(scenarioFile);
            var triggerFile = Path.ChangeExtension(scenarioFile, ".tro");
            _scenarioTriggers = File.Exists(triggerFile) ? ScenarioTriggers.Load(triggerFile) : [];
            // Free-War SCN files hold shared Human commander placeholders.
            // Apply the selected faction/rank before either the world or the
            // deterministic simulation consumes placement entity IDs.
            if (scenario.WarLaunch is { } warLaunch) definition = warLaunch.ApplyTo(definition);
            _gameplayMap = TerrainMap.Load(_installation.DataFile("scenario", scenario.Directory, $"{scenario.Name}.map"));
            _gameplayTileset = BtsTileset.Load(_installation.DataFile("scenario", definition.Tileset));
            _gameplayPath = PathRegionMap.Load(
                _installation.DataFile("scenario", scenario.Directory, $"{scenario.Name}.pth"),
                _gameplayMap.Width,
                _gameplayMap.Height);
            // The engine checks build simulations from the same rule bundle,
            // so gameplay and the determinism suite cannot drift apart.
            var rules = _simulationRules ??= SimulationRules.Load(_installation);
            _buildingFootprints = rules.Footprints;
            _scenarioWorld = ScenarioWorld.Create(definition, rules.Footprints);
            _entityCatalog ??= rules.Entities;
            _weaponCatalog ??= rules.Weapons;
            _areaEffects ??= rules.AreaEffects;
            _dependencyCatalog ??= rules.Dependencies;
            var missionScript = MissionScript.LoadForScenario(_installation.DataFile("scenario", scenario.Directory, $"{scenario.Name}.scn"));
            _scenarioSimulation = ScenarioSimulation.Create(definition, _gameplayPath, rules, missionScript, _gameplayMap);
            _missionOutcomeReported = false;
            _previousActorRenderPositions.Clear();
            _groundOccupancy = _scenarioSimulation.GroundOccupancy;
            _alternateOccupancy = _scenarioSimulation.AlternateOccupancy;
            _autonomousEntities = _scenarioSimulation.Actors
                .Where(actor => actor.Seed.Team == AutonomousSpawnSeeder.InternalNeutralTeam)
                .Select(actor => actor.Seed).ToArray();
            // 0x41EBD8 centers the view on the local player's first %AISlots
            // point (x << 8, z << 8): the left edge of cell x, the bottom edge of
            // row z on screen. SCN files without %AISlots fall back to the
            // first local unit.
            var start = definition.Teams.FirstOrDefault(team => team.TeamId == _localPlayerTeam)?.StartPoint ??
                        _scenarioSimulation.Actors
                            .Where(actor => actor.Seed.Team == _localPlayerTeam && actor.Definition.MovementSpeed > 0)
                            .OrderBy(actor => actor.Seed.InstanceId)
                            .Select(actor => (CellCoordinate?)actor.Movement.OccupiedCell)
                            .FirstOrDefault();
            if (start is { } point)
            {
                _cameraX = point.X * 32 - 258;
                _cameraY = CellPixelTop(point.Z) + 32 - 229;
            }
            ClampGameplayCamera();
            _status = $"Loaded {scenario.Directory}\\{scenario.Name}: {_scenarioSimulation.Actors.Count} actors / {missionScript?.Triggers.Count ?? 0} triggers.";
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException)
        {
            _status = $"Could not load map: {error.Message}";
            return false;
        }
    }

    private void DrawGameplayPathRegions(Graphics graphics)
    {
        if (!_showPathRegions || _gameplayPath is null) return;
        var canvas = _activeCanvas;
        var state = canvas is null ? graphics.Save() : null;
        if (canvas is null) graphics.SetClip(new Rectangle(0, 0, 516, 458));
        using var zero = canvas is null ? new SolidBrush(Color.FromArgb(65, 220, 35, 35)) : null;
        using var boundary = canvas is null ? new Pen(Color.FromArgb(150, 40, 220, 230)) : null;
        var firstX = Math.Max(0, _cameraX / 32);
        var firstRow = Math.Max(0, _cameraY / 32);
        var lastX = Math.Min(_gameplayPath.Width - 1, (_cameraX + 515) / 32);
        var lastRow = Math.Min(_gameplayPath.Height - 1, (_cameraY + 457) / 32);
        for (var row = firstRow; row <= lastRow; row++)
        for (var x = firstX; x <= lastX; x++)
        {
            var z = _gameplayPath.Height - 1 - row;
            var cell = new CellCoordinate(x, z);
            var region = _gameplayPath.RegionAt(cell);
            var screenX = x * 32 - _cameraX;
            var screenY = row * 32 - _cameraY;
            if (region == 0)
            {
                if (canvas is not null) canvas.Fill(new Rectangle(screenX, screenY, 32, 32), Color.FromArgb(65, 220, 35, 35));
                else graphics.FillRectangle(zero!, screenX, screenY, 32, 32);
            }
            if (x > 0 && _gameplayPath.RegionAt(new CellCoordinate(x - 1, z)) != region)
            {
                if (canvas is not null) canvas.Fill(new Rectangle(screenX, screenY, 1, 32), Color.FromArgb(150, 40, 220, 230));
                else graphics.DrawLine(boundary!, screenX, screenY, screenX, screenY + 32);
            }
            // The cell drawn above this one is world row z + 1.
            if (z + 1 < _gameplayPath.Height && _gameplayPath.RegionAt(new CellCoordinate(x, z + 1)) != region)
            {
                if (canvas is not null) canvas.Fill(new Rectangle(screenX, screenY, 32, 1), Color.FromArgb(150, 40, 220, 230));
                else graphics.DrawLine(boundary!, screenX, screenY, screenX + 32, screenY);
            }
        }
        if (state is not null) graphics.Restore(state);
    }

    private void DrawGameplayVents(Graphics graphics)
    {
        if (_scenarioSimulation is null) return;
        if (_activeCanvas is { } canvas)
        {
            foreach (var vent in _scenarioSimulation.PetraVents)
            {
                var x = vent.Position.X * 32 - _cameraX;
                var y = CellPixelTop(vent.Position.Z) - _cameraY;
                canvas.Ellipse(new Rectangle(x + 7, y + 7, 18, 18),
                    vent.HarvesterInstanceId is null ? Color.FromArgb(220, 230, 185, 40) : Color.FromArgb(220, 65, 230, 110),
                    thickness: 2, foreground: true);
                DrawMenuText(graphics, vent.HarvesterInstanceId is null ? "P7" : "P7+", new Rectangle(x + 6, y - 1, 24, 14), center: false, remap: Color.FromArgb(220, 230, 185, 40));
            }
            return;
        }
        var state = graphics.Save();
        graphics.SetClip(new Rectangle(0, 0, 516, 458));
        using var unclaimed = new Pen(Color.FromArgb(220, 230, 185, 40), 2);
        using var claimed = new Pen(Color.FromArgb(220, 65, 230, 110), 2);
        using var text = new SolidBrush(Color.FromArgb(220, 230, 185, 40));
        foreach (var vent in _scenarioSimulation.PetraVents)
        {
            var x = vent.Position.X * 32 - _cameraX;
            var y = CellPixelTop(vent.Position.Z) - _cameraY;
            graphics.DrawEllipse(vent.HarvesterInstanceId is null ? unclaimed : claimed, x + 7, y + 7, 18, 18);
            DrawMenuText(graphics, vent.HarvesterInstanceId is null ? "P7" : "P7+", new Rectangle(x + 6, y - 1, 24, 14), center: false, remap: Color.FromArgb(220, 230, 185, 40));
        }
        graphics.Restore(state);
    }

    private void DrawGameplayFogOfWar(Graphics graphics)
    {
        if (_scenarioSimulation is null || _gameplayMap is null) return;
        // The original leaves map space no stamp has reached black (grid bit 31,
        // explored memory). Hostile units outside current sight are hidden by
        // the actor pass, not here.
        var state = _activeCanvas is null ? graphics.Save() : null;
        if (_activeCanvas is null) graphics.SetClip(new Rectangle(0, 0, 516, 458));
        using var unseen = _activeCanvas is null ? new SolidBrush(Color.Black) : null;
        var firstX = Math.Max(0, _cameraX / 32);
        var firstRow = Math.Max(0, _cameraY / 32);
        var lastX = Math.Min(_gameplayMap.Width - 1, (_cameraX + 515) / 32);
        var lastRow = Math.Min(_gameplayMap.Height - 1, (_cameraY + 457) / 32);
        for (var row = firstRow; row <= lastRow; row++)
        for (var x = firstX; x <= lastX; x++)
        {
            var z = _gameplayMap.Height - 1 - row;
            if (_scenarioSimulation.IsCellExploredByTeam(_localPlayerTeam, new CellCoordinate(x, z))) continue;
            var bounds = new Rectangle(x * 32 - _cameraX, row * 32 - _cameraY, 32, 32);
            if (_activeCanvas is { } canvas) canvas.Fill(bounds, Color.Black);
            else graphics.FillRectangle(unseen!, bounds);
        }
        if (state is not null) graphics.Restore(state);
    }

    private void DrawBuildingPlacementPreview(Graphics graphics)
    {
        if (_gameplayCommandMode != GameplayCommandMode.PlaceBuilding ||
            !TryGetBuildingPlacementPreview(out var item, out var entityId, out var cells, out var valid)) return;

        // This is intentionally a port-side placement aid.  The collision
        // cells come from dc.exe's footprint table, so its green/red result is
        // identical to the engine's immediate placement preflight; the native
        // transport/drop animation itself has not been recovered yet.
        var canvas = _activeCanvas;
        var state = canvas is null ? graphics.Save() : null;
        if (canvas is null) graphics.SetClip(new Rectangle(0, 0, 516, 458));
        var fillColor = valid ? Color.FromArgb(70, 65, 230, 105) : Color.FromArgb(80, 235, 65, 50);
        var borderColor = valid ? Color.FromArgb(235, 80, 245, 120) : Color.FromArgb(235, 250, 80, 55);
        using var fill = canvas is null ? new SolidBrush(fillColor) : null;
        using var border = canvas is null ? new Pen(borderColor, 2) : null;
        foreach (var cell in cells)
        {
            var bounds = new Rectangle(cell.X * 32 - _cameraX + 1, CellPixelTop(cell.Z) - _cameraY + 1, 30, 30);
            if (canvas is not null)
            {
                canvas.Fill(bounds, fillColor);
                canvas.Fill(new Rectangle(bounds.X, bounds.Y, bounds.Width, 2), borderColor);
                canvas.Fill(new Rectangle(bounds.X, bounds.Bottom - 2, bounds.Width, 2), borderColor);
                canvas.Fill(new Rectangle(bounds.X, bounds.Y, 2, bounds.Height), borderColor);
                canvas.Fill(new Rectangle(bounds.Right - 2, bounds.Y, 2, bounds.Height), borderColor);
            }
            else
            {
                graphics.FillRectangle(fill!, bounds);
                graphics.DrawRectangle(border!, bounds);
            }
        }
        if (state is not null) graphics.Restore(state);

        var label = $"{BuildingLabel(item)} #{entityId}: {(valid ? "CLEAR DROP" : "BLOCKED")}";
        DrawGameplayHudText(graphics, label, new Rectangle(8, 404, 500, 14));
    }

    private bool TryGetBuildingPlacementPreview(
        out DependencyDefinition item,
        out int entityId,
        out IReadOnlyList<CellCoordinate> cells,
        out bool valid)
    {
        item = null!;
        entityId = 0;
        cells = [];
        valid = false;
        if (_pendingBuildingItemId is not { } itemId || _gameplayPointer is not { } pointer ||
            pointer.X is < 0 or >= 516 || pointer.Y is < 0 or >= 458 ||
            _dependencyCatalog?.TryGet(itemId, out item) != true || _buildingFootprints is null ||
            !_buildingFootprints.TryResolveBuildingEntity(item.BuildingFaction!.Value, item.BuildingVariant!.Value, item.BuildingSlot!.Value, out entityId))
            return false;

        var origin = CellAtPixel(pointer.X + _cameraX, pointer.Y + _cameraY);
        cells = _buildingFootprints.OccupiedCells(entityId, origin);
        if (cells.Count == 0) return false;
        valid = _gameplayMap is not null && _groundOccupancy is not null &&
            cells.All(cell => (uint)cell.X < (uint)_gameplayMap.Width && (uint)cell.Z < (uint)_gameplayMap.Height && !_groundOccupancy.IsOccupied(cell));
        return true;
    }

    private void MoveGameplayCamera(int x, int y)
    {
        SetGameplayCamera(_cameraX + x, _cameraY + y);
    }

    private void ClampGameplayCamera()
    {
        if (_gameplayMap is null) return;
        _cameraX = Math.Clamp(_cameraX, 0, Math.Max(0, _gameplayMap.Width * 32 - 516));
        _cameraY = Math.Clamp(_cameraY, 0, Math.Max(0, _gameplayMap.Height * 32 - 458));
    }

    /// <summary>
    /// Projects the actor's current authoritative form/facing/action into the
    /// exact cached FIN frame used by drawing, click hit-testing, and box
    /// selection. The returned rectangles are screen-space viewport geometry.
    /// </summary>
    private bool TryGameplayActorVisual(WorldEntity entity, out GameplayActorVisual visual)
    {
        visual = null!;
        if (_scenarioSimulation is null || _entityCatalog is null || _entityAnimations is null ||
            _scenarioSimulation.Actor(entity.InstanceId) is not { } actorState) return false;
        var renderEntityId = actorState.DeployedEntityId ?? entity.EntityId;
        if ((uint)renderEntityId >= (uint)_entityCatalog.Entities.Count) return false;
        var deploymentPresentation = ActiveFormDeploymentPresentation(entity, actorState);
        var combatPresentation = deploymentPresentation ?? ActiveCombatPresentation(entity, actorState);
        var moveSelection = combatPresentation is null && actorState.Playback is not null && actorState.DeployedEntityId is null
            ? _entityAnimations.PreferredMove(renderEntityId, actorState.Facing.RenderSector16)
            : null;
        var candidate = combatPresentation?.Candidate ?? moveSelection?.Candidate ?? _entityAnimations.Preferred(renderEntityId);
        if (candidate is null) return false;
        var span = candidate.LastFrame - candidate.FirstFrame + 1;
        var frameAge = combatPresentation is null
            ? (_world.TickCount - _screenStartedAtTick) / 3
            : _world.TickCount - combatPresentation.StartedAtTick;
        var frame = (ushort)(candidate.FirstFrame + frameAge % (ulong)span);
        var fileName = Path.GetFileName(candidate.FinPath);
        var bitmap = AnimationBitmap(fileName, frame);
        if (bitmap is null) return false;
        var key = $"{fileName}:{frame}";
        var origin = AnimationOrigin(fileName, frame);
        var position = ActorPosition(entity);
        var canvas = new Rectangle(
            position.XRaw / 8 - _cameraX + origin.X,
            WorldPixelY(position.ZRaw) - _cameraY + origin.Y,
            bitmap.Width,
            bitmap.Height);
        var localOpaque = AnimationOpaqueBounds(key, bitmap);
        var opaque = new Rectangle(
            canvas.X + localOpaque.X,
            canvas.Y + localOpaque.Y,
            localOpaque.Width,
            localOpaque.Height);
        visual = new GameplayActorVisual(actorState, renderEntityId, candidate, combatPresentation,
            moveSelection, fileName, frame, bitmap, canvas, opaque);
        return true;
    }

    private void DrawGameplayActors(Graphics graphics, GameCanvas canvas)
    {
        if (_installation is null || _scenarioSimulation is null) return;
        try
        {
            _entityCatalog ??= EntityCatalog.Load(_installation.DataFile("gamestat", "gamestat.txt"));
            _entityAnimations ??= EntityAnimationCatalog.Build(_entityCatalog, _installation.DataFile("animate"));
            _weaponEffects ??= _weaponCatalog is null ? null : WeaponEffectCatalog.Build(_weaponCatalog, _installation.DataFile("animate"));
            var state = graphics.Save();
            graphics.SetClip(new Rectangle(0, 0, 516, 458));
            var targetedInstanceIds = _scenarioSimulation.Actors
                .Where(actor => !actor.IsDestroyed && actor.AttackTargetInstanceId is not null)
                .Select(actor => actor.AttackTargetInstanceId!.Value)
                .ToHashSet();
            // Painter's order: higher on screen first, which is larger world Z.
            foreach (var entity in GameplayEntities().OrderByDescending(entity => ActorPosition(entity).ZRaw).ThenBy(entity => ActorPosition(entity).XRaw))
            {
                if (!TryGameplayActorVisual(entity, out var visual)) continue;
                var actorState = visual.Actor;
                var renderEntityId = visual.RenderEntityId;
                var candidate = visual.Candidate;
                var combatPresentation = visual.CombatPresentation;
                var moveSelection = visual.MoveSelection;
                var fileName = visual.FileName;
                var bitmap = visual.Bitmap;
                var position = ActorPosition(entity);
                var worldX = position.XRaw / 8;
                var worldY = WorldPixelY(position.ZRaw);
                var opaque = visual.OpaqueBounds;
                var centerX = opaque.Left + opaque.Width / 2;
                if (_selectedEntityInstanceIds.Contains(entity.InstanceId))
                {
                    // A FIN logical origin is not consistently the visible
                    // feet of its composed sprite. Anchor the provisional
                    // ground indicator to the frame's opaque visual base so
                    // it stays with the unit instead of its abstract cell.
                    var groundY = opaque.Bottom;
                    canvas.Ellipse(new Rectangle(centerX - 25, groundY - 12, 50, 20), Color.FromArgb(72, 255, 255), thickness: 2, foreground: true);
                }
                canvas.Draw(GpuBitmap(bitmap), visual.CanvasBounds);

                // Status indicators are foreground UI. Draw them after the
                // FIN composite and bind target geometry to the visible pixels,
                // not to transparent canvas margins whose origins vary by frame.
                if (targetedInstanceIds.Contains(entity.InstanceId))
                {
                    var targetedBounds = new Rectangle(opaque.Left - 2, opaque.Top - 2, opaque.Width + 3, opaque.Height + 3);
                    var targetedColor = Color.FromArgb(220, 255, 80, 55);
                    canvas.FillForeground(new Rectangle(targetedBounds.X, targetedBounds.Y, targetedBounds.Width, 2), targetedColor);
                    canvas.FillForeground(new Rectangle(targetedBounds.X, targetedBounds.Bottom - 2, targetedBounds.Width, 2), targetedColor);
                    canvas.FillForeground(new Rectangle(targetedBounds.X, targetedBounds.Y, 2, targetedBounds.Height), targetedColor);
                    canvas.FillForeground(new Rectangle(targetedBounds.Right - 2, targetedBounds.Y, 2, targetedBounds.Height), targetedColor);
                }
                if (actorState is not null && (_selectedEntityInstanceIds.Contains(entity.InstanceId) || actorState.Health < actorState.MaximumHealth))
                    DrawActorHealthBar(graphics, canvas, actorState, centerX, opaque.Top - 5);
                if (actorState?.InspirationTicksRemaining > 0)
                {
                    canvas.Ellipse(new Rectangle(centerX - 3, opaque.Top - 13, 7, 7), Color.FromArgb(235, 255, 214, 72), filled: true, foreground: true);
                }

                if (_showAssetNames)
                {
                    var definition = _entityCatalog[renderEntityId];
                    var animationState = combatPresentation is not null
                        ? $" · {candidate.AnimationName} [action]"
                        : moveSelection is not null
                            ? $" · {candidate.AnimationName}{(moveSelection.ExactSector ? "" : $"~s{moveSelection.RequestedSector}")}"
                            : $" · {candidate.AnimationName}";
                    var ownership = entity.Team == _localPlayerTeam ? "local" : "remote";
                    var identity = renderEntityId == entity.EntityId ? $"#{entity.EntityId}" : $"#{entity.EntityId}→#{renderEntityId}";
                    var special = actorState is not null && UnitSpecialCommandCatalog.TryGet(actorState.Definition, out var command)
                        ? $" · action {command.Label}"
                        : "";
                    var label = $"{identity} {definition.Code} · team {entity.Team} / faction {definition.Faction} {ownership}{special} · {fileName}{animationState}";
                    var labelX = worldX + 5 - _cameraX;
                    var labelY = worldY - 12 - _cameraY;
                    canvas.FillForeground(new Rectangle(labelX, labelY, 500, 14), Color.FromArgb(190, 0, 0, 0));
                    DrawMenuText(graphics, label, new Rectangle(labelX, labelY, 500, 14), center: false, remap: Color.FromArgb(245, 241, 200));
                }
            }
            DrawGameplayImpactEffects(graphics, canvas);
            DrawGameplayTransportEffects(graphics, canvas);
            DrawGameplayDeathEffects(graphics, canvas);
            foreach (var projectile in _scenarioSimulation.Projectiles)
            {
                var x = projectile.Position.XRaw / 8 - _cameraX;
                var y = WorldPixelY(projectile.Position.ZRaw) - projectile.HeightRaw / 8 - _cameraY;
                var candidate = _weaponEffects?.Bullet(projectile.WeaponId);
                var rendered = false;
                if (candidate is not null)
                {
                    var fileName = Path.GetFileName(candidate.FinPath);
                    var span = candidate.LastFrame - candidate.FirstFrame + 1;
                    var frame = candidate.FirstFrame + (ushort)(projectile.AnimationTicks % span);
                    var bitmap = AnimationBitmap(fileName, frame);
                    if (bitmap is not null)
                    {
                        var origin = AnimationOrigin(fileName, frame);
                        canvas.Draw(GpuBitmap(bitmap), x + origin.X, y + origin.Y);
                        rendered = true;
                    }
                }
                if (!rendered)
                {
                    canvas.Ellipse(new Rectangle(x - 4, y - 4, 8, 8), Color.FromArgb(235, 255, 225, 95), filled: true);
                    canvas.Ellipse(new Rectangle(x - 1, y - 1, 3, 3), Color.FromArgb(255, 255, 255, 215), filled: true);
                }
                if (_showAssetNames)
                {
                    var label = $"W{projectile.WeaponId} M{projectile.ProjectileMode} H{projectile.HeightRaw} U{projectile.ElapsedTicks}";
                    canvas.FillForeground(new Rectangle(x + 5, y - 10, 160, 14), Color.FromArgb(190, 0, 0, 0));
                    DrawMenuText(graphics, label, new Rectangle(x + 5, y - 10, 160, 14), center: false, remap: Color.FromArgb(245, 241, 200));
                }
            }
            DrawSelectedWaypointQueue(graphics, canvas);
            if (_diagnosticMoveTarget is { } target)
            {
                if (_diagnosticPathCells.Count > 1)
                {
                    var points = _diagnosticPathCells.Select(cell => new Point(
                        cell.X * 32 + 16 - _cameraX,
                        CellPixelTop(cell.Z) + 16 - _cameraY)).ToArray();
                    for (var index = 1; index < points.Length; index++)
                        canvas.Line(points[index - 1], points[index], Color.FromArgb(225, 255, 205, 55), thickness: 2, foreground: true);
                }
                var x = target.X * 32 - _cameraX;
                var y = CellPixelTop(target.Z) - _cameraY;
                var marker = Color.FromArgb(80, 255, 255);
                canvas.FillForeground(new Rectangle(x + 3, y + 3, 25, 2), marker);
                canvas.FillForeground(new Rectangle(x + 3, y + 26, 25, 2), marker);
                canvas.FillForeground(new Rectangle(x + 3, y + 3, 2, 25), marker);
                canvas.FillForeground(new Rectangle(x + 26, y + 3, 2, 25), marker);
                canvas.Line(new Point(x + 8, y + 16), new Point(x + 23, y + 16), marker, thickness: 2, foreground: true);
                canvas.Line(new Point(x + 16, y + 8), new Point(x + 16, y + 23), marker, thickness: 2, foreground: true);
            }
            if (_selectionDragStart is { } selectionStart && IsSelectionBoxGesture(selectionStart, _selectionDragCurrent, _selectionGestureStartedAtTick))
            {
                var bounds = GameplaySelectionBounds(selectionStart, _selectionDragCurrent);
                canvas.FillForeground(bounds, Color.FromArgb(35, 80, 235, 220));
                var selectionBorder = Color.FromArgb(210, 110, 255, 235);
                canvas.FillForeground(new Rectangle(bounds.X, bounds.Y, bounds.Width, 1), selectionBorder);
                canvas.FillForeground(new Rectangle(bounds.X, bounds.Bottom - 1, bounds.Width, 1), selectionBorder);
                canvas.FillForeground(new Rectangle(bounds.X, bounds.Y, 1, bounds.Height), selectionBorder);
                canvas.FillForeground(new Rectangle(bounds.Right - 1, bounds.Y, 1, bounds.Height), selectionBorder);
            }
            graphics.Restore(state);
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Scenario actor error: {error.Message}";
        }
    }

    private static void DrawActorHealthBar(Graphics graphics, GameCanvas canvas, SimulatedActor actor, int centerX, int y)
    {
        if (actor.MaximumHealth <= 0) return;
        const int width = 30;
        const int height = 4;
        var ratio = Math.Clamp((float)actor.Health / actor.MaximumHealth, 0f, 1f);
        var left = centerX - width / 2;
        var background = Color.FromArgb(215, 16, 12, 12);
        var foreground = ratio > .5f ? Color.FromArgb(220, 78, 228, 87) : Color.FromArgb(220, 240, 173, 48);
        var border = Color.FromArgb(230, 5, 5, 5);
        canvas.FillForeground(new Rectangle(left, y, width, height), background);
        canvas.FillForeground(new Rectangle(left, y, Math.Max(1, (int)(width * ratio)), height), foreground);
        canvas.FillForeground(new Rectangle(left, y, width, 1), border);
        canvas.FillForeground(new Rectangle(left, y + height - 1, width, 1), border);
        canvas.FillForeground(new Rectangle(left, y, 1, height), border);
        canvas.FillForeground(new Rectangle(left + width - 1, y, 1, height), border);
    }

    private void DrawSelectedWaypointQueue(Graphics graphics, GameCanvas canvas)
    {
        if (_scenarioSimulation is null) return;
        var lead = SelectedGameplayEntities().FirstOrDefault();
        if (lead is null || _scenarioSimulation.Actor(lead.InstanceId)?.MoveOrder is not { } order) return;
        var destinations = new[] { order.Target }.Concat(order.PendingWaypoints).ToArray();
        if (destinations.Length == 0) return;

        var points = new List<Point>(destinations.Length + 1)
        {
            new(ActorPosition(lead).XRaw / 8 - _cameraX, WorldPixelY(ActorPosition(lead).ZRaw) - _cameraY),
        };
        points.AddRange(destinations.Select(cell => new Point(
            cell.X * 32 + 16 - _cameraX,
            CellPixelTop(cell.Z) + 16 - _cameraY)));
        for (var index = 1; index < points.Count; index++)
            canvas.Line(points[index - 1], points[index], Color.FromArgb(190, 92, 228, 255), foreground: true);
        for (var index = 0; index < destinations.Length; index++)
        {
            var point = points[index + 1];
            canvas.Ellipse(new Rectangle(point.X - 7, point.Y - 7, 14, 14), Color.FromArgb(150, 10, 35, 48), filled: true, foreground: true);
            canvas.Ellipse(new Rectangle(point.X - 7, point.Y - 7, 14, 14), Color.FromArgb(245, 130, 245, 255), thickness: 2, foreground: true);
            DrawMenuText(graphics, (index + 1).ToString(), new Rectangle(point.X - 3, point.Y - 5, 10, 14), center: false, remap: Color.FromArgb(240, 235, 255, 255));
        }
    }

    private void DrawGameplayImpactEffects(Graphics graphics, GameCanvas canvas)
    {
        if (_weaponEffects is null) return;
        for (var index = _impactEffects.Count - 1; index >= 0; index--)
        {
            var effect = _impactEffects[index];
            var candidate = _weaponEffects.Impact(effect.WeaponId);
            if (candidate is null)
            {
                _impactEffects.RemoveAt(index);
                continue;
            }
            var span = candidate.LastFrame - candidate.FirstFrame + 1;
            var age = _world.TickCount - effect.StartedAtTick;
            if (age >= (ulong)span)
            {
                _impactEffects.RemoveAt(index);
                continue;
            }
            var frame = candidate.FirstFrame + (ushort)age;
            var fileName = Path.GetFileName(candidate.FinPath);
            var bitmap = AnimationBitmap(fileName, frame);
            if (bitmap is null) continue;
            var origin = AnimationOrigin(fileName, frame);
            canvas.Draw(GpuBitmap(bitmap), effect.Position.XRaw / 8 - _cameraX + origin.X, WorldPixelY(effect.Position.ZRaw) - _cameraY + origin.Y);
        }
    }

    private void DrawGameplayTransportEffects(Graphics graphics, GameCanvas canvas)
    {
        if (_entityAnimations is null || _scenarioSimulation is null) return;
        foreach (var transport in _scenarioSimulation.BattlefieldTransports.OrderBy(item => item.InstanceId))
        {
            var move = transport.IsPursuing
                ? _entityAnimations.PreferredMove(transport.TransportEntityId, transport.Facing.RenderSector16)
                : null;
            var candidate = move?.Candidate ?? _entityAnimations.Preferred(transport.TransportEntityId);
            if (candidate is null) continue;
            var span = candidate.LastFrame - candidate.FirstFrame + 1;
            var frame = candidate.FirstFrame + (ushort)((_world.TickCount / 3) % (ulong)span);
            var fileName = Path.GetFileName(candidate.FinPath);
            var bitmap = AnimationBitmap(fileName, frame);
            if (bitmap is null) continue;
            var origin = AnimationOrigin(fileName, frame);
            canvas.Draw(GpuBitmap(bitmap),
                transport.Position.XRaw / 8 - _cameraX + origin.X,
                WorldPixelY(transport.Position.ZRaw) - transport.HeightRaw / 8 - _cameraY + origin.Y);
            if (!_showAssetNames) continue;
            using var font = new Font(FontFamily.GenericMonospace, 8, FontStyle.Regular, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.FromArgb(245, 241, 200));
            var pursuit = transport.IsPursuing
                ? $" {(transport.IsTurning ? $"TURN:{transport.Facing.Current}→{transport.Facing.Target}" : $"MOVE:{transport.HorizontalExecutionsRemaining}")}→({transport.PursuitTarget!.Value.Cell.X},{transport.PursuitTarget.Value.Cell.Z})"
                : string.Empty;
            graphics.DrawString($"transport #{transport.TransportEntityId}/{transport.InstanceId} · {transport.Phase}{pursuit} H{transport.HeightRaw} · {fileName}:{candidate.AnimationName}", font, text,
                transport.Position.XRaw / 8 - _cameraX + 8,
                WorldPixelY(transport.Position.ZRaw) - transport.HeightRaw / 8 - _cameraY - 20);
        }
    }

    private void DrawGameplayDeathEffects(Graphics graphics, GameCanvas canvas)
    {
        if (_entityAnimations is null) return;
        for (var index = _deathEffects.Count - 1; index >= 0; index--)
        {
            var effect = _deathEffects[index];
            var candidate = _entityAnimations.PreferredDeath(effect.EntityId);
            if (candidate is null)
            {
                _deathEffects.RemoveAt(index);
                continue;
            }
            var span = candidate.LastFrame - candidate.FirstFrame + 1;
            var age = (_world.TickCount - effect.StartedAtTick) / 3;
            if (age >= (ulong)span)
            {
                _deathEffects.RemoveAt(index);
                continue;
            }
            var frame = candidate.FirstFrame + (ushort)age;
            var fileName = Path.GetFileName(candidate.FinPath);
            var bitmap = AnimationBitmap(fileName, frame);
            if (bitmap is null) continue;
            var origin = AnimationOrigin(fileName, frame);
            canvas.Draw(GpuBitmap(bitmap), effect.Position.XRaw / 8 - _cameraX + origin.X, WorldPixelY(effect.Position.ZRaw) - _cameraY + origin.Y);
        }
    }

    private CombatPresentation? ActiveCombatPresentation(WorldEntity entity, SimulatedActor actor)
    {
        if (_entityAnimations is null) return null;
        var renderEntityId = actor.DeployedEntityId ?? entity.EntityId;
        // A projectile impact is more immediate feedback than a firing event
        // when both occur in one tick.
        if (TryPresentation(_hitActorStartedAt, _entityAnimations.PreferredHit(renderEntityId, actor.Facing.RenderSector16), entity.InstanceId, out var hit))
            return hit;
        var variantRoll = _firingActorVariantRoll.GetValueOrDefault(entity.InstanceId);
        return TryPresentation(_firingActorStartedAt, _entityAnimations.PreferredFire(renderEntityId, actor.Facing.RenderSector16, variantRoll), entity.InstanceId, out var fire)
            ? fire : null;
    }

    /// <summary>Transient presentation for any decoded mobile-to-static form transition.</summary>
    private CombatPresentation? ActiveFormDeploymentPresentation(WorldEntity entity, SimulatedActor actor)
    {
        if (_entityAnimations is null) return null;
        if (TryPresentation(_formRetractionStartedAt,
                _entityAnimations.PreferredRetract(entity.EntityId, actor.Facing.RenderSector16), entity.InstanceId, out var retract))
            return retract;
        return TryPresentation(_formDeploymentStartedAt,
            _entityAnimations.PreferredDeploy(entity.EntityId, actor.Facing.RenderSector16), entity.InstanceId, out var deploy)
            ? deploy : null;
    }

    private bool TryPresentation(
        IDictionary<int, ulong> starts,
        DirectionalAnimationSelection? selection,
        int entityInstanceId,
        out CombatPresentation presentation)
    {
        presentation = null!;
        if (!starts.TryGetValue(entityInstanceId, out var startedAtTick)) return false;
        if (selection is null)
        {
            starts.Remove(entityInstanceId);
            return false;
        }
        var span = selection.Candidate.LastFrame - selection.Candidate.FirstFrame + 1;
        if (_world.TickCount - startedAtTick >= (ulong)span)
        {
            starts.Remove(entityInstanceId);
            return false;
        }
        presentation = new CombatPresentation(selection.Candidate, startedAtTick);
        return true;
    }

    private void DrawGameplayCursor(Graphics graphics)
    {
        if (_gameplayPointer is not { } pointer) return;
        var animation = Animation("curs.fin", GameplayCursorAnimation(pointer));
        if (animation is null) return;
        var span = animation.LastFrame - animation.FirstFrame + 1;
        var frame = animation.FirstFrame + (ushort)(((_world.TickCount - _screenStartedAtTick) / 3) % (ulong)span);
        var bitmap = AnimationBitmap("curs.fin", frame);
        if (bitmap is null) return;
        // Cursor FIN layers have their own negative hotspot offsets. Unlike a
        // UI gadget rectangle, the pointer itself is that logical origin.
        var origin = AnimationOrigin("curs.fin", frame);
        if (_activeCanvas is { } canvas)
            canvas.DrawForeground(GpuBitmap(bitmap), pointer.X + origin.X, pointer.Y + origin.Y);
        else
            graphics.DrawImageUnscaled(bitmap, pointer.X + origin.X, pointer.Y + origin.Y);
    }

    private string GameplayCursorAnimation(Point pointer)
    {
        if (_selectionDragStart is { } selectionStart && IsSelectionBoxGesture(selectionStart, _selectionDragCurrent, _selectionGestureStartedAtTick))
            return "DRAWBOX";
            if (pointer.X is >= 0 and < 516 && pointer.Y is >= 0 and < 458)
            {
                var edge = GameplayEdgeCursorAnimation(pointer);
                if (edge is not null) return edge;
                if (_gameplayCommandMode is GameplayCommandMode.AttackTarget or GameplayCommandMode.GroundSpecialTarget) return "ATTACK";
                if (_gameplayCommandMode is GameplayCommandMode.Waypoints or GameplayCommandMode.HarvestVent or GameplayCommandMode.PlaceBuilding)
                return "MOVE";
            if (FindGameplayActorAt(pointer, locallyControllableOnly: true) is not null) return "UNITSELECT";
        }
        return "DEFAULT";
    }

    private static string? GameplayEdgeCursorAnimation(Point pointer)
    {
        var up = pointer.Y <= 8;
        var down = pointer.Y >= 449;
        var left = pointer.X <= 8;
        var right = pointer.X >= 507;
        if (up && left) return "PUSHUP&LEFT";
        if (up && right) return "PUSHUP&RIGHT";
        if (down && left) return "DOWN&LEFT";
        if (down && right) return "DOWN&RIGHT";
        if (up) return "PUSHUP";
        if (down) return "PUSHDOWN";
        if (left) return "PUSHLEFT";
        return right ? "PUSHRIGHT" : null;
    }

    private void CapturePreviousActorRenderPositions()
    {
        if (_scenarioSimulation is null)
        {
            _previousActorRenderPositions.Clear();
            return;
        }

        foreach (var actor in _scenarioSimulation.Actors)
        {
            if (!actor.IsDestroyed)
                _previousActorRenderPositions[actor.Seed.InstanceId] = actor.Movement.VisualPosition;
        }
    }

    private FixedPointPosition ActorPosition(WorldEntity entity)
    {
        var current = _scenarioSimulation?.Actor(entity.InstanceId)?.Movement.VisualPosition ?? entity.Position;
        return RenderActorPosition(entity.InstanceId, current);
    }

    private FixedPointPosition RenderActorPosition(int instanceId, FixedPointPosition current)
    {
        if (!_previousActorRenderPositions.TryGetValue(instanceId, out var previous)) return current;

        var elapsedSinceStep = Environment.TickCount64 - _clock.AccumulatedTimestamp;
        var alpha = Math.Clamp(elapsedSinceStep / (double)_clock.IntervalMilliseconds, 0d, 1d);
        return new FixedPointPosition(
            (int)Math.Round(previous.XRaw + (current.XRaw - previous.XRaw) * alpha),
            (int)Math.Round(previous.ZRaw + (current.ZRaw - previous.ZRaw) * alpha));
    }

    // World/screen orientation. The engine works in the executable's world
    // frame, where SCN placements and PTH rows share +Z, and +Z points up the
    // screen: the native minimap handler inverts Z, and the MAP loader keeps a
    // reversed row table. MAP tile rows are stored in screen order (row 0 at
    // the top), so terrain is drawn as stored and the camera stays in screen
    // pixels; only world positions and cells are mirrored here.

    /// <summary>Screen-space pixel row (before the camera offset) of a world 8.8 Z.</summary>
    private int WorldPixelY(int zRaw) => (MapCellHeight * FixedPointPosition.One - zRaw) / 8;

    /// <summary>Screen-space pixel row of the top edge of a world cell row.</summary>
    private int CellPixelTop(int cellZ) => (MapCellHeight - 1 - cellZ) * TerrainRasterizer.TileSize;

    /// <summary>World cell row under a screen-space pixel row.</summary>
    private int CellZAtPixel(int pixelY) => MapCellHeight - 1 - Math.Clamp(pixelY, 0, MapCellHeight * TerrainRasterizer.TileSize - 1) / TerrainRasterizer.TileSize;

    /// <summary>World cell under a screen-space pixel.</summary>
    private CellCoordinate CellAtPixel(int pixelX, int pixelY) => new(pixelX / TerrainRasterizer.TileSize, CellZAtPixel(pixelY));

    private int MapCellHeight => _gameplayMap?.Height ?? _gameplayPath?.Height ?? 0;
}
