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

// The recovered maine HUD exposes these common and unit-specific command modes.
internal enum GameplayCommandMode
{
    MoveOnly,
    AttackTarget,
    GroundSpecialTarget,
    Waypoints,
    HarvestVent,
    PlaceBuilding,
}

internal enum GameplayHudTab
{
    Build,
    Research,
    Options,
}

public sealed class MainForm : Form
{
    private sealed record DeathEffect(int EntityId, FixedPointPosition Position, ulong StartedAtTick);
    private sealed record ImpactEffect(int WeaponId, FixedPointPosition Position, ulong StartedAtTick);
    private sealed record CombatPresentation(EntityAnimationCandidate Candidate, ulong StartedAtTick);
    private sealed record GameplayActorVisual(
        SimulatedActor Actor,
        int RenderEntityId,
        EntityAnimationCandidate Candidate,
        CombatPresentation? CombatPresentation,
        DirectionalAnimationSelection? MoveSelection,
        string FileName,
        ushort Frame,
        Bitmap Bitmap,
        Rectangle CanvasBounds,
        Rectangle OpaqueBounds);
    // Campaigns use team zero. War maps choose their enabled team from the
    // player's selected faction in the decoded SCN roster.
    private int _localPlayerTeam;
    private readonly GameInstallation? _installation;
    private readonly GameplayHudLayout _gameplayHudLayout;
    private readonly WorldSimulation _world = new();
    private readonly FixedStepClock _clock;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    // Presentation retains one completed authoritative step so rendering can
    // interpolate movement at the display cadence. This has no simulation use:
    // commands, pathing, collisions, and animation selection continue to read
    // ScenarioSimulation's exact 66 ms state.
    private readonly Dictionary<int, FixedPointPosition> _previousActorRenderPositions = [];
    private readonly Direct3DSurface _surface;
    private GameCanvas? _activeCanvas;
    private readonly Dictionary<string, Image> _backgrounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GpuImage> _gpuBackgrounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GpuImage> _gpuColorKeyImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Bitmap, GpuImage> _gpuBitmaps = [];
    private readonly Dictionary<string, AnimationDefinition> _animationDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _animationFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Point> _animationOrigins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Rectangle> _animationOpaqueBounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Bitmap> _fontGlyphs = [];
    private readonly Dictionary<string, Bitmap> _remappedFontGlyphs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.OrdinalIgnoreCase);
    private BitmapFont? _menuFont;
    private EncyclopediaCatalog? _encyclopedia;
    private readonly Dictionary<string, EncyclopediaArticle> _encyclopediaArticles = new(StringComparer.OrdinalIgnoreCase);
    private int _encyclopediaCategory = 1;
    private int _encyclopediaEntry;
    private int _encyclopediaPreviewFrameOffset;
    private int _encyclopediaPreviewFacingIndex;
    private bool _encyclopediaPreviewPaused;
    private readonly Dictionary<(uint FrameId, bool FlipHorizontally, bool TransparentZero), GpuImage> _terrainGpuTiles = [];
    private Bitmap? _minimapPreview;
    private ScenarioWorld? _scenarioWorld;
    private ScenarioSimulation? _scenarioSimulation;
    private IReadOnlyList<ScenarioTrigger> _scenarioTriggers = [];
    private ScenarioMissionText? _missionText;
    private int _briefingScrollLine;
    private bool _storyReturnsToGameplay;
    private bool _resumeGameplayFromStory;
    private bool _gameplayPausedBeforeStory;
    private TerrainMap? _gameplayMap;
    private BtsTileset? _gameplayTileset;
    private PathRegionMap? _gameplayPath;
    private EntityCatalog? _entityCatalog;
    private WeaponCatalog? _weaponCatalog;
    private AreaEffectCatalog? _areaEffects;
    private DependencyCatalog? _dependencyCatalog;
    private BuildingFootprintCatalog? _buildingFootprints;
    private SoundCatalog? _soundCatalog;
    private EntityAnimationCatalog? _entityAnimations;
    private WeaponEffectCatalog? _weaponEffects;
    private IReadOnlyList<WorldEntity> _autonomousEntities = [];
    private CellOccupancy? _groundOccupancy;
    private CellOccupancy? _alternateOccupancy;
    private IReadOnlyList<MenuButton> _buttons = [];
    private MenuScreenId _screen = MenuScreenId.Main;
    private int? _hoveredButton;
    private int? _pressedButton;
    private bool _training;
    private bool _grayRace;
    private string _leaderName = string.Empty;
    private IReadOnlyList<SinglePlayerWarScenario> _singlePlayerMaps = [];
    private int _singlePlayerMapIndex;
    private readonly WarLobbyPlayer[] _warLobbyPlayers = WarLobbyPlayer.CreateDefault();
    private int _warStorageCells;
    private int _warArtifacts;
    private bool _warEruptingVents;
    private bool _warRenewableVents;
    // dc.exe stores these as 1..20 quarter-percent steps (display = value * 25).
    private int _warP7QuantityMultiplier = 100;
    private int _warP7FlowMultiplier = 100;
    private int _warCommanderRank = 0;
    private ScenarioChoice? _selectedScenario;
    private bool _showAssetNames;
    private bool _showPathRegions;
    private GameplayCommandMode _gameplayCommandMode = GameplayCommandMode.MoveOnly;
    private int? _pendingBuildingItemId;
    private GameplayHudTab _gameplayHudTab = GameplayHudTab.Build;
    private bool _gameplayPaused;
    // The native options group exposes ALLIES, while only its underlying
    // directed relation table is recovered. This panel is a port-owned view
    // that applies a deliberately symmetric local-player policy to that table.
    private bool _showAlliesPanel;
    private readonly HashSet<int> _selectedEntityInstanceIds = [];
    private int _cameraX = 30 * 32;
    private int _cameraY = 22 * 32;
    private CellCoordinate? _diagnosticMoveTarget;
    private IReadOnlyList<CellCoordinate> _diagnosticPathCells = [];
    private readonly List<DeathEffect> _deathEffects = [];
    private readonly List<ImpactEffect> _impactEffects = [];
    private readonly Dictionary<int, ulong> _firingActorStartedAt = [];
    private readonly Dictionary<int, byte> _firingActorVariantRoll = [];
    private readonly Dictionary<int, ulong> _hitActorStartedAt = [];
    private readonly Dictionary<int, ulong> _formDeploymentStartedAt = [];
    private readonly Dictionary<int, ulong> _formRetractionStartedAt = [];
    private Point? _mapDragStart;
    private Point _mapDragCamera;
    private bool _mapDragged;
    private bool _minimapDragging;
    private Point? _selectionDragStart;
    private Point _selectionDragCurrent;
    private ulong _selectionGestureStartedAtTick;
    private bool _selectionGestureToggle;
    private UnitSelectionLayerFilter _selectionGestureLayerFilter;
    private Point? _gameplayPointer;
    private bool _gameplayCursorHidden;
    // `maine` declares Last Msg / Next Msg at (4,460) and (24,460). The port
    // records its existing simulation/status feedback for those recovered
    // controls instead of adding a parallel notification system.
    private readonly List<string> _gameplayMessageHistory = [];
    private string? _lastRecordedGameplayStatus;
    private int _gameplayMessageIndex = -1;
    private bool _singlePlayerScrollDragging;
    private string _status;
    private ulong _screenStartedAtTick;

    public MainForm(GameInstallation? installation, MenuScreenId initialScreen = MenuScreenId.Main)
    {
        _installation = installation;
        _gameplayHudLayout = GameplayHudLayout.Load(installation);
        _clock = new FixedStepClock(Environment.TickCount64);
        _status = InspectInstallation(installation);

        Text = "Dark Colony";
        ClientSize = new Size(640, 480);
        MinimumSize = Size;
        MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        KeyPreview = true;

        _surface = new Direct3DSurface(RenderFrame) { Dock = DockStyle.Fill };
        _surface.MouseMove += SurfaceMouseMove;
        _surface.MouseLeave += (_, _) =>
        {
            _gameplayPointer = null;
            _singlePlayerScrollDragging = false;
            SetGameplayCursorVisibility(visible: true);
            SetHover(null);
        };
        _surface.MouseEnter += (_, _) => SetGameplayCursorVisibility(visible: _screen != MenuScreenId.Gameplay);
        _surface.MouseDown += SurfaceMouseDown;
        _surface.MouseUp += SurfaceMouseUp;
        Controls.Add(_surface);

        KeyDown += (_, eventArgs) =>
        {
            if (HandleNewGameKey(eventArgs.KeyCode))
            {
                eventArgs.Handled = true;
                return;
            }
            if (HandleGameplayKey(eventArgs.KeyCode))
            {
                eventArgs.Handled = true;
                return;
            }
            if (eventArgs.KeyCode == Keys.Escape && _screen == MenuScreenId.Story && _storyReturnsToGameplay)
            {
                ResumeGameplayFromStory();
                eventArgs.Handled = true;
                return;
            }
            if (eventArgs.KeyCode == Keys.Escape && _screen != MenuScreenId.Main)
            {
                ShowScreen(MenuScreenId.Main);
            }
        };
        KeyPress += (_, eventArgs) =>
        {
            if (_screen != MenuScreenId.NewGame || char.IsControl(eventArgs.KeyChar)) return;
            if (!char.IsLetterOrDigit(eventArgs.KeyChar) && eventArgs.KeyChar != ' ') return;
            if (_leaderName.Length >= 17) return;
            _leaderName += char.ToUpperInvariant(eventArgs.KeyChar);
            _surface.Invalidate();
            eventArgs.Handled = true;
        };

        ShowScreen(initialScreen);
        _timer.Tick += (_, _) =>
        {
            _clock.Advance(Environment.TickCount64, () =>
            {
                UpdateGameplayEdgeScroll();
                if (!_gameplayPaused)
                {
                    CapturePreviousActorRenderPositions();
                    _world.Step();
                    _scenarioSimulation?.Step(_world.LastCommands);
                    CaptureDeathEffects();
                    CaptureCombatSounds();
                    CaptureCombatAnimations();
                    CaptureBattlefieldTransportFeedback();
                    CaptureAttackOrderFeedback();
                    CaptureHealingFeedback();
                    CaptureInspireFeedback();
                    CaptureHarvesterFeedback();
                    CaptureConstructionFeedback();
                }
            });
            _surface.RenderAndPresent();
        };
        _timer.Start();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData) =>
        HandleNewGameKey(keyData & Keys.KeyCode) || HandleGameplayKey(keyData) || base.ProcessCmdKey(ref message, keyData);

    private bool HandleNewGameKey(Keys key)
    {
        if (_screen != MenuScreenId.NewGame || key != Keys.Back || _leaderName.Length == 0) return false;
        _leaderName = _leaderName[..^1];
        _surface.Invalidate();
        return true;
    }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SetGameplayCursorVisibility(visible: true);
            _timer.Dispose();
            _surface.Dispose();
            foreach (var image in _backgrounds.Values) image.Dispose();
            foreach (var image in _animationFrames.Values) image.Dispose();
            foreach (var image in _fontGlyphs.Values) image.Dispose();
            _minimapPreview?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static string InspectInstallation(GameInstallation? installation)
    {
        if (installation is null) return "Original data not found — use --data <installation>";
        try
        {
            var matrix = DamageMatrix.Load(installation.DataFile("gamestat", "mbullet.txt"));
            return $"Original data ready · damage matrix 0/0 = {matrix[0, 0]}%";
        }
        catch (Exception error) when (error is IOException or FormatException)
        {
            return $"Data error: {error.Message}";
        }
    }

    private void ShowScreen(MenuScreenId screen)
    {
        ClearTransientInputState();
        if (screen == MenuScreenId.Gameplay && !_resumeGameplayFromStory)
        {
            DisposeGameplayMinimapPreview();
            ReleaseTerrainGpuTiles();
            _scenarioWorld = null;
            _scenarioSimulation = null;
            _previousActorRenderPositions.Clear();
            _gameplayMap = null;
            _gameplayTileset = null;
            _gameplayPath = null;
            _selectedEntityInstanceIds.Clear();
            _gameplayHudTab = GameplayHudTab.Build;
            _gameplayPaused = false;
            _showAlliesPanel = false;
            _pendingBuildingItemId = null;
            _diagnosticMoveTarget = null;
            _diagnosticPathCells = [];
            _autonomousEntities = [];
            _firingActorStartedAt.Clear();
            _firingActorVariantRoll.Clear();
            _hitActorStartedAt.Clear();
            _formDeploymentStartedAt.Clear();
            _formRetractionStartedAt.Clear();
            _deathEffects.Clear();
            _groundOccupancy = null;
            _alternateOccupancy = null;
            _cameraX = 30 * 32;
            _cameraY = 22 * 32;
            LoadGameplayScenario();
        }
        _resumeGameplayFromStory = false;
        _screen = screen;
        _screenStartedAtTick = _world.TickCount;
        _hoveredButton = null;
        _pressedButton = null;
        _buttons = screen switch
        {
            MenuScreenId.Main => MainButtons(),
            MenuScreenId.NewGame => NewGameButtons(),
            MenuScreenId.LoadGame => LoadButtons(),
            MenuScreenId.SinglePlayer => SinglePlayerButtons(),
            MenuScreenId.Encyclopedia => EncyclopediaButtons(),
            MenuScreenId.NetworkOptions => NetworkButtons(),
            MenuScreenId.Story => StoryButtons(),
            _ => [],
        };
        _surface.Cursor = screen == MenuScreenId.Gameplay ? Cursors.Cross : Cursors.Hand;
        SetGameplayCursorVisibility(visible: screen != MenuScreenId.Gameplay);
        _surface.Invalidate();
    }

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

    private IReadOnlyList<MenuButton> MainButtons() =>
    [
        Button(0, 138, 314, 179, 25, "NEW CAMPAIGN", () => OpenNewGame(false)),
        Button(1, 138, 340, 179, 25, "TRAINING", () => OpenNewGame(true)),
        Button(2, 138, 366, 179, 25, "LOAD GAME", () => ShowScreen(MenuScreenId.LoadGame)),
        Button(3, 318, 314, 179, 25, "MULTI PLAYER WAR", () => ShowScreen(MenuScreenId.NetworkOptions)),
        Button(4, 318, 340, 179, 25, "SINGLE PLAYER WAR", () => ShowScreen(MenuScreenId.SinglePlayer)),
        Button(5, 318, 366, 179, 25, "ENCYCLOPEDIA", () => ShowScreen(MenuScreenId.Encyclopedia)),
        Button(16, 138, 392, 179, 25, "PLAY INTRO", () => _status = "Intro playback will be connected after media-source detection."),
        Button(12, 318, 392, 179, 25, "QUIT", Close),
    ];

    private IReadOnlyList<MenuButton> NewGameButtons() =>
    [
        Button(0, 193, 23, 90, 26, "HUMAN", () => SelectRace(false), !_grayRace),
        Button(1, 357, 23, 90, 26, "GRAY", () => SelectRace(true), _grayRace),
        Button(2, 399, 349, 179, 26, _training ? "START TRAINING" : "START CAMPAIGN", StartCampaign),
        Button(4, 521, 431, 90, 26, "BACK", () => ShowScreen(MenuScreenId.Main)),
    ];

    private IReadOnlyList<MenuButton> LoadButtons() =>
    [
        Button(4, 313, 447, 90, 26, "BACK", () => ShowScreen(MenuScreenId.Main)),
        Button(5, 403, 447, 90, 26, "LOAD", () => _status = "Save-game decoding is not implemented yet."),
    ];

    private IReadOnlyList<MenuButton> SinglePlayerButtons() =>
    [
        Button(0, 430, 452, 90, 26, "MENU", () => ShowScreen(MenuScreenId.Main)),
        Button(1, 530, 452, 90, 26, "READY", StartSinglePlayerWar),
        Button(2, 588, 194, 26, 26, "", () => SelectSinglePlayerMap(-1), artName: "UP"),
        Button(3, 588, 294, 26, 26, "", () => SelectSinglePlayerMap(1), artName: "DOWN"),
    ];

    private IReadOnlyList<MenuButton> EncyclopediaButtons() =>
    [
        Button(0, 309, 446, 89, 25, "BACK", () => ShowScreen(MenuScreenId.Main)),
        Button(1, 464, 310, 89, 25, "HUMANS", () => SelectEncyclopediaCategory(1), _encyclopediaCategory == 1),
        Button(11, 464, 348, 89, 25, "ARTIFACTS", () => SelectEncyclopediaCategory(2), _encyclopediaCategory == 2),
        Button(2, 464, 386, 89, 25, "GRAYS", () => SelectEncyclopediaCategory(0), _encyclopediaCategory == 0),
        Button(3, 272, 102, 25, 25, "", () => MoveEncyclopediaSelection(-1), artName: "UP"),
        Button(4, 272, 441, 25, 25, "", () => MoveEncyclopediaSelection(1), artName: "DOWN"),
        // `encycloe` identifies these exact native gadgets as LEFT/RIGHT/REW/FFW.
        Button(5, 343, 220, 23, 14, "", () => StepEncyclopediaPreview(-1), artName: "LEFT"),
        Button(6, 445, 220, 23, 14, "", () => StepEncyclopediaPreview(1), artName: "RIGHT"),
        Button(7, 511, 220, 27, 14, "", RewindEncyclopediaPreview, artName: "REW"),
        Button(8, 601, 220, 27, 14, "", FastForwardEncyclopediaPreview, artName: "FFW"),
    ];

    private void SelectEncyclopediaCategory(int category)
    {
        _encyclopediaCategory = category;
        _encyclopediaEntry = 0;
        ResetEncyclopediaPreview();
        ShowScreen(MenuScreenId.Encyclopedia);
        PlayEncyclopediaNarration();
    }

    private void MoveEncyclopediaSelection(int delta)
    {
        var category = Encyclopedia()?.Categories[_encyclopediaCategory];
        if (category is null || category.Entries.Count == 0) return;
        _encyclopediaEntry = (_encyclopediaEntry + delta + category.Entries.Count) % category.Entries.Count;
        ResetEncyclopediaPreview();
        _status = $"Encyclopedia: {category.Entries[_encyclopediaEntry].Name}";
        PlayEncyclopediaNarration();
    }

    private void ResetEncyclopediaPreview()
    {
        _encyclopediaPreviewFrameOffset = 0;
        _encyclopediaPreviewFacingIndex = 0;
        _encyclopediaPreviewPaused = false;
    }

    private void StepEncyclopediaPreview(int delta)
    {
        _encyclopediaPreviewFacingIndex = (_encyclopediaPreviewFacingIndex + delta + 8) % 8;
        _encyclopediaPreviewFrameOffset = 0;
        _encyclopediaPreviewPaused = false;
        _status = delta < 0 ? "Encyclopedia animation: turn left." : "Encyclopedia animation: turn right.";
    }

    private void RewindEncyclopediaPreview()
    {
        _encyclopediaPreviewFrameOffset = 0;
        _encyclopediaPreviewPaused = true;
        _status = "Encyclopedia animation: first frame.";
    }

    private void FastForwardEncyclopediaPreview()
    {
        _encyclopediaPreviewFrameOffset = int.MaxValue;
        _encyclopediaPreviewPaused = true;
        _status = "Encyclopedia animation: last frame.";
    }

    private void PlayEncyclopediaNarration()
    {
        if (_installation is null || Encyclopedia()?.Categories[_encyclopediaCategory] is not { } category ||
            (uint)_encyclopediaEntry >= (uint)category.Entries.Count) return;
        try
        {
            var entry = category.Entries[_encyclopediaEntry];
            var relativePath = Path.ChangeExtension(entry.ResourceStem.Replace('/', Path.DirectorySeparatorChar), ".WAV");
            var path = Path.Combine(_installation.RootPath, relativePath);
            if (File.Exists(path)) new SoundPlayer(path).Play();
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Encyclopedia narration error: {error.Message}";
        }
    }

    private IReadOnlyList<MenuButton> NetworkButtons() =>
    [
        Button(0, 38, 41, 178, 24, "TCP/IP", () => _status = "TCP/IP selected."),
        Button(1, 38, 89, 178, 24, "IPX NETWORK", () => _status = "Legacy IPX is not supported."),
        Button(2, 38, 137, 178, 24, "MODEM", () => _status = "Legacy modem play is not supported."),
        Button(3, 38, 185, 178, 24, "SERIAL CABLE", () => _status = "Legacy serial play is not supported."),
        Button(4, 454, 377, 178, 24, "ACT AS SERVER", () => _status = "Modern networking is a later milestone."),
        Button(5, 454, 409, 178, 24, "CONNECT TO SERVER", () => _status = "Modern networking is a later milestone."),
        Button(6, 454, 441, 178, 24, "MAIN MENU", () => ShowScreen(MenuScreenId.Main)),
    ];

    // `intrface/storye` supplies the native scroll geometry and Next/Back
    // controls. Mission text comes from the scenario companion files rather
    // than being copied into UI code.
    private IReadOnlyList<MenuButton> StoryButtons() =>
    [
        Button(2, 602, 15, 26, 26, "", () => ScrollBriefing(-1), artName: "UP"),
        Button(3, 602, 408, 26, 26, "", () => ScrollBriefing(1), artName: "DOWN"),
        Button(4, 453, 449, 90, 26, "BACK", LeaveStoryBack),
        Button(5, 543, 449, 90, 26, "NEXT", LeaveStoryNext),
    ];

    private void LeaveStoryBack()
    {
        if (!_storyReturnsToGameplay)
        {
            ShowScreen(MenuScreenId.NewGame);
            return;
        }
        ResumeGameplayFromStory();
    }

    private void LeaveStoryNext()
    {
        if (!_storyReturnsToGameplay)
        {
            ShowScreen(MenuScreenId.Gameplay);
            return;
        }
        ResumeGameplayFromStory();
    }

    private void ResumeGameplayFromStory()
    {
        _storyReturnsToGameplay = false;
        _resumeGameplayFromStory = true;
        _gameplayPaused = _gameplayPausedBeforeStory;
        ShowScreen(MenuScreenId.Gameplay);
    }

    private MenuButton Button(
        int id,
        int x,
        int y,
        int width,
        int height,
        string label,
        Action action,
        bool selected = false,
        string? artName = null) =>
        new(id, new Rectangle(x, y, width, height), label, action, selected, artName);

    private void OpenNewGame(bool training)
    {
        _training = training;
        ShowScreen(MenuScreenId.NewGame);
    }

    private void SelectRace(bool gray)
    {
        _grayRace = gray;
        _singlePlayerMapIndex = 0;
        ShowScreen(MenuScreenId.NewGame);
    }

    private void StartCampaign()
    {
        _storyReturnsToGameplay = false;
        _resumeGameplayFromStory = false;
        _selectedScenario = null;
        _localPlayerTeam = 0;
        _status = $"{(_grayRace ? "Gray" : "Human")} {(_training ? "training" : "campaign")} terrain harness.";
        var scenario = GameplayScenario();
        var scenarioFile = _installation?.DataFile("scenario", scenario.Directory, $"{scenario.Name}.scn");
        _missionText = scenarioFile is not null && File.Exists(scenarioFile)
            ? ScenarioMissionText.LoadForScenario(scenarioFile)
            : null;
        _briefingScrollLine = 0;
        ShowScreen(_missionText is { Briefing.Length: > 0 } ? MenuScreenId.Story : MenuScreenId.Gameplay);
    }

    private void ScrollBriefing(int delta)
    {
        var lineCount = MissionBriefingLines().Count;
        _briefingScrollLine = Math.Clamp(_briefingScrollLine + delta * 3, 0, Math.Max(0, lineCount - 24));
        _surface.Invalidate();
    }

    private void StartSinglePlayerWar()
    {
        EnsureSinglePlayerMaps();
        var maps = _singlePlayerMaps;
        if (maps.Count == 0)
        {
            _status = $"No complete {(_grayRace ? "Gray" : "Human")} War maps were found in scenario\\mplayer.";
            return;
        }

        _singlePlayerMapIndex = Math.Clamp(_singlePlayerMapIndex, 0, maps.Count - 1);
        var selected = maps[_singlePlayerMapIndex];
        var localPlayer = _warLobbyPlayers.FirstOrDefault(player => player.Type == WarLobbyPlayerType.Human);
        if (localPlayer is null)
        {
            _status = "Single Player War needs one Human player slot.";
            return;
        }

        var faction = localPlayer.Gray ? 1 : 0;
        var settings = new SinglePlayerWarSettings(
            _warStorageCells,
            _warArtifacts,
            _warEruptingVents,
            _warRenewableVents,
            _warP7QuantityMultiplier,
            _warP7FlowMultiplier,
            _warCommanderRank);
        if (!selected.TryCreateLaunch(faction, settings, out var launch))
        {
            _status = $"{selected.DisplayName} has no enabled {(localPlayer.Gray ? "Gray" : "Human")} team.";
            return;
        }

        _selectedScenario = new ScenarioChoice("mplayer", launch.Stem, WarLaunch: launch);
        _localPlayerTeam = launch.LocalTeamId;
        _grayRace = localPlayer.Gray;
        _status = $"Single Player War: {launch.Stem.ToUpperInvariant()} as {(localPlayer.Gray ? "Gray" : "Human")} team {_localPlayerTeam + 1}; P7 { _warP7QuantityMultiplier}% / flow {_warP7FlowMultiplier}%.";
        ShowScreen(MenuScreenId.Gameplay);
    }

    private void EnsureSinglePlayerMaps()
    {
        if (_singlePlayerMaps.Count != 0 || _installation is null) return;
        _singlePlayerMaps = SinglePlayerWarCatalog.Load(_installation).Scenarios;
        _singlePlayerMapIndex = Math.Clamp(_singlePlayerMapIndex, 0, Math.Max(0, _singlePlayerMaps.Count - 1));
    }

    private void SelectSinglePlayerMap(int delta)
    {
        EnsureSinglePlayerMaps();
        var maps = _singlePlayerMaps;
        if (maps.Count == 0) return;
        _singlePlayerMapIndex = (_singlePlayerMapIndex + delta + maps.Count) % maps.Count;
            _status = $"Single Player War map: {maps[_singlePlayerMapIndex].Stem.ToUpperInvariant()}.";
        _surface.Invalidate();
    }

    private string BackgroundName() => _screen switch
    {
        MenuScreenId.Main => "intro",
        MenuScreenId.NewGame => "choo",
        MenuScreenId.LoadGame => "loader",
        MenuScreenId.SinglePlayer => "tcpwait",
        MenuScreenId.Encyclopedia => "ency",
        MenuScreenId.NetworkOptions => "net",
        MenuScreenId.Story => "story",
        MenuScreenId.Gameplay => "intrface",
        _ => "intro",
    };

    private Image? Background()
    {
        if (_installation is null) return null;
        var name = BackgroundName();
        if (_backgrounds.TryGetValue(name, out var cached)) return cached;
        var path = _installation.DataFile("intrface", $"{name}.gif");
        if (!File.Exists(path)) return null;
        using var source = Image.FromFile(path);
        var copy = new Bitmap(source);
        _backgrounds[name] = copy;
        return copy;
    }

    private void RenderFrame(Graphics graphics, GameCanvas canvas)
    {
        _activeCanvas = canvas;
        var background = Background();
        if (_screen == MenuScreenId.Gameplay)
        {
            DrawGameplayTerrain(canvas);
            DrawGameplayPathRegions(graphics);
            DrawGameplayVents(graphics);
            DrawBuildingPlacementPreview(graphics);
            DrawGameplayActors(graphics, canvas);
            DrawGameplayFogOfWar(graphics);
            if (background is not null) DrawGameplayHud(graphics, background);
            DrawGameplayMinimap(graphics);
            DrawGameplayUnitHud(graphics);
            DrawGameplayPanelPrompt(graphics);
            DrawGameplayCursor(graphics);
            // Keep the native gameplay viewport clear. The previous
            // developer-control panel covered the upper-left map area, which
            // the original Single Player War HUD leaves unobstructed.
        }
        else if (background is not null)
        {
            canvas.Draw(GpuBackground(BackgroundName(), background), new Rectangle(0, 0, 640, 480));
        }

        if (_screen == MenuScreenId.Main) DrawOpeningLogo(graphics);
        DrawInnerMenuAssets(graphics);
        if (_screen == MenuScreenId.NewGame) DrawNewGameLeaderName(graphics);
        if (_screen == MenuScreenId.SinglePlayer) DrawSinglePlayerMapSelection(graphics);
        if (_screen == MenuScreenId.Story) DrawMissionBriefing(graphics);

        for (var index = 0; index < _buttons.Count; index++) DrawButton(graphics, _buttons[index], index);

        if (_screen == MenuScreenId.LoadGame)
        {
            DrawPanelText(graphics, "NO RECOVERED SAVE GAMES", new Rectangle(75, 105, 430, 250));
        }

        else if (_screen == MenuScreenId.Encyclopedia)
        {
            DrawEncyclopedia(graphics);
        }
        _activeCanvas = null;
    }

    private GpuImage GpuBackground(string name, Image background)
    {
        if (_gpuBackgrounds.TryGetValue(name, out var cached)) return cached;
        using var bitmap = new Bitmap(640, 480, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(background, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var bgra = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            var rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var source = y * Math.Abs(data.Stride) + x * 4;
                var destination = (y * bitmap.Width + x) * 4;
                rgba[destination] = bgra[source + 2];
                rgba[destination + 1] = bgra[source + 1];
                rgba[destination + 2] = bgra[source];
                rgba[destination + 3] = bgra[source + 3];
            }
            cached = new GpuImage(bitmap.Width, bitmap.Height, rgba);
            _gpuBackgrounds.Add(name, cached);
            return cached;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private GpuImage GpuColorKeyImage(string name, Image source)
    {
        if (_gpuColorKeyImages.TryGetValue(name, out var cached)) return cached;
        using var bitmap = new Bitmap(640, 480, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var bgra = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            var rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var sourceOffset = y * Math.Abs(data.Stride) + x * 4;
                var destination = (y * bitmap.Width + x) * 4;
                var red = bgra[sourceOffset + 2];
                var green = bgra[sourceOffset + 1];
                var blue = bgra[sourceOffset];
                rgba[destination] = red;
                rgba[destination + 1] = green;
                rgba[destination + 2] = blue;
                rgba[destination + 3] = red == 0 && green == 0 && blue == 0 ? (byte)0 : bgra[sourceOffset + 3];
            }
            cached = new GpuImage(bitmap.Width, bitmap.Height, rgba);
            _gpuColorKeyImages.Add(name, cached);
            return cached;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private GpuImage GpuBitmap(Bitmap bitmap)
    {
        if (_gpuBitmaps.TryGetValue(bitmap, out var cached)) return cached;
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            var rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var source = y * Math.Abs(data.Stride) + x * 4;
                var destination = (y * bitmap.Width + x) * 4;
                rgba[destination] = bgra[source + 2];
                rgba[destination + 1] = bgra[source + 1];
                rgba[destination + 2] = bgra[source];
                rgba[destination + 3] = bgra[source + 3];
            }
            cached = new GpuImage(bitmap.Width, bitmap.Height, rgba);
            _gpuBitmaps.Add(bitmap, cached);
            return cached;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
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

    // `0x409c94` subtracts 0x207 from pointer X and 0x5a from pointer Y,
    // then divides against 0x60 by 0x54. Those operands identify this exact
    // native 96x84 interior, inside the top-right HUD well.
    private static readonly Rectangle GameplayMinimapBounds = new(519, 6, 96, 84);

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
            GameplayMinimapBounds.Y + (int)Math.Round((worldHeight - _cameraY - 458) / (double)worldHeight * GameplayMinimapBounds.Height),
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
            // Match the native handler's centred odd numerator and its
            // vertically inverted map coordinate.
            var mapX = Math.Min(map.Width - 1, ((x * 2 + 1) * map.Width) / (image.Width * 2));
            var mapY = Math.Min(map.Height - 1, (((image.Height - 1 - y) * 2 + 1) * map.Height) / (image.Height * 2));
            var cell = map[mapX, mapY];
            var tileId = cell.OverlayTileId != 0 ? cell.OverlayTileId : cell.BaseTileId;
            if (!tileset.TilesById.TryGetValue(tileId, out var tile)) continue;
            var paletteIndex = tile.PaletteIndices[16 * TerrainTile.Width + 16];
            var color = tileset.Palette[paletteIndex];
            image.SetPixel(x, y, Color.FromArgb(color.Red, color.Green, color.Blue));
        }
        return image;
    }

    private ScenarioChoice GameplayScenario() => _selectedScenario ?? (_training
        ? new ScenarioChoice("test", _grayRace ? "atrain1" : "htrain1")
        : new ScenarioChoice(_grayRace ? "alien" : "human", _grayRace ? "alien01" : "human01"));

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
            var footprints = BuildingFootprintCatalog.Load(_installation.ExecutablePath);
            _buildingFootprints = footprints;
            _scenarioWorld = ScenarioWorld.Create(definition, footprints);
            _entityCatalog ??= EntityCatalog.Load(_installation.DataFile("gamestat", "gamestat.txt"));
            _weaponCatalog ??= WeaponCatalog.Load(_installation.DataFile("gamestat", "weapstat.txt"));
            _areaEffects ??= AreaEffectCatalog.Load(_installation.DataFile("gamestat", "boomstat.txt"));
            _dependencyCatalog ??= DependencyCatalog.Load(_installation.DataFile("gamestat", "depend.txt"));
            var damageMatrix = DamageMatrix.Load(_installation.DataFile("gamestat", "mbullet.txt"));
            _scenarioSimulation = ScenarioSimulation.Create(definition, _entityCatalog, _gameplayPath, footprints,
                weaponCatalog: _weaponCatalog, damageMatrix: damageMatrix, dependencyCatalog: _dependencyCatalog, areaEffects: _areaEffects,
                randomTable: NativeRandomTable.Load(_installation.ExecutablePath));
            _previousActorRenderPositions.Clear();
            _groundOccupancy = _scenarioSimulation.GroundOccupancy;
            _alternateOccupancy = _scenarioSimulation.AlternateOccupancy;
            _autonomousEntities = _scenarioSimulation.Actors
                .Where(actor => actor.Seed.Team == AutonomousSpawnSeeder.InternalNeutralTeam)
                .Select(actor => actor.Seed).ToArray();
            var localActor = _scenarioSimulation.Actors
                .Where(actor => actor.Seed.Team == _localPlayerTeam && actor.Definition.MovementSpeed > 0)
                .OrderBy(actor => actor.Seed.InstanceId)
                .FirstOrDefault();
            if (localActor is not null)
            {
                _cameraX = localActor.Movement.OccupiedCell.X * 32 - 258;
                _cameraY = localActor.Movement.OccupiedCell.Z * 32 - 229;
            }
            ClampGameplayCamera();
            _status = $"Loaded {scenario.Directory}\\{scenario.Name}: {_scenarioSimulation.Actors.Count} actors / {_scenarioTriggers.Count} triggers (not executing).";
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
        var firstZ = Math.Max(0, _cameraY / 32);
        var lastX = Math.Min(_gameplayPath.Width - 1, (_cameraX + 515) / 32);
        var lastZ = Math.Min(_gameplayPath.Height - 1, (_cameraY + 457) / 32);
        for (var z = firstZ; z <= lastZ; z++)
        for (var x = firstX; x <= lastX; x++)
        {
            var cell = new CellCoordinate(x, z);
            var region = _gameplayPath.RegionAt(cell);
            var screenX = x * 32 - _cameraX;
            var screenY = z * 32 - _cameraY;
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
            if (z > 0 && _gameplayPath.RegionAt(new CellCoordinate(x, z - 1)) != region)
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
                var y = vent.Position.Z * 32 - _cameraY;
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
            var y = vent.Position.Z * 32 - _cameraY;
            graphics.DrawEllipse(vent.HarvesterInstanceId is null ? unclaimed : claimed, x + 7, y + 7, 18, 18);
            DrawMenuText(graphics, vent.HarvesterInstanceId is null ? "P7" : "P7+", new Rectangle(x + 6, y - 1, 24, 14), center: false, remap: Color.FromArgb(220, 230, 185, 40));
        }
        graphics.Restore(state);
    }

    private void DrawGameplayFogOfWar(Graphics graphics)
    {
        if (_scenarioSimulation is null || _gameplayMap is null) return;
        // The running original leaves undiscovered map space black. Apply the
        // engine's current sight after terrain and actors so presentation cannot
        // leak a hostile unit beyond its day/night observation radius. Explored
        // terrain memory is a separate future rule; this is live sight only.
        var state = _activeCanvas is null ? graphics.Save() : null;
        if (_activeCanvas is null) graphics.SetClip(new Rectangle(0, 0, 516, 458));
        using var unseen = _activeCanvas is null ? new SolidBrush(Color.Black) : null;
        var firstX = Math.Max(0, _cameraX / 32);
        var firstZ = Math.Max(0, _cameraY / 32);
        var lastX = Math.Min(_gameplayMap.Width - 1, (_cameraX + 515) / 32);
        var lastZ = Math.Min(_gameplayMap.Height - 1, (_cameraY + 457) / 32);
        for (var z = firstZ; z <= lastZ; z++)
        for (var x = firstX; x <= lastX; x++)
        {
            if (_scenarioSimulation.IsCellVisibleToTeam(_localPlayerTeam, new CellCoordinate(x, z))) continue;
            var bounds = new Rectangle(x * 32 - _cameraX, z * 32 - _cameraY, 32, 32);
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
            var bounds = new Rectangle(cell.X * 32 - _cameraX + 1, cell.Z * 32 - _cameraY + 1, 30, 30);
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

        var origin = new CellCoordinate((pointer.X + _cameraX) / 32, (pointer.Y + _cameraY) / 32);
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
        var origin = _animationOrigins.GetValueOrDefault(key);
        var position = ActorPosition(entity);
        var canvas = new Rectangle(
            position.XRaw / 8 - _cameraX + origin.X,
            position.ZRaw / 8 - _cameraY + origin.Y,
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
            foreach (var entity in GameplayEntities().OrderBy(entity => ActorPosition(entity).ZRaw).ThenBy(entity => ActorPosition(entity).XRaw))
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
                var worldY = position.ZRaw / 8;
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
                var y = projectile.Position.ZRaw / 8 - projectile.HeightRaw / 8 - _cameraY;
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
                        var origin = _animationOrigins.GetValueOrDefault($"{fileName}:{frame}");
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
                        cell.Z * 32 + 16 - _cameraY)).ToArray();
                    for (var index = 1; index < points.Length; index++)
                        canvas.Line(points[index - 1], points[index], Color.FromArgb(225, 255, 205, 55), thickness: 2, foreground: true);
                }
                var x = target.X * 32 - _cameraX;
                var y = target.Z * 32 - _cameraY;
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
            new(ActorPosition(lead).XRaw / 8 - _cameraX, ActorPosition(lead).ZRaw / 8 - _cameraY),
        };
        points.AddRange(destinations.Select(cell => new Point(
            cell.X * 32 + 16 - _cameraX,
            cell.Z * 32 + 16 - _cameraY)));
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
            }
            else _status = $"Unit production rejected: {production.Outcome}.";
        }
        foreach (var research in _scenarioSimulation.LastResearchCompletions)
        {
            if (research.TeamId != _localPlayerTeam) continue;
            _status = research.Outcome == ResearchOutcome.Completed
                ? $"Research item {research.DependencyItemId} complete."
                : $"Research rejected: {research.Outcome}.";
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
            if (deployment.Outcome == StealDeploymentOutcome.Deployed && source is not null)
            {
                _formDeploymentStartedAt[deployment.EntityInstanceId] = _world.TickCount;
                PlayGameplaySound(source.Definition.Id, "DPY");
                _status = $"{source.Definition.DisplayName} deployed; nearby enemy mining income will be intercepted.";
            }
            else if (deployment.Outcome == StealDeploymentOutcome.Retracted && source is not null)
            {
                _formRetractionStartedAt[deployment.EntityInstanceId] = _world.TickCount;
                _status = $"{source.Definition.DisplayName} retracted from its stealing stance and is mobile.";
            }
            else _status = $"Steal deployment rejected: {deployment.Outcome}.";
        }
        foreach (var theft in _scenarioSimulation.LastP7Thefts)
        {
            if (theft.ThiefTeamId == _localPlayerTeam)
                _status = $"Stole {theft.Amount} P7 from enemy miner #{theft.VictimHarvesterInstanceId}.";
            else if (theft.VictimTeamId == _localPlayerTeam)
                _status = $"Enemy unit #{theft.ThiefInstanceId} intercepted {theft.Amount} P7.";
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
            var origin = _animationOrigins.GetValueOrDefault($"{fileName}:{frame}");
            canvas.Draw(GpuBitmap(bitmap), effect.Position.XRaw / 8 - _cameraX + origin.X, effect.Position.ZRaw / 8 - _cameraY + origin.Y);
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
            var origin = _animationOrigins.GetValueOrDefault($"{fileName}:{frame}");
            canvas.Draw(GpuBitmap(bitmap),
                transport.Position.XRaw / 8 - _cameraX + origin.X,
                transport.Position.ZRaw / 8 - transport.HeightRaw / 8 - _cameraY + origin.Y);
            if (!_showAssetNames) continue;
            using var font = new Font(FontFamily.GenericMonospace, 8, FontStyle.Regular, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.FromArgb(245, 241, 200));
            var pursuit = transport.IsPursuing
                ? $" {(transport.IsTurning ? $"TURN:{transport.Facing.Current}→{transport.Facing.Target}" : $"MOVE:{transport.HorizontalExecutionsRemaining}")}→({transport.PursuitTarget!.Value.Cell.X},{transport.PursuitTarget.Value.Cell.Z})"
                : string.Empty;
            graphics.DrawString($"transport #{transport.TransportEntityId}/{transport.InstanceId} · {transport.Phase}{pursuit} H{transport.HeightRaw} · {fileName}:{candidate.AnimationName}", font, text,
                transport.Position.XRaw / 8 - _cameraX + 8,
                transport.Position.ZRaw / 8 - transport.HeightRaw / 8 - _cameraY - 20);
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
            var origin = _animationOrigins.GetValueOrDefault($"{fileName}:{frame}");
            canvas.Draw(GpuBitmap(bitmap), effect.Position.XRaw / 8 - _cameraX + origin.X, effect.Position.ZRaw / 8 - _cameraY + origin.Y);
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

    private void DrawGameplayHud(Graphics graphics, Image hud)
    {
        if (_activeCanvas is { } canvas)
        {
            canvas.Draw(GpuColorKeyImage("gameplay-hud", hud), new Rectangle(0, 0, 640, 480));
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

    private void DrawGameplayUnitHud(Graphics graphics)
    {
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
            var message = GameplayMessageText();
            DrawGameplayHudText(graphics, FitGameplayReadout(message, _gameplayHudLayout.MessageStatus.CharacterCapacity),
                new Rectangle(_gameplayHudLayout.MessageStatus.Origin, new Size(427, 14)));
            // maine in_text 234: the original HUD's days counter at 613,433.
            DrawMenuText(graphics, _scenarioSimulation.DayNight.CompletedDays.ToString(), new Rectangle(604, 427, 30, 14), remap: Color.FromArgb(205, 225, 190));
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
        DrawGameplayPanelIdentity(graphics, name);
        DrawGameplaySelectionDiagnostics(graphics, actor, definition);
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
        var origin = _animationOrigins.GetValueOrDefault($"curs.fin:{frame}:base");
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

    private void DrawGameplayTabs(Graphics graphics)
    {
        DrawGameplayIconButton(graphics, _gameplayHudLayout.BuildTab.Bounds, _gameplayHudLayout.BuildTab.Frame, _gameplayHudTab == GameplayHudTab.Build, true);
        DrawGameplayIconButton(graphics, _gameplayHudLayout.ResearchTab.Bounds, _gameplayHudLayout.ResearchTab.Frame, _gameplayHudTab == GameplayHudTab.Research, true);
        DrawGameplayIconButton(graphics, _gameplayHudLayout.OptionsTab.Bounds, _gameplayHudLayout.OptionsTab.Frame, _gameplayHudTab == GameplayHudTab.Options, true);
    }

    private void DrawSelectedUnitCommands(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        // Group 40 has common actions plus one shared, contextual ability
        // slot. Keep its label/frame tied to the original `maine` controls.
        var commandState = GameplayCommandState(selected);
        DrawGameplayCommandButton(graphics, _gameplayHudLayout.Stop, commandState.AnyCanStop);
        DrawGameplayCommandButton(graphics, _gameplayHudLayout.MoveOnly, commandState.AnyCanMove, GameplayCommandMode.MoveOnly);
        DrawGameplayCommandButton(graphics, _gameplayHudLayout.MoveAndAttack, commandState.AnyCanAttack, GameplayCommandMode.AttackTarget);
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
        DrawGameplayPanelIdentity(graphics, name);
        DrawGameplaySelectionDiagnostics(graphics, actor, definition);
        if (HoveredGameplayCommandLabel(displayedButtons) is { } hoveredCommand)
            DrawGameplayPanelIdentity(graphics, hoveredCommand);
    }

    private void DrawObservedEntityHud(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        var lead = selected[0];
        var definition = GameplayDefinition(lead);
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        var name = selected.Count == 1 && definition is not null
            ? definition.DisplayName
            : $"{selected.Count} Units";
        DrawGameplayPanelIdentity(graphics, name);
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
        DrawGameplayPanelIdentity(graphics,
            selected.Count == 1 && definition is not null ? definition.DisplayName : $"{selected.Count} Structures");
        // A selected completed structure is a production source, not a
        // generic faction palette.  `depend.txt` records the prerequisite
        // building for every troop, so expose only the entries this exact
        // source can satisfy.  This keeps the HUD's unit buttons aligned with
        // the engine's authoritative source-building validation.
        DrawProductionButtons(graphics, lead);
        // Structures use the same live actor state as units. Showing their
        // health and decoded stats here keeps production selection from
        // hiding damage state behind a generic instruction.
        if (_showAssetNames)
            DrawGameplayLowerReadout(graphics, definition is null
                ? (!AvailableTroopItems().Any() ? "NO MATCHED PRODUCTION" : "SELECT UNIT")
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
        DrawGameplayPanelIdentity(graphics, name);
        DrawGameplaySelectionDiagnostics(graphics, actor, definition);
        if (HoveredGameplayCommandLabel([_gameplayHudLayout.Stop, _gameplayHudLayout.MoveAndAttack]) is { } hoveredCommand)
            DrawGameplayPanelIdentity(graphics, hoveredCommand);
    }

    private void DrawSelectedStealStanceHud(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        DrawGameplayCommandButton(graphics,
            _gameplayHudLayout.Contextual with { Frame = 75, Label = "STEAL MONEY" }, true);
        var lead = selected[0];
        var definition = GameplayDefinition(lead);
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        var name = selected.Count == 1 && definition is not null ? definition.DisplayName : $"{selected.Count} Stealing";
        DrawGameplayPanelIdentity(graphics, name);
        DrawGameplaySelectionDiagnostics(graphics, actor, definition, "STEALING: 50% NEARBY MINER P7");
        if (HoveredGameplayCommandLabel([_gameplayHudLayout.Contextual with { Frame = 75, Label = "STEAL MONEY" }]) is { } hoveredCommand)
            DrawGameplayPanelIdentity(graphics, hoveredCommand);
    }

    private void DrawSelectedHarvesterHud(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        DrawGameplayCommandButton(graphics,
            _gameplayHudLayout.Contextual with { Frame = 74, Label = "DEPLOY" }, true);
        var lead = selected[0];
        var definition = GameplayDefinition(lead);
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        var name = selected.Count == 1 && definition is not null ? definition.DisplayName : $"{selected.Count} Miners";
        DrawGameplayPanelIdentity(graphics, name);
        DrawGameplaySelectionDiagnostics(graphics, actor, definition,
            actor?.HarvestVentId is { } ventId ? $"P7 VENT {ventId + 1}: ATTACHED" : "P7 MINER: DEPLOYED");
        if (HoveredGameplayCommandLabel([_gameplayHudLayout.Contextual with { Frame = 74, Label = "DEPLOY" }]) is { } hoveredCommand)
            DrawGameplayPanelIdentity(graphics, hoveredCommand);
    }

    private void DrawSelectedMineHud(Graphics graphics, IReadOnlyList<WorldEntity> selected)
    {
        var lead = selected[0];
        var definition = GameplayDefinition(lead);
        var actor = _scenarioSimulation?.Actor(lead.InstanceId);
        var name = selected.Count == 1 && definition is not null ? definition.DisplayName : $"{selected.Count} Mines";
        DrawGameplayPanelIdentity(graphics, name);
        var triggerCount = actor is null ? 0 : _scenarioSimulation?.MineTriggersRemainingFor(actor) ?? 0;
        var readiness = actor?.CooldownTicks > 0 ? $"REARM {actor.CooldownTicks}" : "READY";
        DrawGameplaySelectionDiagnostics(graphics, actor, definition,
            $"ARMED - {triggerCount} TRIGGER{(triggerCount == 1 ? string.Empty : "S")} - {readiness}");
    }

    private void DrawBuildCatalog(Graphics graphics)
    {
        DrawBuildCatalogButtons(graphics);
        DrawGameplayPanelIdentity(graphics, "Build");
    }

    // `maine` group 84 (Human) and group 53 (Gray) combine their troop
    // controls with the building group. The two advanced Sci-Pod/Robo-Ftr
    // variants reuse the same physical right-column button as their base form.
    private static readonly IReadOnlyDictionary<int, Point> HumanTroopSlots = new Dictionary<int, Point>
    {
        [87] = new(518, 112), [89] = new(518, 153), [90] = new(518, 194),
        [92] = new(518, 235), [91] = new(518, 276), [88] = new(518, 317),
        [93] = new(518, 358), [94] = new(577, 112), [135] = new(577, 153)
    };

    private static readonly IReadOnlyDictionary<int, Point> GrayTroopSlots = new Dictionary<int, Point>
    {
        [46] = new(518, 112), [48] = new(518, 153), [71] = new(518, 194),
        [49] = new(518, 235), [50] = new(518, 276), [47] = new(518, 317),
        [51] = new(518, 358), [52] = new(577, 112), [134] = new(577, 153)
    };

    private static readonly IReadOnlyDictionary<int, Point> HumanBuildingSlots = new Dictionary<int, Point>
    {
        [206] = new(577, 194), [80] = new(577, 235), [81] = new(577, 276),
        [85] = new(577, 276), [82] = new(577, 317), [86] = new(577, 317),
        [83] = new(577, 358)
    };

    private static readonly IReadOnlyDictionary<int, Point> GrayBuildingSlots = new Dictionary<int, Point>
    {
        [205] = new(577, 194), [41] = new(577, 235), [42] = new(577, 276),
        [97] = new(577, 276), [43] = new(577, 317), [98] = new(577, 317),
        [44] = new(577, 358)
    };

    private void DrawBuildCatalogButtons(Graphics graphics)
    {
        foreach (var item in AvailableTroopItems())
            if (TroopSlots().TryGetValue(item.UiId, out var position))
                DrawMappedCatalogButton(graphics, CatalogBounds(item.UiId, position), item.UiId, TroopLabel(item), PurchaseEligibilityFor(item) == PurchaseEligibility.Available);
        foreach (var item in AvailableBuildingItems().GroupBy(item => BuildingSlots().GetValueOrDefault(item.UiId)).Select(group => group
                     .OrderByDescending(item => PurchaseEligibilityFor(item) == PurchaseEligibility.Available)
                     .ThenByDescending(item => item.Id)
                     .First()))
            if (BuildingSlots().TryGetValue(item.UiId, out var position))
                DrawMappedCatalogButton(graphics, CatalogBounds(item.UiId, position), item.UiId, BuildingLabel(item), PurchaseEligibilityFor(item) == PurchaseEligibility.Available);
    }

    private void DrawProductionButtons(Graphics graphics, WorldEntity structure)
    {
        foreach (var item in TroopItemsForStructure(structure))
            if (TroopSlots().TryGetValue(item.UiId, out var position))
                DrawMappedCatalogButton(graphics, CatalogBounds(item.UiId, position), item.UiId,
                    TroopLabel(item), PurchaseEligibilityFor(item) == PurchaseEligibility.Available);
    }

    private IReadOnlyDictionary<int, Point> TroopSlots() => _grayRace ? GrayTroopSlots : HumanTroopSlots;
    private IReadOnlyDictionary<int, Point> BuildingSlots() => _grayRace ? GrayBuildingSlots : HumanBuildingSlots;
    private Rectangle CatalogBounds(int uiId, Point fallbackPosition) =>
        _gameplayHudLayout.CatalogBounds(uiId, new Rectangle(fallbackPosition, new Size(59, 41)));

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

    private DependencyDefinition? BuildCatalogItemAt(Point point)
    {
        var troop = AvailableTroopItems()
            .FirstOrDefault(item => TroopSlots().TryGetValue(item.UiId, out var position) && CatalogBounds(item.UiId, position).Contains(point));
        if (troop is not null) return troop;
        return AvailableBuildingItems()
            .Where(item => BuildingSlots().TryGetValue(item.UiId, out var position) && CatalogBounds(item.UiId, position).Contains(point))
            // Advanced variants deliberately overlap their base control in
            // `maine`; prefer the currently legal record at that location.
            .OrderByDescending(item => PurchaseEligibilityFor(item) == PurchaseEligibility.Available)
            .ThenByDescending(item => item.Id)
            .FirstOrDefault();
    }

    private WorldEntity? ProductionAnchorFor(DependencyDefinition troop)
    {
        if (_scenarioSimulation is null) return null;
        var selectedIds = _selectedEntityInstanceIds;
        // The current engine still needs an anchor while native troop-create
        // coordinates are being traced. Prefer a matching selected structure,
        // otherwise choose the earliest live completed prerequisite structure
        // deterministically. This choice is intentionally port-owned.
        return _scenarioSimulation.Actors
            .Where(actor => !actor.IsDestroyed && actor.Seed.Team == _localPlayerTeam && actor.Definition.MovementSpeed <= 0)
            .Select(actor => actor.Seed)
            .Where(structure => troop.PrerequisiteItemIds.Any(itemId => StructureMatchesBuildItem(structure, itemId)))
            .OrderByDescending(structure => selectedIds.Contains(structure.InstanceId))
            .ThenBy(structure => structure.InstanceId)
            .FirstOrDefault();
    }

    private IEnumerable<DependencyDefinition> AvailableBuildingItems()
    {
        if (_dependencyCatalog is null) return [];
        return _dependencyCatalog.Items.Values
            .Where(item => item.IsBuilding && item.BuildingFaction == (_grayRace ? 1 : 0))
            .OrderBy(item => item.Id);
    }

    private IEnumerable<DependencyDefinition> AvailableTroopItems()
    {
        if (_dependencyCatalog is null || _entityCatalog is null) return [];
        return _dependencyCatalog.Items.Values
            .Where(item => item.IsTroop && item.TroopEntityId is { } troopId && (uint)troopId < (uint)_entityCatalog.Entities.Count)
            .Where(item => _entityCatalog[item.TroopEntityId!.Value].Faction == (_grayRace ? 1 : 0))
            .OrderBy(item => item.Id);
    }

    private PurchaseEligibility PurchaseEligibilityFor(DependencyDefinition item) =>
        _scenarioSimulation?.EconomyForTeam(_localPlayerTeam)?.Evaluate(_dependencyCatalog, item.Id) ?? PurchaseEligibility.CatalogUnavailable;

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

    private IEnumerable<DependencyDefinition> TroopItemsForStructure(WorldEntity structure)
    {
        if (_dependencyCatalog is null || _entityCatalog is null) return [];
        return _dependencyCatalog.Items.Values
            .Where(item => item.IsTroop && item.TroopEntityId is { } troopId && (uint)troopId < (uint)_entityCatalog.Entities.Count)
            .Where(item => _entityCatalog[item.TroopEntityId!.Value].Faction == (_grayRace ? 1 : 0))
            .Where(item => item.PrerequisiteItemIds.Any(prerequisite => StructureMatchesBuildItem(structure, prerequisite)))
            .OrderBy(item => item.Id);
    }

    private bool StructureMatchesBuildItem(WorldEntity structure, int itemId)
    {
        if (_dependencyCatalog?.TryGet(itemId, out var item) != true || !item.IsBuilding || _buildingFootprints is null) return false;
        return _buildingFootprints.TryResolveBuildingEntity(item.BuildingFaction!.Value, item.BuildingVariant!.Value, item.BuildingSlot!.Value, out var entityId) &&
               entityId == structure.EntityId;
    }

    private string TroopLabel(DependencyDefinition item) => _entityCatalog is not null && item.TroopEntityId is { } entityId && (uint)entityId < (uint)_entityCatalog.Entities.Count
        ? _entityCatalog[entityId].DisplayName.ToUpperInvariant() : $"UNIT {item.Id}";

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

    private IEnumerable<DependencyDefinition> UpgradeItemsForStructure(WorldEntity structure)
    {
        if (_dependencyCatalog is null || _entityCatalog is null) return [];
        return _dependencyCatalog.Items.Values
            .Where(item => item.IsUpgrade && item.UpgradeEntityId is { } targetId && (uint)targetId < (uint)_entityCatalog.Entities.Count)
            .Where(item => _entityCatalog[item.UpgradeEntityId!.Value].Faction == (_grayRace ? 1 : 0))
            .Where(item => item.PrerequisiteItemIds.Any(prerequisite => StructureMatchesBuildItem(structure, prerequisite)))
            .OrderBy(item => item.Id);
    }

    private static string UpgradeLabel(DependencyDefinition item) => item.ResearchEffect switch
    {
        ResearchEffectKind.Ability => item.UiId == 131 ? "NAPALM" : "VIRUS SAC",
        ResearchEffectKind.WeaponLevel => $"WPN+{item.UpgradeLevel}",
        ResearchEffectKind.ArmorLevel => $"ARM+{item.UpgradeLevel}",
        _ => "RESEARCH",
    };

    // Native `maine` reuses a physical button for the two levels of each
    // research chain. Keep the authored UI IDs as the key, rather than
    // compressing a sorted dependency list into arbitrary first-four slots.
    private static readonly IReadOnlyDictionary<int, Point> HumanResearchSlots = new Dictionary<int, Point>
    {
        [110] = new(518, 112), [111] = new(518, 112), [112] = new(577, 112), [113] = new(577, 112),
        [118] = new(518, 153), [119] = new(518, 153), [120] = new(577, 153), [121] = new(577, 153),
        [114] = new(518, 194), [115] = new(518, 194), [116] = new(577, 194), [117] = new(577, 194),
        [122] = new(518, 235), [123] = new(518, 235), [124] = new(577, 235), [125] = new(577, 235),
        [126] = new(518, 276), [127] = new(518, 276), [128] = new(577, 276), [129] = new(577, 276),
        [130] = new(518, 317), [131] = new(518, 317), [132] = new(577, 317), [133] = new(577, 317),
    };

    private static readonly IReadOnlyDictionary<int, Point> GrayResearchSlots = new Dictionary<int, Point>
    {
        [56] = new(518, 112), [100] = new(518, 112), [99] = new(577, 112), [68] = new(577, 112),
        [58] = new(518, 153), [103] = new(518, 153), [72] = new(577, 153), [104] = new(577, 153),
        [57] = new(518, 194), [101] = new(518, 194), [69] = new(577, 194), [102] = new(577, 194),
        [55] = new(518, 235), [77] = new(518, 235), [66] = new(577, 235), [67] = new(577, 235),
        [59] = new(518, 276), [105] = new(518, 276), [73] = new(577, 276), [106] = new(577, 276),
        [60] = new(518, 317), [78] = new(518, 317), [74] = new(577, 317), [107] = new(577, 317),
    };

    private IReadOnlyList<(DependencyDefinition Item, Point Position)> ResearchButtonsForStructure(WorldEntity structure)
    {
        var slots = _grayRace ? GrayResearchSlots : HumanResearchSlots;
        IReadOnlySet<int> completed = _scenarioSimulation?.EconomyForTeam(_localPlayerTeam)?.CompletedItems ?? new HashSet<int>();
        return UpgradeItemsForStructure(structure)
            .Where(item => slots.ContainsKey(item.UiId))
            .GroupBy(item => slots[item.UiId])
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.UpgradeLevel ?? 0).ThenBy(item => item.Id).ToArray();
                var visible = ordered.FirstOrDefault(item => !completed.Contains(item.Id)) ?? ordered[^1];
                return (Item: visible, Position: slots[visible.UiId]);
            })
            .OrderBy(button => button.Item.Id)
            .ToArray();
    }

    private void DrawResearchCatalog(Graphics graphics)
    {
        var structure = SelectedGameplayEntities().FirstOrDefault(entity => _scenarioSimulation?.Actor(entity.InstanceId)?.Definition.MovementSpeed <= 0);
        if (structure is not null)
        {
            var upgrades = ResearchButtonsForStructure(structure);
            foreach (var (item, position) in upgrades)
                DrawMappedCatalogButton(graphics, CatalogBounds(item.UiId, position), item.UiId, UpgradeLabel(item), PurchaseEligibilityFor(item) == PurchaseEligibility.Available);
            DrawGameplayPanelIdentity(graphics, upgrades.Count == 0 ? "NO RESEARCH" : "RESEARCH");
            return;
        }
        var entries = _grayRace
            ? new[] { ("WEAPON +1", 84), ("ARMOR +1", 55), ("PSYCH +", 89), ("VIRUS SAC", 45) }
            : new[] { ("WEAPON +1", 47), ("ARMOR +1", 48), ("WEAPON +2", 27), ("ARMOR +2", 90) };
        var slots = new[] { new Point(518,112), new Point(577,112), new Point(518,194), new Point(577,194) };
        for (var index = 0; index < entries.Length; index++)
            DrawMappedCatalogButton(graphics, new Rectangle(slots[index], new Size(59, 41)), entries[index].Item2, entries[index].Item1);
        DrawGameplayPanelIdentity(graphics, "UPGRADES");
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
            DrawMappedCatalogButton(graphics, entry.Bounds, entry.Frame, entry.Label, entry.UiId is 62 or 196);
        DrawGameplayPanelIdentity(graphics, _gameplayPaused ? "PAUSED" : "GAME OPTIONS");
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
        DrawGameplayPanelIdentity(graphics, "ALLIES");
    }

    private static Rectangle[] AllianceSlots() =>
    [
        new(518, 112, 59, 41), new(577, 112, 59, 41),
        new(518, 153, 59, 41), new(577, 153, 59, 41),
        new(518, 194, 59, 41), new(577, 194, 59, 41),
        new(518, 235, 59, 41), new(577, 235, 59, 41),
    ];

    private IReadOnlyList<int> AllianceTargetTeams() => _scenarioSimulation?.TeamResources.Keys
        .Where(team => team != _localPlayerTeam)
        .OrderBy(team => team)
        .Take(AllianceSlots().Length - 1)
        .ToArray() ?? [];

    private void DrawMappedCatalogButton(Graphics graphics, Rectangle bounds, int frame, string label, bool available = false)
    {
        DrawGameplayIconButton(graphics, bounds, frame, false, available);
        DrawGameplayHudText(graphics, label, new Rectangle(bounds.X + 2, bounds.Y + 27, bounds.Width - 4, 12));
    }

    private void DrawGameplayCommandButton(
        Graphics graphics,
        GameplayHudButton button,
        bool available,
        GameplayCommandMode? selectedMode = null)
    {
        var hovered = _gameplayPointer is { } pointer && button.Bounds.Contains(pointer);
        DrawGameplayIconButton(graphics, button.Bounds, button.Frame, selectedMode == _gameplayCommandMode || hovered, available);
    }

    private string? HoveredGameplayCommandLabel(IReadOnlyList<GameplayHudButton> buttons)
    {
        if (_gameplayPointer is not { } pointer) return null;
        return buttons.FirstOrDefault(button => button.Bounds.Contains(pointer))?.Label;
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

    private void DrawGameplayPanelIdentity(Graphics graphics, string text)
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

    private void DrawGameplayPanelPrompt(Graphics graphics)
    {
        var prompt = _gameplayCommandMode switch
        {
            GameplayCommandMode.Waypoints => _gameplayHudLayout.SetWaypointsMessage,
            GameplayCommandMode.AttackTarget or GameplayCommandMode.GroundSpecialTarget or GameplayCommandMode.HarvestVent => _gameplayHudLayout.SelectTargetMessage,
            _ => null,
        };
        if (prompt is not null) DrawGameplayPanelIdentity(graphics, prompt);
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

    private Bitmap? SpriteFrameBitmap(string spriteName, int frameIndex)
    {
        var key = $"sprite:{spriteName}:{frameIndex}";
        if (_animationFrames.TryGetValue(key, out var cached)) return cached;
        try
        {
            var sprite = LoadSprite(spriteName);
            if ((uint)frameIndex >= (uint)sprite.Frames.Count) return null;
            var frame = sprite.Frames[frameIndex];
            var bitmap = BitmapFromRgba(frame.Width, frame.Height, sprite.FrameRgba(frameIndex));
            _animationFrames[key] = bitmap;
            return bitmap;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FileNotFoundException)
        {
            _status = $"HUD sprite error: {error.Message}";
            return null;
        }
    }

    private void SetGameplayCommandMode(GameplayCommandMode mode)
    {
        _gameplayCommandMode = mode;
        // Native mode prompts are continuously projected through UI 79 by
        // the gameplay input loop; they are not appended to message history.
        _surface.Invalidate();
    }

    private string GameplayCommandModeLabel() => _gameplayCommandMode switch
    {
        GameplayCommandMode.Waypoints => "WAYPOINT MODE",
        GameplayCommandMode.AttackTarget => "MOVE & ATTACK",
        GameplayCommandMode.GroundSpecialTarget => "SPECIAL GROUND TARGET",
        GameplayCommandMode.HarvestVent => "DEPLOY ON VENT",
        GameplayCommandMode.PlaceBuilding => "DROP BUILDING",
        _ => "MOVE MODE",
    };

    private void DrawOpeningLogo(Graphics graphics)
    {
        var animation = Animation("dcss.fin", "DCSS");
        if (animation is null) return;
        var age = _world.TickCount - _screenStartedAtTick;
        var frame = animation.FirstFrame + (ushort)Math.Min((ulong)(animation.LastFrame - animation.FirstFrame), age / 2);
        DrawAnimationFrame(graphics, "dcss.fin", frame, 130, 0);
    }

    private void DrawInnerMenuAssets(Graphics graphics)
    {
        switch (_screen)
        {
            case MenuScreenId.NewGame:
                // newgamee gadgets 21-26: both race portraits occupy their own
                // 112x232 viewport. The source declares every listed portrait
                // gadget anim_stopped; REZ ranges remain transition evidence.
                DrawStoppedAnimation(graphics, "hcar.fin", "HLOOP", 27, 23);
                DrawStoppedAnimation(graphics, "acar.fin", "ALOOP", 500, 23);
                break;

            case MenuScreenId.SinglePlayer:
                // The native free-war lobby is intrface/multie, over tcpwait,
                // not shumane. CHAA supplies its structural foreground layer;
                // the player, map, and option values below are data-driven.
                DrawStoppedAnimation(graphics, "chaa.fin", "CHAA", 0, 0);
                break;
        }
    }

    private EncyclopediaCatalog? Encyclopedia()
    {
        if (_encyclopedia is not null || _installation is null) return _encyclopedia;
        try
        {
            _encyclopedia = EncyclopediaCatalog.Load(_installation.DataFile("intrface", "encyclo.txt"));
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Encyclopedia error: {error.Message}";
        }

        return _encyclopedia;
    }

    private void DrawEncyclopedia(Graphics graphics)
    {
        var catalog = Encyclopedia();
        if (catalog is null) return;
        var category = catalog.Categories[_encyclopediaCategory];
        _encyclopediaEntry = Math.Clamp(_encyclopediaEntry, 0, category.Entries.Count - 1);
        var selected = category.Entries[_encyclopediaEntry];
        DrawMenuText(graphics, selected.Name.ToUpperInvariant(), new Rectangle(18, 18, 260, 18));
        DrawEncyclopediaArticle(graphics, selected);
        DrawEncyclopediaEntity(graphics, selected);
        DrawEncyclopediaRuntimeStats(graphics, selected);
    }

    private void DrawEncyclopediaRuntimeStats(Graphics graphics, EncyclopediaEntry entry)
    {
        if (_installation is null || !EncyclopediaUnitIdentityCatalog.TryGetEntityId(entry, out var entityId)) return;
        try
        {
            _entityCatalog ??= EntityCatalog.Load(_installation.DataFile("gamestat", "gamestat.txt"));
            if ((uint)entityId >= (uint)_entityCatalog.Entities.Count) return;
            var entity = _entityCatalog[entityId];
            // `encycloe` declares its only nearby in_text at (307,287), 16 chars.
            // Keep this compact and derived from the runtime catalog, leaving
            // the source article prose untouched in its own panel.
            DrawMenuText(graphics, $"HP{entity.Health} SP{entity.MovementSpeed}", new Rectangle(307, 287, 154, 14),
                center: false, remap: Color.FromArgb(91, 203, 0));
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Encyclopedia stat error: {error.Message}";
        }
    }

    private void DrawEncyclopediaArticle(Graphics graphics, EncyclopediaEntry entry)
    {
        if (_installation is null) return;
        try
        {
            if (!_encyclopediaArticles.TryGetValue(entry.ResourceStem, out var article))
            {
                article = EncyclopediaArticle.Load(_installation.RootPath, entry.ResourceStem);
                _encyclopediaArticles.Add(entry.ResourceStem, article);
            }

            var row = 0;
            foreach (var sourceLine in article.Lines)
            {
                foreach (var line in WrapEncyclopediaLine(sourceLine.Text, maximumCharacters: 31))
                {
                    if (row >= 24) return;
                    DrawEncyclopediaArticleLine(graphics, line, sourceLine.Segments, 19, 103 + row * 14);
                    row++;
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FileNotFoundException)
        {
            _status = $"Encyclopedia article error: {error.Message}";
        }
    }

    private static IEnumerable<string> WrapEncyclopediaLine(string source, int maximumCharacters)
    {
        if (source.Length == 0) return [string.Empty];
        var words = source.Split(' ', StringSplitOptions.None);
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (current.Length != 0 && candidate.Length > maximumCharacters)
            {
                lines.Add(current);
                current = word;
            }
            else current = candidate;
        }
        if (current.Length != 0) lines.Add(current);
        return lines;
    }

    private void DrawEncyclopediaArticleLine(Graphics graphics, string line, IReadOnlyList<EncyclopediaArticleSegment> sourceSegments, int x, int y)
    {
        var remaining = line;
        var cursor = x;
        foreach (var segment in sourceSegments)
        {
            if (remaining.Length == 0) break;
            var take = Math.Min(segment.Text.Length, remaining.Length);
            var text = remaining[..take];
            DrawMenuText(graphics, text, new Rectangle(cursor, y, 238 - (cursor - x), 14), center: false,
                remap: EncyclopediaTextColor(segment.PaletteIndex));
            cursor += MenuTextWidth(text);
            remaining = remaining[take..];
        }
    }

    private int MenuTextWidth(string text) => _menuFont?.Measure(text) ?? text.Length * 7;

    private static Color EncyclopediaTextColor(int paletteIndex) => paletteIndex switch
    {
        0 => Color.FromArgb(238, 63, 63),
        1 => Color.FromArgb(55, 142, 255),
        4 => Color.FromArgb(95, 238, 70),
        _ => Color.FromArgb(215, 225, 195),
    };

    private void DrawEncyclopediaEntity(Graphics graphics, EncyclopediaEntry entry)
    {
        var animation = entry.NativeId switch
        {
            0 => ("gray.fin", "GRAYSTAND0"),
            1 => ("atril.fin", "ATRILSTAND0"),
            2 => ("scyth.fin", "SCYTHSTAND0"),
            3 => ("ortu.fin", "ORTUMOVE0"),
            4 => ("psyc.fin", "PSYCSTAND0"),
            5 => ("slug.fin", "SLUGSTAND0"),
            6 => ("xeno.fin", "XENOSTAND0"),
            7 => ("slom.fin", "SLOMSTAND0"),
            8 => ("sauc.fin", "EASY2"),
            9 => ("zisp.fin", "ZISPSTAND0"),
            10 => ("trooper1.fin", "TROOPER1STAND6"),
            11 => ("barr.fin", "BARRSTAND0"),
            12 => ("reap.fin", "REAPSTAND0"),
            13 => ("scgm.fin", "SCGMMOVE0"),
            14 => ("cyborg.fin", "CYBORGSTAND0"),
            15 => ("expl.fin", "EXPLSTAND0"),
            16 => ("turr.fin", "TURRSTAND0"),
            17 => ("engi.fin", "ENGISTAND0"),
            18 => ("drop.fin", "DROPSTAND0"),
            19 => ("beon.fin", "BEONMOVE0"),
            20 => ("tektara.fin", "TEKTARA"),
            21 => ("mactor.fin", "MACTORSTAND0"),
            22 => ("lens.fin", "LENSSTAND0"),
            23 => ("luna.fin", "LUNAMOVE0"),
            24 => ("hyyk.fin", "HYYKDEPLOY0"),
            _ => ((string File, string Name)?)null,
        };
        if (animation is { } value)
        {
            DrawEncyclopediaAnimationCentered(
                graphics,
                value.File,
                value.Name,
                new Rectangle(304, 8, 328, 208),
                3);
        }
    }

    private bool DrawEncyclopediaAnimationCentered(
        Graphics graphics,
        string fileName,
        string animationName,
        Rectangle viewport,
        ulong ticksPerFrame)
    {
        var animation = EncyclopediaFacingAnimation(fileName, animationName);
        if (animation is null) return false;
        var span = animation.LastFrame - animation.FirstFrame + 1;
        var offset = _encyclopediaPreviewPaused
            ? Math.Clamp(_encyclopediaPreviewFrameOffset, 0, span - 1)
            : (int)((_world.TickCount - _screenStartedAtTick) / ticksPerFrame % (ulong)span);
        var bitmap = AnimationBitmap(fileName, (ushort)(animation.FirstFrame + offset));
        if (bitmap is null) return false;
        var state = graphics.Save();
        graphics.SetClip(viewport);
        graphics.DrawImageUnscaled(bitmap,
            viewport.X + (viewport.Width - bitmap.Width) / 2,
            viewport.Y + (viewport.Height - bitmap.Height) / 2);
        graphics.Restore(state);
        return true;
    }

    private AnimationRange? EncyclopediaFacingAnimation(string fileName, string defaultAnimationName)
    {
        // The normal FIN stand families use named even facing sectors (0..14).
        // LEFT/RIGHT therefore change the selected named family, rather than
        // treating adjacent logical frames as a fake time animation. Some
        // shipped names contain typos or incomplete sectors, so fall back to
        // the entry's documented default when an exact direction is absent.
        var trailingDigit = defaultAnimationName.Length - 1;
        while (trailingDigit >= 0 && char.IsAsciiDigit(defaultAnimationName[trailingDigit])) trailingDigit--;
        if (trailingDigit == defaultAnimationName.Length - 1) return Animation(fileName, defaultAnimationName);
        var prefix = defaultAnimationName[..(trailingDigit + 1)];
        var desiredName = $"{prefix}{_encyclopediaPreviewFacingIndex * 2}";
        return Animation(fileName, desiredName) ?? Animation(fileName, defaultAnimationName);
    }

    private bool DrawLoopAnimationCentered(
        Graphics graphics,
        string fileName,
        string animationName,
        Rectangle viewport,
        ulong ticksPerFrame)
    {
        var animation = Animation(fileName, animationName);
        if (animation is null) return false;
        var span = animation.LastFrame - animation.FirstFrame + 1;
        var age = _world.TickCount - _screenStartedAtTick;
        var frame = animation.FirstFrame + (ushort)((age / ticksPerFrame) % (ulong)span);
        var bitmap = AnimationBitmap(fileName, frame);
        if (bitmap is null) return false;

        var state = graphics.Save();
        graphics.SetClip(viewport);
        graphics.DrawImageUnscaled(
            bitmap,
            viewport.X + (viewport.Width - bitmap.Width) / 2,
            viewport.Y + (viewport.Height - bitmap.Height) / 2);
        graphics.Restore(state);
        return true;
    }

    private bool DrawLoopAnimation(
        Graphics graphics,
        string fileName,
        string animationName,
        int x,
        int y,
        ulong ticksPerFrame)
    {
        var animation = Animation(fileName, animationName);
        if (animation is null) return false;
        var span = animation.LastFrame - animation.FirstFrame + 1;
        var age = _world.TickCount - _screenStartedAtTick;
        var frame = animation.FirstFrame + (ushort)((age / ticksPerFrame) % (ulong)span);
        return DrawAnimationFrame(graphics, fileName, frame, x, y);
    }

    private bool DrawStoppedAnimation(Graphics graphics, string fileName, string animationName, int x, int y)
    {
        var animation = Animation(fileName, animationName);
        return animation is not null && DrawAnimationFrame(graphics, fileName, animation.FirstFrame, x, y);
    }

    private void DrawButton(Graphics graphics, MenuButton button, int sequenceIndex)
    {
        var hovered = _hoveredButton == button.Id;
        var pressed = _pressedButton == button.Id;
        var bright = button.Selected || hovered;
        var artName = button.ArtName ?? (button.Bounds.Width >= 170 ? "LARGEBUTTON" : "MEDBUTTON");
        var art = Animation("knobe.fin", artName);
        var drewOriginal = false;
        if (art is not null)
        {
            ushort frame;
            if (_screen == MenuScreenId.Main)
            {
                var age = (long)(_world.TickCount - _screenStartedAtTick) - sequenceIndex * 2L;
                // Several one-off ranges end with an empty FIN sentinel.
                // LARGE/MED/SMALL BUTTON and UP/DOWN all have their final
                // visible frame immediately before that sentinel.
                var lastVisible = LastVisibleFrame(art);
                frame = (ushort)(art.FirstFrame + Math.Clamp(age, 0L, (long)(lastVisible - art.FirstFrame)));
            }
            else if (pressed || bright)
            {
                frame = (ushort)Math.Max(art.FirstFrame, LastVisibleFrame(art) - 1);
            }
            else frame = art.FirstFrame;
            drewOriginal = DrawAnimationFrame(
                graphics,
                "knobe.fin",
                frame,
                button.Bounds.X,
                button.Bounds.Y,
                remapWarControlPalette: _screen == MenuScreenId.SinglePlayer);
        }

        if (!drewOriginal)
        {
            using var fill = new SolidBrush(Color.FromArgb(pressed ? 210 : bright ? 175 : 125, 8, 17, 12));
            using var border = new Pen(pressed ? Color.FromArgb(110, 160, 90) : bright ? Color.FromArgb(155, 230, 115) : Color.FromArgb(76, 118, 72));
            graphics.FillRectangle(fill, button.Bounds);
            graphics.DrawRectangle(border, button.Bounds.X, button.Bounds.Y, button.Bounds.Width - 1, button.Bounds.Height - 1);
        }

        var warButtonText = _screen == MenuScreenId.SinglePlayer ? Color.FromArgb(159, 19, 19) : (Color?)null;
        if (!DrawMenuText(graphics, button.Label, button.Bounds, remap: warButtonText))
        {
            using var font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(pressed ? Color.FromArgb(145, 170, 135) : Color.FromArgb(205, 226, 195));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(button.Label, font, brush, button.Bounds, format);
        }
    }

    private bool DrawMenuText(Graphics graphics, string text, Rectangle bounds, bool center = true, Color? remap = null)
    {
        if (_installation is null) return false;
        try
        {
            _menuFont ??= new BitmapFont(
                Sprite.Load(_installation.DataFile("intrface", "mfonto5.spr")),
                frameOffset: 31,
                lineHeight: 14);

            var cursor = center ? bounds.X + (bounds.Width - _menuFont.Measure(text)) / 2 : bounds.X;
            var lineTop = bounds.Y + (bounds.Height - _menuFont.LineHeight) / 2;
            foreach (var character in text)
            {
                var frameIndex = _menuFont.FrameIndex(character);
                var frame = _menuFont.Glyph(character);
                if (frame is null) continue;
                if (frame.Width != 0 && frame.Height != 0)
                {
                    Bitmap? bitmap;
                    var cacheKey = remap is { } color ? $"{frameIndex}:{color.ToArgb()}" : string.Empty;
                    var found = remap is null
                        ? _fontGlyphs.TryGetValue(frameIndex, out bitmap)
                        : _remappedFontGlyphs.TryGetValue(cacheKey, out bitmap);
                    if (!found)
                    {
                        var rgba = _menuFont.Sprite.FrameRgba(frameIndex);
                        if (remap is { } tint)
                        {
                            rgba = (byte[])rgba.Clone();
                            for (var pixel = 0; pixel < rgba.Length; pixel += 4)
                            {
                                if (rgba[pixel + 3] == 0) continue;
                                rgba[pixel] = tint.R;
                                rgba[pixel + 1] = tint.G;
                                rgba[pixel + 2] = tint.B;
                            }
                        }
                        bitmap = BitmapFromRgba(frame.Width, frame.Height, rgba);
                        if (remap is null) _fontGlyphs[frameIndex] = bitmap;
                        else _remappedFontGlyphs[cacheKey] = bitmap;
                    }

                    if (_activeCanvas is { } canvas)
                        canvas.DrawForeground(GpuBitmap(bitmap!), cursor + frame.AnchorX, lineTop + frame.AnchorY);
                    else
                        graphics.DrawImageUnscaled(bitmap!, cursor + frame.AnchorX, lineTop + frame.AnchorY);
                }

                cursor += _menuFont.Advance(character);
            }

            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Font error: {error.Message}";
            return false;
        }
    }

    private AnimationRange? Animation(string fileName, string animationName)
    {
        if (_installation is null) return null;
        try
        {
            if (!_animationDefinitions.TryGetValue(fileName, out var definition))
            {
                definition = AnimationDefinition.Load(_installation.DataFile("animate", fileName));
                _animationDefinitions[fileName] = definition;
            }

            return definition.Animations.FirstOrDefault(item => item.Name == animationName);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private bool DrawAnimationFrame(
        Graphics graphics,
        string fileName,
        int frameIndex,
        int x,
        int y,
        float opacity = 1f,
        bool remapWarControlPalette = false)
    {
        var bitmap = AnimationBitmap(fileName, frameIndex, remapWarControlPalette);
        if (bitmap is null) return false;
        // Interface source rectangles already specify the gadget origin. FIN
        // layer offsets were consumed while composing/cropping the bitmap and
        // must not be applied a second time here.
        if (opacity >= 1f && _activeCanvas is { } canvas)
            canvas.DrawForeground(GpuBitmap(bitmap), x, y);
        else if (opacity >= 1f) graphics.DrawImageUnscaled(bitmap, x, y);
        else
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
            graphics.DrawImage(bitmap, new Rectangle(x, y, bitmap.Width, bitmap.Height), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
        }
        return true;
    }

    private Bitmap? AnimationBitmap(string fileName, int frameIndex, bool remapWarControlPalette = false)
    {
        if (_installation is null) return null;
        var key = $"{fileName}:{frameIndex}:{(remapWarControlPalette ? "war-controls" : "base")}";
        try
        {
            if (_animationFrames.TryGetValue(key, out var cached)) return cached;
            if (!_animationDefinitions.TryGetValue(fileName, out var definition))
            {
                definition = AnimationDefinition.Load(_installation.DataFile("animate", fileName));
                _animationDefinitions[fileName] = definition;
            }

            var composite = definition.Compose(frameIndex, LoadSprite);
            if (remapWarControlPalette && fileName.Equals("knobe.fin", StringComparison.OrdinalIgnoreCase))
            {
                // `multie` uses the same knobe sprites as the green menus,
                // but dc.exe applies its UI palette bank before drawing them.
                // The captured native War screen maps the three base knobe
                // shades (12,36,0 / 28,77,0 / 48,117,0) to these exact
                // neutral/red shades.  Replace only those source colors:
                // recoloring every opaque pixel would destroy the composite's
                // black, grey, cyan, and glyph details.
                RemapWarControlPalette(composite.Rgba);
            }
            var bitmap = BitmapFromRgba(composite);
            _animationFrames[key] = bitmap;
            _animationOrigins[key] = new Point(composite.X, composite.Y);
            return bitmap;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentOutOfRangeException)
        {
            _status = $"Animation error: {error.Message}";
            return null;
        }
    }

    private static void RemapWarControlPalette(byte[] rgba)
    {
        for (var pixel = 0; pixel < rgba.Length; pixel += 4)
        {
            if (rgba[pixel + 3] == 0) continue;
            if (rgba[pixel] == 12 && rgba[pixel + 1] == 36 && rgba[pixel + 2] == 0)
            {
                rgba[pixel] = 7;
                rgba[pixel + 1] = 7;
                rgba[pixel + 2] = 7;
            }
            else if (rgba[pixel] == 28 && rgba[pixel + 1] == 77 && rgba[pixel + 2] == 0)
            {
                rgba[pixel] = 65;
                rgba[pixel + 1] = 8;
                rgba[pixel + 2] = 0;
            }
            else if (rgba[pixel] == 48 && rgba[pixel + 1] == 117 && rgba[pixel + 2] == 0)
            {
                rgba[pixel] = 175;
                rgba[pixel + 1] = 11;
                rgba[pixel + 2] = 15;
            }
        }
    }

    private Rectangle AnimationOpaqueBounds(string key, Bitmap bitmap)
    {
        if (_animationOpaqueBounds.TryGetValue(key, out var cached)) return cached;
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A == 0) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        var bounds = right < left
            ? new Rectangle(0, 0, bitmap.Width, bitmap.Height)
            : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        _animationOpaqueBounds[key] = bounds;
        return bounds;
    }

    private Sprite LoadSprite(string name)
    {
        if (_installation is null) throw new InvalidOperationException("Original data is not available.");
        if (_sprites.TryGetValue(name, out var cached)) return cached;
        foreach (var directory in new[] { "sprites", "intrface" })
        {
            var path = _installation.DataFile(directory, $"{name}.spr");
            if (!File.Exists(path)) continue;
            var sprite = Sprite.Load(path);
            _sprites[name] = sprite;
            return sprite;
        }

        throw new FileNotFoundException($"Missing animation sprite {name}.spr.");
    }

    private static Bitmap BitmapFromRgba(CompositeFrame frame)
        => BitmapFromRgba(frame.Width, frame.Height, frame.Rgba);

    private static Bitmap BitmapFromRgba(int width, int height, byte[] rgba)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[rgba.Length];
            for (var index = 0; index < rgba.Length; index += 4)
            {
                bgra[index] = rgba[index + 2];
                bgra[index + 1] = rgba[index + 1];
                bgra[index + 2] = rgba[index];
                bgra[index + 3] = rgba[index + 3];
            }

            Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private void DrawPanelText(Graphics graphics, string text, Rectangle bounds)
    {
        if (_activeCanvas is { } canvas)
        {
            var panelFill = Color.FromArgb(140, 0, 0, 0);
            var panelBorder = Color.FromArgb(85, 125, 80);
            canvas.FillForeground(bounds, panelFill);
            canvas.FillForeground(new Rectangle(bounds.X, bounds.Y, bounds.Width, 1), panelBorder);
            canvas.FillForeground(new Rectangle(bounds.X, bounds.Bottom - 1, bounds.Width, 1), panelBorder);
            canvas.FillForeground(new Rectangle(bounds.X, bounds.Y, 1, bounds.Height), panelBorder);
            canvas.FillForeground(new Rectangle(bounds.Right - 1, bounds.Y, 1, bounds.Height), panelBorder);
            DrawMenuText(graphics, text, bounds, remap: Color.FromArgb(175, 200, 170));
            return;
        }
        using var fill = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
        using var border = new Pen(Color.FromArgb(85, 125, 80));
        using var font = new Font(FontFamily.GenericMonospace, 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(175, 200, 170));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(border, bounds);
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private void DrawMissionBriefing(Graphics graphics)
    {
        // storye's scroll control is x=610/y=48/h=353. Its content area is
        // therefore deliberately kept clear of the native arrows at right.
        var lines = MissionBriefingLines();
        const int firstY = 48;
        const int lineHeight = 14;
        const int visibleLines = 25;
        for (var row = 0; row < visibleLines; row++)
        {
            var index = _briefingScrollLine + row;
            if ((uint)index >= (uint)lines.Count) break;
            DrawMenuText(graphics, lines[index], new Rectangle(60, firstY + row * lineHeight, 530, lineHeight),
                center: false, remap: Color.FromArgb(175, 220, 155));
        }
    }

    private IReadOnlyList<string> MissionBriefingLines()
    {
        var source = _missionText?.Briefing ?? string.Empty;
        var lines = new List<string>();
        foreach (var paragraph in source.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var words = paragraph.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var line = string.Empty;
            foreach (var word in words)
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                // The source story panel is 530 logical pixels wide. The
                // shipped bitmap font averages below eight pixels here; a
                // conservative 66-character wrap prevents clipping while
                // preserving the original authored newlines.
                if (candidate.Length > 66 && line.Length != 0)
                {
                    lines.Add(line);
                    line = word;
                }
                else line = candidate;
            }
            if (line.Length != 0) lines.Add(line);
        }

        return lines;
    }

    private void DrawSinglePlayerMapSelection(Graphics graphics)
    {
        EnsureSinglePlayerMaps();
        var maps = _singlePlayerMaps;
        if (maps.Count == 0)
        {
            DrawMenuText(graphics, "NO WAR MAPS", new Rectangle(29, 200, 535, 18), center: false);
            return;
        }

        const int rowHeight = 14;
        const int visibleRows = 8;
        const int listTop = 200;
        const int listHeight = 114;
        _singlePlayerMapIndex = Math.Clamp(_singlePlayerMapIndex, 0, maps.Count - 1);
        var first = Math.Clamp(_singlePlayerMapIndex - visibleRows / 2, 0, Math.Max(0, maps.Count - visibleRows));
        var listBounds = new Rectangle(29, listTop, 535, listHeight);
        var state = graphics.Save();
        graphics.SetClip(listBounds);
        for (var row = 0; row < visibleRows && first + row < maps.Count; row++)
        {
            var index = first + row;
            var bounds = new Rectangle(29, listTop + row * rowHeight, 535, rowHeight);
            if (index == _singlePlayerMapIndex)
            {
                using var highlight = new SolidBrush(Color.FromArgb(85, 0, 255, 255));
                graphics.FillRectangle(highlight, bounds);
            }
            DrawMenuText(graphics, maps[index].DisplayName, new Rectangle(29, bounds.Y, 280, rowHeight), center: false);
            DrawMenuText(graphics, WarMapDescription(maps[index].Stem), new Rectangle(310, bounds.Y, 250, rowHeight), center: false);
        }
        graphics.Restore(state);

        DrawSinglePlayerScrollThumb(graphics, maps.Count, first, visibleRows);
        DrawWarLobbyPlayers(graphics);
        DrawWarLobbyOptions(graphics);
    }

    private static void DrawSinglePlayerScrollThumb(Graphics graphics, int mapCount, int first, int visibleRows)
    {
        var track = new Rectangle(596, 227, 10, 61); // multie scroll 30
        var maximumFirst = Math.Max(0, mapCount - visibleRows);
        var thumbHeight = Math.Clamp(track.Height * visibleRows / Math.Max(visibleRows, mapCount), 10, track.Height);
        var travel = track.Height - thumbHeight;
        var top = track.Y + (maximumFirst == 0 ? 0 : travel * first / maximumFirst);
        using var fill = new SolidBrush(Color.FromArgb(190, 235, 25, 25));
        using var edge = new Pen(Color.FromArgb(230, 245, 80, 80));
        graphics.FillRectangle(fill, track.X + 1, top, track.Width - 2, thumbHeight);
        graphics.DrawRectangle(edge, track.X, top, track.Width - 1, thumbHeight - 1);
    }

    private void DrawWarLobbyPlayers(Graphics graphics)
    {
        var headings = new[] { ("Type", 36, 102), ("Race", 141, 102), ("Name", 246, 160), ("Color", 409, 84), ("Team", 496, 84), ("Ready", 577, 60) };
        foreach (var (label, x, width) in headings) DrawMenuText(graphics, label, new Rectangle(x, 3, width, 12), center: false);
        var warGreen = Color.FromArgb(91, 203, 0);
        for (var index = 0; index < _warLobbyPlayers.Length; index++)
        {
            var player = _warLobbyPlayers[index];
            var y = 21 + index * 19;
            var typeColor = player.Type == WarLobbyPlayerType.Human ? Color.FromArgb(79, 7, 7) : warGreen;
            DrawMenuText(graphics, WarLobbyTypeLabel(player.Type), new Rectangle(45, y, 50, 16), remap: typeColor);
            DrawMenuText(graphics, player.Gray ? "Gray" : "Human", new Rectangle(150, y, 50, 16), remap: warGreen);
            var name = index == 0 && !string.IsNullOrWhiteSpace(_leaderName) ? _leaderName : player.Name;
            DrawMenuText(graphics, name, new Rectangle(246, y, 160, 16), center: false, remap: warGreen);
            var type = Animation("knobe.fin", "PLAYERTYPE");
            if (type is not null) DrawAnimationFrame(graphics, "knobe.fin", type.FirstFrame + (int)player.Type, 99, y);
            var opacity = player.Type == WarLobbyPlayerType.None ? 0.32f : 1f;
            var race = Animation("knobe.fin", "RACEFACE");
            if (race is not null) DrawAnimationFrame(graphics, "knobe.fin", race.FirstFrame + (player.Gray ? 1 : 0), 204, y, opacity);
            var colors = Animation("knobe.fin", "CUBE");
            if (colors is not null) DrawAnimationFrame(graphics, "knobe.fin", colors.FirstFrame + player.Color, 425, y + 2, opacity);
            var teams = Animation("knobe.fin", "TEAMS");
            if (teams is not null) DrawAnimationFrame(graphics, "knobe.fin", teams.FirstFrame + player.Team, 512, y + 2, opacity);
            DrawWarReadyCheckbox(graphics, new Rectangle(610, y - 3, 27, 17), player.Ready, opacity);
        }
    }

    private void DrawWarReadyCheckbox(Graphics graphics, Rectangle bounds, bool selected, float opacity)
    {
        // `multie` declares checkb 16-23 at these bounds. It is a distinct
        // widget from the player-row CHAB masks, so model the checkbox itself
        // rather than treating readiness as a floating text glyph.
        using var fill = new SolidBrush(Color.FromArgb((int)(opacity * 255), 0, 0, 0));
        using var edge = new Pen(Color.FromArgb((int)(opacity * 255), 65, 65, 65));
        using var inset = new Pen(Color.FromArgb((int)(opacity * 255), selected ? 175 : 35, selected ? 11 : 35, selected ? 15 : 35));
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(edge, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        var marker = new Rectangle(bounds.X + 8, bounds.Y + 3, 11, 11);
        graphics.DrawRectangle(inset, marker.X, marker.Y, marker.Width - 1, marker.Height - 1);
        if (selected) DrawMenuText(graphics, "✓", marker, remap: Color.FromArgb(255, 31, 31));
    }

    private void DrawWarLobbyOptions(Graphics graphics)
    {
        DrawOptionRow(graphics, "Storage Cells", 326, _warStorageCells, ["OFF", "LOW", "MED", "HIGH"]);
        DrawOptionRow(graphics, "Artifacts", 344, _warArtifacts, ["OFF", "LOW", "MED", "HIGH"]);
        DrawOptionRow(graphics, "Erupting Vents", 362, _warEruptingVents ? 1 : 0, ["OFF", "ON"], 538);
        DrawOptionRow(graphics, "Renewable Vents", 380, _warRenewableVents ? 1 : 0, ["OFF", "ON"], 538);
        DrawMultiplierRow(graphics, "P7 Quantity Multiplier", 400, _warP7QuantityMultiplier);
        DrawMultiplierRow(graphics, "P7 Flow Multiplier", 418, _warP7FlowMultiplier);
        var rank = _warLobbyPlayers.FirstOrDefault(player => player.Type == WarLobbyPlayerType.Human)?.Gray == true
            ? new[] { "XIMAL.", "IDRAC.", "SITRUC.", "REGLIA." }[_warCommanderRank]
            : new[] { "LEUT.", "CAPT.", "MAJ.", "COL." }[_warCommanderRank];
        DrawMenuText(graphics, "Commander Rank", new Rectangle(332, 435, 207, 18), center: false, remap: Color.FromArgb(91, 203, 0));
        DrawMenuText(graphics, rank, new Rectangle(539, 436, 64, 16));
        DrawLobbyArrow(graphics, "LEFT", 521, 435);
        DrawLobbyArrow(graphics, "RIGHT", 603, 435);
    }

    private void DrawOptionRow(Graphics graphics, string label, int y, int selected, string[] values, int start = 456)
    {
        var labelColor = Color.FromArgb(91, 203, 0);
        var valueColor = Color.FromArgb(255, 31, 31);
        DrawMenuText(graphics, label, new Rectangle(332, y, start - 332, 18), center: false, remap: labelColor);
        for (var index = 0; index < values.Length; index++)
        {
            var bounds = new Rectangle(start + index * 41, y, 41, 18);
            var button = Animation("knobe.fin", "BUTTON");
            if (button is not null)
                DrawAnimationFrame(
                    graphics,
                    "knobe.fin",
                    index == selected ? LastVisibleFrame(button) : button.FirstFrame,
                    bounds.X,
                    bounds.Y,
                    remapWarControlPalette: true);
            DrawMenuText(graphics, values[index], bounds, remap: valueColor);
        }
    }

    private void DrawMultiplierRow(Graphics graphics, string label, int y, int value)
    {
        DrawMenuText(graphics, label, new Rectangle(332, y, 207, 18), center: false, remap: Color.FromArgb(91, 203, 0));
        DrawMenuText(graphics, $"{value}%", new Rectangle(539, y, 64, 18));
        DrawLobbyArrow(graphics, "LEFT", 521, y);
        DrawLobbyArrow(graphics, "RIGHT", 603, y);
    }

    private void DrawLobbyArrow(Graphics graphics, string animationName, int x, int y)
    {
        var animation = Animation("knobe.fin", animationName);
        if (animation is not null && DrawAnimationFrame(
            graphics, "knobe.fin", animation.FirstFrame, x, y, remapWarControlPalette: true)) return;
        DrawMenuText(graphics, animationName == "LEFT" ? "<" : ">", new Rectangle(x, y, 41, 18));
    }

    private static ushort LastVisibleFrame(AnimationRange animation) =>
        animation.LastFrame > animation.FirstFrame ? (ushort)(animation.LastFrame - 1) : animation.FirstFrame;

    private static string WarMapDescription(string stem)
    {
        var players = stem.Length > 1 && char.IsDigit(stem[1]) ? stem[1] : '?';
        var terrain = stem.Length == 0 ? "Unknown" : char.ToLowerInvariant(stem[0]) switch
        {
            'a' => "Atlantis",
            'd' => "Desert",
            'j' => "Jungle",
            _ => "Unknown",
        };
        return $"({players} Player {terrain} Map)";
    }

    private void DrawNewGameLeaderName(Graphics graphics)
    {
        // newgamee in_text 5: 205,308, width 17. The source uses this same
        // persistent leader value that shumane later shows in in_text 5.
        DrawMenuText(graphics, _leaderName, new Rectangle(205, 308, 170, 18), center: false);
    }

    private void SelectSinglePlayerMapAt(Point point)
    {
        const int rowHeight = 14;
        const int visibleRows = 8;
        const int listTop = 200;
        const int listHeight = 114;
        var maps = _singlePlayerMaps;
        if (point.X is < 29 or >= 564 || point.Y is < listTop or >= listTop + listHeight || maps.Count == 0) return;
        _singlePlayerMapIndex = Math.Clamp(_singlePlayerMapIndex, 0, maps.Count - 1);
        var first = Math.Clamp(_singlePlayerMapIndex - visibleRows / 2, 0, Math.Max(0, maps.Count - visibleRows));
        var index = first + (point.Y - listTop) / rowHeight;
        if (index >= maps.Count) return;
        _singlePlayerMapIndex = index;
        _status = $"Single Player War map: {maps[index].DisplayName}.";
    }

    private void SelectSinglePlayerMapFromScroll(Point point)
    {
        var maps = _singlePlayerMaps;
        if (maps.Count == 0) return;
        const int top = 227;
        const int height = 61;
        var fraction = Math.Clamp(point.Y - top, 0, height - 1) / (double)(height - 1);
        _singlePlayerMapIndex = (int)Math.Round(fraction * (maps.Count - 1));
        _status = $"Single Player War map: {maps[_singlePlayerMapIndex].DisplayName}.";
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

    private bool IsVisibleToLocalTeam(WorldEntity entity)
    {
        if (entity.Team == _localPlayerTeam) return true;
        var actor = _scenarioSimulation?.Actor(entity.InstanceId);
        return actor is not null && _scenarioSimulation!.IsActorVisibleToTeam(_localPlayerTeam, actor);
    }

    private void QueueDiagnosticMove(Point point)
    {
        if (_gameplayMap is null || _gameplayPath is null || _scenarioSimulation is null ||
            _entityCatalog is null || _groundOccupancy is null || _alternateOccupancy is null || point.X >= 516 || point.Y >= 458) return;
        if (_gameplayCommandMode == GameplayCommandMode.AttackTarget)
        {
            var targetActor = FindGameplayActorAt(point, locallyControllableOnly: false);
            if (targetActor is not null && _scenarioSimulation.TeamRelations.IsHostile(_localPlayerTeam, targetActor.Team))
            {
                QueueAttackTarget(targetActor);
                return;
            }
        }
        var target = new CellCoordinate((point.X + _cameraX) / 32, (point.Y + _cameraY) / 32);
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
        var selected = GameplayEntities()
            .Where(entity => _selectedEntityInstanceIds.Contains(entity.InstanceId) && IsLocallyControllable(entity))
            .Where(entity => _scenarioSimulation.Actor(entity.InstanceId) is { } actor &&
                _scenarioSimulation.EffectiveDefinition(actor).MovementSpeed > 0)
            .OrderBy(entity => entity.InstanceId)
            .ToArray();
        if (selected.Length == 0) { _status = "Select at least one mobile local team-0 unit."; return; }
        if (_gameplayCommandMode == GameplayCommandMode.AttackTarget)
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

    private string SecondaryCommandHudStatus(UnitSecondary command)
    {
        if (command.RequiredResearchItemId is { } itemId &&
            _scenarioSimulation?.EconomyForTeam(_localPlayerTeam)?.CompletedItems.Contains(itemId) != true)
            return "TECH REQUIRED";
        return command.WeaponId is null ? "EXECUTOR PENDING" : "GROUND TARGET READY";
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

    private static string WarLobbyTypeLabel(WarLobbyPlayerType type) => type switch
    {
        WarLobbyPlayerType.Ai => "AI",
        WarLobbyPlayerType.AiPlus => "AI+",
        WarLobbyPlayerType.Human => "Human",
        _ => "None",
    };

    private bool HandleWarLobbyClick(Point point)
    {
        // Control geometry and value ranges are recovered from intrface/multie
        // and dc.exe's 0x4112dd-0x4116f6 event dispatcher.
        for (var index = 0; index < _warLobbyPlayers.Length; index++)
        {
            var y = 21 + index * 19;
            var player = _warLobbyPlayers[index];
            if (new Rectangle(99, y, 36, 16).Contains(point))
            {
                player.Type = (WarLobbyPlayerType)(((int)player.Type + 1) % 4);
                return true;
            }
            // The source face gadget is 204..230, but treating the adjacent
            // read-only Race label as the same target makes the selected
            // faction discoverable without changing the native visual layout.
            if (new Rectangle(141, y, 89, 22).Contains(point))
            {
                ToggleWarLobbyRace(index);
                return true;
            }
            if (new Rectangle(409, y + 1, 14, 14).Contains(point))
            {
                player.Color = (player.Color + 15) % 16;
                return true;
            }
            if (new Rectangle(473, y + 1, 14, 14).Contains(point))
            {
                player.Color = (player.Color + 1) % 16;
                return true;
            }
            if (new Rectangle(496, y + 1, 14, 14).Contains(point))
            {
                player.Team = (player.Team + 15) % 16;
                return true;
            }
            if (new Rectangle(560, y + 1, 14, 14).Contains(point))
            {
                player.Team = (player.Team + 1) % 16;
                return true;
            }
            if (new Rectangle(610, y, 27, 17).Contains(point))
            {
                player.Ready = !player.Ready;
                return true;
            }
        }

        if (TrySelectWarOption(point, 326, ref _warStorageCells, 4, 456) ||
            TrySelectWarOption(point, 344, ref _warArtifacts, 4, 456)) return true;
        if (TrySelectWarBinaryOption(point, 362, ref _warEruptingVents) ||
            TrySelectWarBinaryOption(point, 380, ref _warRenewableVents)) return true;
        if (TryAdjustWarMultiplier(point, 400, ref _warP7QuantityMultiplier) ||
            TryAdjustWarMultiplier(point, 418, ref _warP7FlowMultiplier)) return true;
        if (point.Y is >= 435 and < 453)
        {
            if (point.X is >= 521 and < 562)
            {
                _warCommanderRank = Math.Max(0, _warCommanderRank - 1);
                return true;
            }
            if (point.X is >= 603 and < 644)
            {
                _warCommanderRank = Math.Min(3, _warCommanderRank + 1);
                return true;
            }
        }
        return false;
    }

    private void ToggleWarLobbyRace(int playerIndex)
    {
        var player = _warLobbyPlayers[playerIndex];
        player.Gray = !player.Gray;
        if (player.Type != WarLobbyPlayerType.Human) return;

        _grayRace = player.Gray;
        EnsureSinglePlayerMaps();
        if (_singlePlayerMaps.Count == 0) return;
        var faction = player.Gray ? 1 : 0;
        if (_singlePlayerMaps[_singlePlayerMapIndex].EnabledTeamForRace(faction) is null)
        {
            var compatible = _singlePlayerMaps
                .Select((scenario, index) => (scenario, index))
                .FirstOrDefault(item => item.scenario.EnabledTeamForRace(faction) is not null);
            _singlePlayerMapIndex = compatible.index;
        }
        _status = $"Player {playerIndex + 1}: {(player.Gray ? "Gray" : "Human")}; map {_singlePlayerMaps[_singlePlayerMapIndex].DisplayName}.";
    }

    private static bool TrySelectWarOption(Point point, int y, ref int selected, int count, int start)
    {
        if (point.Y < y || point.Y >= y + 18 || point.X < start || point.X >= start + count * 41) return false;
        selected = (point.X - start) / 41;
        return true;
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

    private static bool TrySelectWarBinaryOption(Point point, int y, ref bool selected)
    {
        if (point.Y < y || point.Y >= y + 18 || point.X < 538 || point.X >= 620) return false;
        selected = point.X >= 579;
        return true;
    }

    private static bool TryAdjustWarMultiplier(Point point, int y, ref int percent)
    {
        if (point.Y < y || point.Y >= y + 18) return false;
        if (point.X is >= 521 and < 562)
        {
            percent = Math.Max(25, percent - 25);
            return true;
        }
        if (point.X is >= 603 and < 644)
        {
            percent = Math.Min(500, percent + 25);
            return true;
        }
        return false;
    }

    private enum WarLobbyPlayerType { Ai, AiPlus, Human, None }

    private sealed class WarLobbyPlayer
    {
        public WarLobbyPlayerType Type { get; set; }
        public bool Gray { get; set; }
        public string Name { get; init; } = string.Empty;
        public int Color { get; set; }
        public int Team { get; set; }
        public bool Ready { get; set; }

        public static WarLobbyPlayer[] CreateDefault() =>
        [
            new() { Type = WarLobbyPlayerType.Human, Name = "Player", Color = 0, Team = 0 },
            new() { Type = WarLobbyPlayerType.Ai, Gray = true, Color = 1, Team = 1 },
            new() { Type = WarLobbyPlayerType.None, Color = 2, Team = 2 },
            new() { Type = WarLobbyPlayerType.None, Gray = true, Color = 3, Team = 3 },
            new() { Type = WarLobbyPlayerType.None, Color = 4, Team = 4 },
            new() { Type = WarLobbyPlayerType.None, Gray = true, Color = 5, Team = 5 },
            new() { Type = WarLobbyPlayerType.None, Color = 6, Team = 6 },
            new() { Type = WarLobbyPlayerType.None, Gray = true, Color = 7, Team = 7 },
        ];
    }

    private readonly record struct ScenarioChoice(
        string Directory,
        string Name,
        string? DisplayName = null,
        IReadOnlyList<ScenarioTeam>? Teams = null,
        SinglePlayerWarLaunch? WarLaunch = null);
}
