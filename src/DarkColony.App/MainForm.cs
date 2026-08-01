using DarkColony.App.Ui;
using DarkColony.App.Rendering;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;
using DarkColony.Engine.Commands;
using DarkColony.Engine.Movement;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DarkColony.App;

public sealed class MainForm : Form
{
    // Campaigns use team zero. War maps choose their enabled team from the
    // player's selected faction in the decoded SCN roster.
    private int _localPlayerTeam;
    private readonly GameInstallation? _installation;
    private readonly WorldSimulation _world = new();
    private readonly FixedStepClock _clock;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly Direct3DSurface _surface;
    private readonly Dictionary<string, Image> _backgrounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AnimationDefinition> _animationDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> _animationFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Point> _animationOrigins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Rectangle> _animationOpaqueBounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Bitmap> _fontGlyphs = [];
    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.OrdinalIgnoreCase);
    private BitmapFont? _menuFont;
    private EncyclopediaCatalog? _encyclopedia;
    private int _encyclopediaCategory = 1;
    private int _encyclopediaEntry;
    private Bitmap? _terrainPreview;
    private ScenarioWorld? _scenarioWorld;
    private ScenarioSimulation? _scenarioSimulation;
    private TerrainMap? _gameplayMap;
    private BtsTileset? _gameplayTileset;
    private PathRegionMap? _gameplayPath;
    private EntityCatalog? _entityCatalog;
    private EntityAnimationCatalog? _entityAnimations;
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
    private readonly HashSet<int> _selectedEntityInstanceIds = [];
    private int _cameraX = 30 * 32;
    private int _cameraY = 22 * 32;
    private CellCoordinate? _diagnosticMoveTarget;
    private IReadOnlyList<CellCoordinate> _diagnosticPathCells = [];
    private Point? _mapDragStart;
    private Point _mapDragCamera;
    private bool _mapDragged;
    private Point? _selectionDragStart;
    private Point _selectionDragCurrent;
    private Point? _gameplayPointer;
    private bool _singlePlayerScrollDragging;
    private string _status;
    private ulong _screenStartedAtTick;

    public MainForm(GameInstallation? installation)
    {
        _installation = installation;
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
            SetHover(null);
        };
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

        ShowScreen(MenuScreenId.Main);
        _timer.Tick += (_, _) =>
        {
            _clock.Advance(Environment.TickCount64, () =>
            {
                UpdateGameplayEdgeScroll();
                _world.Step();
                _scenarioSimulation?.Step(_world.LastCommands);
            });
            _surface.RenderAndPresent();
        };
        _timer.Start();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData) =>
        HandleNewGameKey(keyData & Keys.KeyCode) || HandleGameplayKey(keyData & Keys.KeyCode) || base.ProcessCmdKey(ref message, keyData);

    private bool HandleNewGameKey(Keys key)
    {
        if (_screen != MenuScreenId.NewGame || key != Keys.Back || _leaderName.Length == 0) return false;
        _leaderName = _leaderName[..^1];
        _surface.Invalidate();
        return true;
    }

    private bool HandleGameplayKey(Keys key)
    {
        if (_screen != MenuScreenId.Gameplay) return false;
        if (key == Keys.Escape && _selectedEntityInstanceIds.Count != 0)
        {
            _selectedEntityInstanceIds.Clear();
            _diagnosticMoveTarget = null;
            _diagnosticPathCells = [];
            _status = "Selection cleared.";
            return true;
        }
        if (key == Keys.F3)
        {
            _showAssetNames = !_showAssetNames;
            _status = $"Gameplay asset names {(_showAssetNames ? "on" : "off")}.";
            return true;
        }
        if (key == Keys.F4)
        {
            _showPathRegions = !_showPathRegions;
            _status = $"PTH region diagnostic {(_showPathRegions ? "on" : "off")}; zero is an unresolved sentinel.";
            return true;
        }
        if (key == Keys.S)
        {
            StopSelectedUnits();
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
            _timer.Dispose();
            _surface.Dispose();
            foreach (var image in _backgrounds.Values) image.Dispose();
            foreach (var image in _animationFrames.Values) image.Dispose();
            foreach (var image in _fontGlyphs.Values) image.Dispose();
            _terrainPreview?.Dispose();
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
        if (screen == MenuScreenId.Gameplay)
        {
            _terrainPreview?.Dispose();
            _terrainPreview = null;
            _scenarioWorld = null;
            _scenarioSimulation = null;
            _gameplayMap = null;
            _gameplayTileset = null;
            _gameplayPath = null;
            _selectedEntityInstanceIds.Clear();
            _diagnosticMoveTarget = null;
            _diagnosticPathCells = [];
            _autonomousEntities = [];
            _groundOccupancy = null;
            _alternateOccupancy = null;
            _cameraX = 30 * 32;
            _cameraY = 22 * 32;
            LoadGameplayScenario();
        }
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
            _ => [],
        };
        _surface.Cursor = screen == MenuScreenId.Gameplay ? Cursors.Cross : Cursors.Hand;
        _surface.Invalidate();
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
    ];

    private void SelectEncyclopediaCategory(int category)
    {
        _encyclopediaCategory = category;
        _encyclopediaEntry = 0;
        ShowScreen(MenuScreenId.Encyclopedia);
    }

    private void MoveEncyclopediaSelection(int delta)
    {
        var category = Encyclopedia()?.Categories[_encyclopediaCategory];
        if (category is null || category.Entries.Count == 0) return;
        _encyclopediaEntry = (_encyclopediaEntry + delta + category.Entries.Count) % category.Entries.Count;
        _status = $"Encyclopedia: {category.Entries[_encyclopediaEntry].Name}";
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
        _selectedScenario = null;
        _localPlayerTeam = 0;
        _status = $"{(_grayRace ? "Gray" : "Human")} {(_training ? "training" : "campaign")} terrain harness.";
        ShowScreen(MenuScreenId.Gameplay);
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
        if (!selected.TryCreateLaunch(faction, out var launch))
        {
            _status = $"{selected.DisplayName} has no enabled {(localPlayer.Gray ? "Gray" : "Human")} team.";
            return;
        }

        _selectedScenario = new ScenarioChoice("mplayer", launch.Stem);
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

    private void RenderFrame(Graphics graphics)
    {
        graphics.Clear(Color.Black);
        var background = Background();
        if (_screen == MenuScreenId.Gameplay)
        {
            DrawGameplayTerrain(graphics);
            DrawGameplayPathRegions(graphics);
            DrawGameplayActors(graphics);
            if (background is not null) DrawGameplayHud(graphics, background);
            // Keep the native gameplay viewport clear. The previous
            // developer-control panel covered the upper-left map area, which
            // the original Single Player War HUD leaves unobstructed.
        }
        else if (background is not null)
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(background, new Rectangle(0, 0, 640, 480));
        }

        if (_screen == MenuScreenId.Main) DrawOpeningLogo(graphics);
        DrawInnerMenuAssets(graphics);
        if (_screen == MenuScreenId.NewGame) DrawNewGameLeaderName(graphics);
        if (_screen == MenuScreenId.SinglePlayer) DrawSinglePlayerMapSelection(graphics);

        for (var index = 0; index < _buttons.Count; index++) DrawButton(graphics, _buttons[index], index);

        if (_screen == MenuScreenId.LoadGame)
        {
            DrawPanelText(graphics, "NO RECOVERED SAVE GAMES", new Rectangle(75, 105, 430, 250));
        }

        else if (_screen == MenuScreenId.Encyclopedia)
        {
            DrawEncyclopedia(graphics);
        }

    }

    private void DrawGameplayTerrain(Graphics graphics)
    {
        if (_installation is null) return;
        try
        {
            if (_gameplayMap is null || _gameplayTileset is null || _gameplayPath is null || _scenarioWorld is null) return;

            if (_terrainPreview is null)
            {
                var image = TerrainRasterizer.RenderViewport(_gameplayMap, _gameplayTileset, _cameraX, _cameraY, 516, 458);
                _terrainPreview = BitmapFromRgba(image.Width, image.Height, image.Rgba);
            }

            graphics.DrawImageUnscaled(_terrainPreview, 0, 0);
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Terrain error: {error.Message}";
        }
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
            var definition = ScenarioDefinition.Load(_installation.DataFile("scenario", scenario.Directory, $"{scenario.Name}.scn"));
            _gameplayMap = TerrainMap.Load(_installation.DataFile("scenario", scenario.Directory, $"{scenario.Name}.map"));
            _gameplayTileset = BtsTileset.Load(_installation.DataFile("scenario", definition.Tileset));
            _gameplayPath = PathRegionMap.Load(
                _installation.DataFile("scenario", scenario.Directory, $"{scenario.Name}.pth"),
                _gameplayMap.Width,
                _gameplayMap.Height);
            var footprints = BuildingFootprintCatalog.Load(_installation.ExecutablePath);
            _scenarioWorld = ScenarioWorld.Create(definition, footprints);
            _entityCatalog ??= EntityCatalog.Load(_installation.DataFile("gamestat", "gamestat.txt"));
            _scenarioSimulation = ScenarioSimulation.Create(definition, _entityCatalog, _gameplayPath, footprints);
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
            _status = $"Loaded {scenario.Directory}\\{scenario.Name}: {_scenarioSimulation.Actors.Count} actors.";
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
        var state = graphics.Save();
        graphics.SetClip(new Rectangle(0, 0, 516, 458));
        using var zero = new SolidBrush(Color.FromArgb(65, 220, 35, 35));
        using var boundary = new Pen(Color.FromArgb(150, 40, 220, 230));
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
            if (region == 0) graphics.FillRectangle(zero, screenX, screenY, 32, 32);
            if (x > 0 && _gameplayPath.RegionAt(new CellCoordinate(x - 1, z)) != region)
                graphics.DrawLine(boundary, screenX, screenY, screenX, screenY + 32);
            if (z > 0 && _gameplayPath.RegionAt(new CellCoordinate(x, z - 1)) != region)
                graphics.DrawLine(boundary, screenX, screenY, screenX + 32, screenY);
        }
        graphics.Restore(state);
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

    private void DrawGameplayActors(Graphics graphics)
    {
        if (_installation is null || _scenarioSimulation is null) return;
        try
        {
            _entityCatalog ??= EntityCatalog.Load(_installation.DataFile("gamestat", "gamestat.txt"));
            _entityAnimations ??= EntityAnimationCatalog.Build(_entityCatalog, _installation.DataFile("animate"));
            var state = graphics.Save();
            graphics.SetClip(new Rectangle(0, 0, 516, 458));
            foreach (var entity in GameplayEntities().OrderBy(entity => ActorPosition(entity).ZRaw).ThenBy(entity => ActorPosition(entity).XRaw))
            {
                if ((uint)entity.EntityId >= (uint)_entityCatalog.Entities.Count) continue;
                var actorState = _scenarioSimulation.Actor(entity.InstanceId);
                var moveSelection = actorState?.Playback is not null
                    ? _entityAnimations.PreferredMove(entity.EntityId, actorState.Facing.RenderSector16)
                    : null;
                var candidate = moveSelection?.Candidate ?? _entityAnimations.Preferred(entity.EntityId);
                if (candidate is null) continue;
                var span = candidate.LastFrame - candidate.FirstFrame + 1;
                var frame = candidate.FirstFrame + (ushort)(((_world.TickCount - _screenStartedAtTick) / 3) % (ulong)span);
                var fileName = Path.GetFileName(candidate.FinPath);
                var bitmap = AnimationBitmap(fileName, frame);
                if (bitmap is null) continue;
                var key = $"{fileName}:{frame}";
                var origin = _animationOrigins.GetValueOrDefault(key);
                var position = ActorPosition(entity);
                var worldX = position.XRaw / 8;
                var worldY = position.ZRaw / 8;
                var screenX = worldX - _cameraX + origin.X;
                var screenY = worldY - _cameraY + origin.Y;
                if (_selectedEntityInstanceIds.Contains(entity.InstanceId))
                {
                    using var selection = new Pen(Color.FromArgb(72, 255, 255), 2);
                    // A FIN logical origin is not consistently the visible
                    // feet of its composed sprite. Anchor the provisional
                    // ground indicator to the frame's opaque visual base so
                    // it stays with the unit instead of its abstract cell.
                    var opaque = AnimationOpaqueBounds(key, bitmap);
                    var centerX = screenX + opaque.Left + opaque.Width / 2;
                    var groundY = screenY + opaque.Bottom;
                    graphics.DrawEllipse(selection, centerX - 25, groundY - 12, 50, 20);
                }
                graphics.DrawImageUnscaled(bitmap, screenX, screenY);

                if (_showAssetNames)
                {
                    var definition = _entityCatalog[entity.EntityId];
                    using var font = new Font(FontFamily.GenericMonospace, 8, FontStyle.Regular, GraphicsUnit.Pixel);
                    using var back = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
                    using var text = new SolidBrush(Color.FromArgb(245, 241, 200));
                    var direction = moveSelection is null ? "" : $" · {candidate.AnimationName}{(moveSelection.ExactSector ? "" : $"~s{moveSelection.RequestedSector}")}";
                    var label = $"#{entity.EntityId} {definition.Code} · {fileName}{direction}";
                    var size = graphics.MeasureString(label, font);
                    var labelX = worldX + 5 - _cameraX;
                    var labelY = worldY - 12 - _cameraY;
                    graphics.FillRectangle(back, labelX, labelY, size.Width, size.Height);
                    graphics.DrawString(label, font, text, labelX, labelY);
                }
            }
            if (_diagnosticMoveTarget is { } target)
            {
                if (_diagnosticPathCells.Count > 1)
                {
                    using var pathPen = new Pen(Color.FromArgb(225, 255, 205, 55), 2);
                    var points = _diagnosticPathCells.Select(cell => new Point(
                        cell.X * 32 + 16 - _cameraX,
                        cell.Z * 32 + 16 - _cameraY)).ToArray();
                    graphics.DrawLines(pathPen, points);
                }
                var x = target.X * 32 - _cameraX;
                var y = target.Z * 32 - _cameraY;
                using var marker = new Pen(Color.FromArgb(80, 255, 255), 2);
                graphics.DrawRectangle(marker, x + 3, y + 3, 25, 25);
                graphics.DrawLine(marker, x + 8, y + 16, x + 23, y + 16);
                graphics.DrawLine(marker, x + 16, y + 8, x + 16, y + 23);
            }
            if (_selectionDragStart is { } selectionStart)
            {
                var bounds = Rectangle.FromLTRB(
                    Math.Min(selectionStart.X, _selectionDragCurrent.X), Math.Min(selectionStart.Y, _selectionDragCurrent.Y),
                    Math.Max(selectionStart.X, _selectionDragCurrent.X), Math.Max(selectionStart.Y, _selectionDragCurrent.Y));
                using var selectionFill = new SolidBrush(Color.FromArgb(35, 80, 235, 220));
                using var selectionBorder = new Pen(Color.FromArgb(210, 110, 255, 235));
                graphics.FillRectangle(selectionFill, bounds);
                graphics.DrawRectangle(selectionBorder, bounds);
            }
            graphics.Restore(state);
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Scenario actor error: {error.Message}";
        }
    }

    private static void DrawGameplayHud(Graphics graphics, Image hud)
    {
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
        for (var index = 0; index < category.Entries.Count; index++)
        {
            var bounds = new Rectangle(20, 112 + index * 22, 238, 18);
            if (index == _encyclopediaEntry)
            {
                using var highlight = new SolidBrush(Color.FromArgb(90, 30, 100, 65));
                graphics.FillRectangle(highlight, bounds);
            }

            DrawMenuText(graphics, category.Entries[index].Name.ToUpperInvariant(), bounds);
        }

        var selected = category.Entries[_encyclopediaEntry];
        DrawMenuText(graphics, selected.Name.ToUpperInvariant(), new Rectangle(18, 18, 260, 18));
        DrawEncyclopediaEntity(graphics, selected);
    }

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
            DrawLoopAnimationCentered(
                graphics,
                value.File,
                value.Name,
                new Rectangle(304, 8, 328, 208),
                3);
        }
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
                frame = (ushort)(art.FirstFrame + Math.Clamp(age, 0L, (long)(art.LastFrame - art.FirstFrame)));
            }
            else if (pressed || bright)
            {
                // The FIN range is a one-off construction transition, not an
                // idle loop. The third-to-last frame is the completed outlined
                // control; the terminal frame is the compact stopped state.
                frame = (ushort)Math.Max(art.FirstFrame, art.LastFrame - 2);
            }
            else frame = art.LastFrame;
            drewOriginal = DrawAnimationFrame(graphics, "knobe.fin", frame, button.Bounds.X, button.Bounds.Y);
        }

        if (!drewOriginal)
        {
            using var fill = new SolidBrush(Color.FromArgb(pressed ? 210 : bright ? 175 : 125, 8, 17, 12));
            using var border = new Pen(pressed ? Color.FromArgb(110, 160, 90) : bright ? Color.FromArgb(155, 230, 115) : Color.FromArgb(76, 118, 72));
            graphics.FillRectangle(fill, button.Bounds);
            graphics.DrawRectangle(border, button.Bounds.X, button.Bounds.Y, button.Bounds.Width - 1, button.Bounds.Height - 1);
        }

        if (!DrawMenuText(graphics, button.Label, button.Bounds))
        {
            using var font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(pressed ? Color.FromArgb(145, 170, 135) : Color.FromArgb(205, 226, 195));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(button.Label, font, brush, button.Bounds, format);
        }
    }

    private bool DrawMenuText(Graphics graphics, string text, Rectangle bounds, bool center = true)
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
                    if (!_fontGlyphs.TryGetValue(frameIndex, out var bitmap))
                    {
                        bitmap = BitmapFromRgba(frame.Width, frame.Height, _menuFont.Sprite.FrameRgba(frameIndex));
                        _fontGlyphs[frameIndex] = bitmap;
                    }

                    graphics.DrawImageUnscaled(bitmap, cursor + frame.AnchorX, lineTop + frame.AnchorY);
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

    private bool DrawAnimationFrame(Graphics graphics, string fileName, int frameIndex, int x, int y)
    {
        var bitmap = AnimationBitmap(fileName, frameIndex);
        if (bitmap is null) return false;
        // Interface source rectangles already specify the gadget origin. FIN
        // layer offsets were consumed while composing/cropping the bitmap and
        // must not be applied a second time here.
        graphics.DrawImageUnscaled(bitmap, x, y);
        return true;
    }

    private Bitmap? AnimationBitmap(string fileName, int frameIndex)
    {
        if (_installation is null) return null;
        var key = $"{fileName}:{frameIndex}";
        try
        {
            if (_animationFrames.TryGetValue(key, out var cached)) return cached;
            if (!_animationDefinitions.TryGetValue(fileName, out var definition))
            {
                definition = AnimationDefinition.Load(_installation.DataFile("animate", fileName));
                _animationDefinitions[fileName] = definition;
            }

            var composite = definition.Compose(frameIndex, LoadSprite);
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

    private static void DrawPanelText(Graphics graphics, string text, Rectangle bounds)
    {
        using var fill = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
        using var border = new Pen(Color.FromArgb(85, 125, 80));
        using var font = new Font(FontFamily.GenericMonospace, 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(175, 200, 170));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(border, bounds);
        graphics.DrawString(text, font, brush, bounds, format);
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
        for (var index = 0; index < _warLobbyPlayers.Length; index++)
        {
            var player = _warLobbyPlayers[index];
            var y = 21 + index * 19;
            DrawMenuText(graphics, WarLobbyTypeLabel(player.Type), new Rectangle(45, y, 50, 16));
            DrawMenuText(graphics, player.Gray ? "Gray" : "Human", new Rectangle(150, y, 50, 16));
            var name = index == 0 && !string.IsNullOrWhiteSpace(_leaderName) ? _leaderName : player.Name;
            DrawMenuText(graphics, name, new Rectangle(246, y, 160, 16), center: false);
            var type = Animation("knobe.fin", "PLAYERTYPE");
            if (type is not null) DrawAnimationFrame(graphics, "knobe.fin", type.FirstFrame + (int)player.Type, 99, y);
            var race = Animation("knobe.fin", "RACEFACE");
            if (race is not null) DrawAnimationFrame(graphics, "knobe.fin", race.FirstFrame + (player.Gray ? 1 : 0), 204, y);
            var colors = Animation("knobe.fin", "CUBE");
            if (colors is not null) DrawAnimationFrame(graphics, "knobe.fin", colors.FirstFrame + player.Color, 425, y + 2);
            var teams = Animation("knobe.fin", "TEAMS");
            if (teams is not null) DrawAnimationFrame(graphics, "knobe.fin", teams.FirstFrame + player.Team, 512, y + 2);
            DrawMenuText(graphics, player.Ready ? "✓" : "", new Rectangle(610, y - 3, 27, 17));
        }
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
        DrawMenuText(graphics, "Commander Rank", new Rectangle(332, 435, 207, 18), center: false);
        DrawMenuText(graphics, rank, new Rectangle(539, 436, 64, 16));
    }

    private void DrawOptionRow(Graphics graphics, string label, int y, int selected, string[] values, int start = 456)
    {
        DrawMenuText(graphics, label, new Rectangle(332, y, start - 332, 18), center: false);
        for (var index = 0; index < values.Length; index++)
        {
            var bounds = new Rectangle(start + index * 41, y, 41, 18);
            if (index == selected)
            {
                using var highlight = new SolidBrush(Color.FromArgb(70, 255, 0, 0));
                graphics.FillRectangle(highlight, bounds);
            }
            DrawMenuText(graphics, values[index], bounds);
        }
    }

    private void DrawMultiplierRow(Graphics graphics, string label, int y, int value)
    {
        DrawMenuText(graphics, label, new Rectangle(332, y, 207, 18), center: false);
        DrawMenuText(graphics, $"{value}%", new Rectangle(539, y, 64, 18));
        DrawMenuText(graphics, "◀", new Rectangle(521, y, 18, 18));
        DrawMenuText(graphics, "▶", new Rectangle(603, y, 18, 18));
    }

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
        if (_screen == MenuScreenId.Gameplay && _selectionDragStart is not null && eventArgs.Button.HasFlag(MouseButtons.Left))
        {
            _selectionDragCurrent = eventArgs.Location;
            _surface.Invalidate();
            return;
        }
        if (_screen == MenuScreenId.Gameplay && _mapDragStart is { } start && eventArgs.Button.HasFlag(MouseButtons.Left))
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
        if (_screen == MenuScreenId.Gameplay && eventArgs.Button == MouseButtons.Left && eventArgs.X < 516 && eventArgs.Y < 458)
        {
            if (ModifierKeys.HasFlag(Keys.Shift))
            {
                _selectionDragStart = eventArgs.Location;
                _selectionDragCurrent = eventArgs.Location;
            }
            else
            {
                _mapDragStart = eventArgs.Location;
                _mapDragCamera = new Point(_cameraX, _cameraY);
                _mapDragged = false;
            }
            _surface.Capture = true;
        }
        if (eventArgs.Button != MouseButtons.Left) return;
        _pressedButton = _buttons.LastOrDefault(button => button.Bounds.Contains(eventArgs.Location))?.Id;
        _surface.Invalidate();
    }

    private void SurfaceMouseUp(object? sender, MouseEventArgs eventArgs)
    {
        var wasMapDrag = _mapDragged;
        var wasSinglePlayerScrollDrag = _singlePlayerScrollDragging;
        var selectionStart = _selectionDragStart;
        var selectionBounds = selectionStart is { } start ? Rectangle.FromLTRB(
            Math.Min(start.X, eventArgs.X), Math.Min(start.Y, eventArgs.Y),
            Math.Max(start.X, eventArgs.X), Math.Max(start.Y, eventArgs.Y)) : Rectangle.Empty;
        var wasSelectionDrag = selectionStart is not null && (selectionBounds.Width > 4 || selectionBounds.Height > 4);
        _mapDragStart = null;
        _mapDragged = false;
        _selectionDragStart = null;
        _singlePlayerScrollDragging = false;
        _surface.Capture = false;
        if (wasSinglePlayerScrollDrag)
        {
            _pressedButton = null;
            _surface.Invalidate();
            return;
        }
        var pressed = _pressedButton;
        _pressedButton = null;
        var button = _buttons.LastOrDefault(candidate => candidate.Id == pressed && candidate.Bounds.Contains(eventArgs.Location));
        button?.Action();
        if (button is null && wasSelectionDrag && _screen == MenuScreenId.Gameplay && eventArgs.Button == MouseButtons.Left)
        {
            SelectGameplayActorsInRectangle(selectionBounds);
        }
        else if (button is null && !wasMapDrag && _screen == MenuScreenId.Gameplay && eventArgs.Button == MouseButtons.Left)
        {
            SelectGameplayActor(eventArgs.Location);
        }
        if (button is null && _screen == MenuScreenId.Gameplay && eventArgs.Button == MouseButtons.Right)
        {
            QueueDiagnosticMove(eventArgs.Location);
        }
        if (button is null && _screen == MenuScreenId.Encyclopedia)
        {
            var category = Encyclopedia()?.Categories[_encyclopediaCategory];
            if (category is not null && eventArgs.X is >= 20 and < 258 && eventArgs.Y >= 112)
            {
                var index = (eventArgs.Y - 112) / 22;
                if (index >= 0 && index < category.Entries.Count)
                {
                    _encyclopediaEntry = index;
                    _status = $"Encyclopedia: {category.Entries[index].Name}";
                }
            }
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
        _terrainPreview?.Dispose();
        _terrainPreview = null;
        _surface.Invalidate();
    }

    private void UpdateGameplayEdgeScroll()
    {
        if (_screen != MenuScreenId.Gameplay || _gameplayPointer is not { } pointer || _mapDragStart is not null || _selectionDragStart is not null) return;
        if (pointer.X is < 0 or >= 516 || pointer.Y is < 0 or >= 458) return;
        var x = pointer.X <= 8 ? -16 : pointer.X >= 507 ? 16 : 0;
        var y = pointer.Y <= 8 ? -16 : pointer.Y >= 449 ? 16 : 0;
        if (x != 0 || y != 0) MoveGameplayCamera(x, y);
    }

    private void SelectGameplayActor(Point point)
    {
        if (_scenarioSimulation is null || _entityCatalog is null || _entityAnimations is null || point.X >= 516 || point.Y >= 458)
        {
            _selectedEntityInstanceIds.Clear();
            return;
        }

        WorldEntity? selected = null;
        foreach (var entity in GameplayEntities().OrderBy(entity => ActorPosition(entity).ZRaw).ThenBy(entity => ActorPosition(entity).XRaw))
        {
            if (entity.Team != _localPlayerTeam) continue;
            if (_scenarioSimulation.Actor(entity.InstanceId)?.Definition.MovementSpeed <= 0) continue;
            if ((uint)entity.EntityId >= (uint)_entityCatalog.Entities.Count) continue;
            var actorState = _scenarioSimulation.Actor(entity.InstanceId);
            var moveSelection = actorState?.Playback is not null
                ? _entityAnimations.PreferredMove(entity.EntityId, actorState.Facing.RenderSector16)
                : null;
            var candidate = moveSelection?.Candidate ?? _entityAnimations.Preferred(entity.EntityId);
            if (candidate is null) continue;
            var span = candidate.LastFrame - candidate.FirstFrame + 1;
            var frame = candidate.FirstFrame + (ushort)(((_world.TickCount - _screenStartedAtTick) / 3) % (ulong)span);
            var fileName = Path.GetFileName(candidate.FinPath);
            var bitmap = AnimationBitmap(fileName, frame);
            if (bitmap is null) continue;
            var origin = _animationOrigins.GetValueOrDefault($"{fileName}:{frame}");
            var position = ActorPosition(entity);
            var left = position.XRaw / 8 - _cameraX + origin.X;
            var top = position.ZRaw / 8 - _cameraY + origin.Y;
            var localX = point.X - left;
            var localY = point.Y - top;
            if (localX < 0 || localY < 0 || localX >= bitmap.Width || localY >= bitmap.Height) continue;
            if (bitmap.GetPixel(localX, localY).A != 0) selected = entity;
        }

        var additive = ModifierKeys.HasFlag(Keys.Shift);
        if (!additive) _selectedEntityInstanceIds.Clear();
        if (selected is not null)
        {
            if (additive && !_selectedEntityInstanceIds.Add(selected.InstanceId))
                _selectedEntityInstanceIds.Remove(selected.InstanceId);
            else
                _selectedEntityInstanceIds.Add(selected.InstanceId);
        }
        _status = selected is null
            ? "Selection cleared."
            : $"{_selectedEntityInstanceIds.Count} selected · #{selected.EntityId} {_entityCatalog[selected.EntityId].DisplayName} · team {selected.Team}.";
    }

    private void SelectGameplayActorsInRectangle(Rectangle bounds)
    {
        if (_scenarioSimulation is null) return;
        var selected = GameplayEntities()
            .Where(entity => entity.Team == _localPlayerTeam)
            .Where(entity => _scenarioSimulation.Actor(entity.InstanceId)?.Definition.MovementSpeed > 0)
            .Where(entity =>
            {
                var position = ActorPosition(entity);
                return bounds.Contains(position.XRaw / 8 - _cameraX, position.ZRaw / 8 - _cameraY);
            })
            .Select(entity => entity.InstanceId)
            .ToArray();
        foreach (var instanceId in selected) _selectedEntityInstanceIds.Add(instanceId);
        _status = selected.Length == 0
            ? "No mobile local units in selection box."
            : $"{_selectedEntityInstanceIds.Count} unit(s) selected.";
    }

    private void QueueDiagnosticMove(Point point)
    {
        if (_selectedEntityInstanceIds.Count == 0 || _gameplayMap is null || _gameplayPath is null || _scenarioSimulation is null ||
            _entityCatalog is null || _groundOccupancy is null || _alternateOccupancy is null || point.X >= 516 || point.Y >= 458) return;
        var target = new CellCoordinate((point.X + _cameraX) / 32, (point.Y + _cameraY) / 32);
        if (target.X < 0 || target.Z < 0 || target.X >= _gameplayMap.Width || target.Z >= _gameplayMap.Height) return;
        var selected = GameplayEntities()
            .Where(entity => _selectedEntityInstanceIds.Contains(entity.InstanceId) && entity.Team == _localPlayerTeam)
            .Where(entity => _scenarioSimulation.Actor(entity.InstanceId)?.Definition.MovementSpeed > 0)
            .OrderBy(entity => entity.InstanceId)
            .ToArray();
        if (selected.Length == 0) { _status = "Select at least one mobile local team-0 unit."; return; }
        var appendWaypoint = ModifierKeys.HasFlag(Keys.Shift);
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
    }

    private void StopSelectedUnits()
    {
        if (_scenarioSimulation is null || _selectedEntityInstanceIds.Count == 0) return;
        var stopped = GameplayEntities()
            .Where(entity => _selectedEntityInstanceIds.Contains(entity.InstanceId) && entity.Team == _localPlayerTeam)
            .Where(entity => _scenarioSimulation.Actor(entity.InstanceId)?.Definition.MovementSpeed > 0)
            .OrderBy(entity => entity.InstanceId)
            .ToArray();
        foreach (var entity in stopped)
            _world.Commands.Enqueue(_world.TickCount, _world.TickCount + 1, new StopIntent(entity.InstanceId));
        _diagnosticMoveTarget = null;
        _diagnosticPathCells = [];
        _status = stopped.Length == 0 ? "No mobile local units selected." : $"Stop ordered for {stopped.Length} unit(s).";
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
        foreach (var actor in _scenarioSimulation.Actors) yield return actor.Seed;
    }

    private FixedPointPosition ActorPosition(WorldEntity entity) =>
        _scenarioSimulation?.Actor(entity.InstanceId)?.Movement.VisualPosition ?? entity.Position;

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
            if (new Rectangle(204, y, 27, 16).Contains(point))
            {
                player.Gray = !player.Gray;
                return true;
            }
            if (new Rectangle(409, y, 78, 16).Contains(point))
            {
                player.Color = Math.Clamp((point.X < 448 ? player.Color + 15 : player.Color + 1) % 16, 0, 15);
                return true;
            }
            if (new Rectangle(496, y, 78, 16).Contains(point))
            {
                player.Team = Math.Clamp((point.X < 535 ? player.Team + 15 : player.Team + 1) % 16, 0, 15);
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
            if (point.X is >= 521 and < 539)
            {
                _warCommanderRank = Math.Max(0, _warCommanderRank - 1);
                return true;
            }
            if (point.X is >= 603 and < 621)
            {
                _warCommanderRank = Math.Min(3, _warCommanderRank + 1);
                return true;
            }
        }
        return false;
    }

    private static bool TrySelectWarOption(Point point, int y, ref int selected, int count, int start)
    {
        if (point.Y < y || point.Y >= y + 18 || point.X < start || point.X >= start + count * 41) return false;
        selected = (point.X - start) / 41;
        return true;
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
        if (point.X is >= 521 and < 539)
        {
            percent = Math.Max(25, percent - 25);
            return true;
        }
        if (point.X is >= 603 and < 621)
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
        IReadOnlyList<ScenarioTeam>? Teams = null);
}
