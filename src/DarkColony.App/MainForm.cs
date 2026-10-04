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

public sealed partial class MainForm : Form
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
    private bool _missionOutcomeReported;
    private MissionOutcome? _bailOutcome;
    private long _bailRequestedAtMilliseconds;
    private int _campaignMission = 1;
    private MissionOutcome? _debriefOutcome;
    private string _debriefText = string.Empty;
    private int _briefingScrollLine;
    private bool _storyReturnsToGameplay;
    private bool _resumeGameplayFromStory;
    private bool _gameplayPausedBeforeStory;
    private TerrainMap? _gameplayMap;
    private BtsTileset? _gameplayTileset;
    private PathRegionMap? _gameplayPath;
    private SimulationRules? _simulationRules;
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
    private string? _lastLoggedStatus;
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
            LogStatusChange();
            _surface.RenderAndPresent();
        };
        _timer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        RuntimeLog.Info($"Window closing: {eventArgs.CloseReason}");
        base.OnFormClosing(eventArgs);
    }

    /// <summary>
    /// The status line carries most recoverable errors (missing data, failed
    /// scenario loads, unimplemented actions); mirror each change to the log.
    /// </summary>
    private void LogStatusChange()
    {
        if (string.Equals(_status, _lastLoggedStatus, StringComparison.Ordinal)) return;
        _lastLoggedStatus = _status;
        RuntimeLog.Info($"Status: {_status}");
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
        RuntimeLog.Info($"Screen {_screen} -> {screen}");
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

    // `0x409c94` subtracts 0x207 from pointer X and 0x5a from pointer Y,
    // then divides against 0x60 by 0x54. Those operands identify this exact
    // native 96x84 interior, inside the top-right HUD well.
    private static readonly Rectangle GameplayMinimapBounds = new(519, 6, 96, 84);

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
}
