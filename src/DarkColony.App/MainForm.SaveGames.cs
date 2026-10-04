using DarkColony.App.Diagnostics;
using DarkColony.App.Ui;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Simulation;

namespace DarkColony.App;

/// <summary>
/// Saving and loading games. A save holds the scenario, how it was launched,
/// and the command journal (see <see cref="SavedGame"/>); loading recreates
/// the scenario and replays the journal to the saved tick.
/// </summary>
public sealed partial class MainForm
{
    private const int SaveListTop = 110;
    private const int SaveRowHeight = 16;
    private const int SaveListRows = 14;

    // Saves carry a digest every 64 updates, so a replay can point at the first step that differs.
    private const int JournalCheckpointInterval = 64;
    private CommandJournal _journal = new(JournalCheckpointInterval);
    private ReplayPlayer? _replay;
    private SavedGame? _replayGame;
    private bool _pendingIsReplay;

    /// <summary>A saved game to watch from its first update (<c>--replay &lt;file&gt;</c>).</summary>
    public string? ReplayPath { get; init; }
    private SavedGame? _pendingSavedGame;
    private IReadOnlyList<string> _saveFiles = [];
    private int _selectedSave;

    private static string SaveDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DarkColonyPort", "saves");

    /// <summary>The HUD save button: writes the current game next to the earlier saves.</summary>
    private void SaveCurrentGame()
    {
        if (_scenarioSimulation is null) return;
        if (_grantedP7)
        {
            // The journal cannot replay the diagnostic grant, so the save would not verify.
            _status = "Saving is off in a --grant-p7 game.";
            return;
        }
        var scenario = GameplayScenario();
        SavedWarLaunch? war = scenario.WarLaunch is { } launch
            ? new SavedWarLaunch(launch.Race, launch.LocalTeamId, launch.Settings.StorageCells, launch.Settings.Artifacts,
                launch.Settings.EruptingVents, launch.Settings.RenewableVents, launch.Settings.P7QuantityPercent,
                launch.Settings.P7FlowPercent, launch.Settings.CommanderRank, launch.Rows, 0)
            : null;
        SavedCampaign? campaign = _selectedScenario is null ? new SavedCampaign(_grayRace, _training, _campaignMission) : null;
        var saved = _journal.Save(_scenarioSimulation, scenario.Directory, scenario.Name, war, campaign);
        Directory.CreateDirectory(SaveDirectory);
        var file = Path.Combine(SaveDirectory, $"{scenario.Name}-{DateTime.Now:yyyyMMdd-HHmmss}{SavedGame.FileExtension}");
        File.WriteAllText(file, saved.ToJson());
        RuntimeLog.Info($"Saved {scenario.Directory}/{scenario.Name} at tick {saved.Ticks} to {file}");
        _status = $"Game saved: {Path.GetFileName(file)}.";
    }

    /// <summary>Lists the saves, newest first, when the load screen opens.</summary>
    private void RefreshSaveFiles()
    {
        _saveFiles = Directory.Exists(SaveDirectory)
            ? Directory.GetFiles(SaveDirectory, $"*{SavedGame.FileExtension}").OrderByDescending(File.GetLastWriteTimeUtc).Take(SaveListRows).ToArray()
            : [];
        _selectedSave = 0;
    }

    private void DrawSaveList(Graphics graphics)
    {
        if (_saveFiles.Count == 0)
        {
            DrawPanelText(graphics, "NO SAVED GAMES", new Rectangle(75, 105, 430, 250));
            return;
        }
        for (var index = 0; index < _saveFiles.Count; index++)
        {
            var bounds = new Rectangle(75, SaveListTop + index * SaveRowHeight, 430, SaveRowHeight);
            var name = Path.GetFileNameWithoutExtension(_saveFiles[index]).ToUpperInvariant();
            DrawMenuText(graphics, name, bounds, center: false,
                remap: index == _selectedSave ? Color.FromArgb(240, 220, 120) : Color.FromArgb(175, 200, 170));
        }
    }

    private void SelectSaveAt(Point point)
    {
        if (point.X is < 75 or >= 505 || point.Y < SaveListTop) return;
        var index = (point.Y - SaveListTop) / SaveRowHeight;
        if (index < _saveFiles.Count) _selectedSave = index;
    }

    /// <summary>The load button: restores the campaign or War context, then the game.</summary>
    private void LoadSelectedSave()
    {
        if (_saveFiles.Count == 0 || _installation is null) return;
        SavedGame saved;
        try
        {
            saved = SavedGame.FromJson(File.ReadAllText(_saveFiles[_selectedSave]));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            _status = $"Cannot load {Path.GetFileName(_saveFiles[_selectedSave])}: {error.Message}";
            return;
        }
        BeginSavedGame(saved, replay: false);
    }

    /// <summary>Opens <see cref="ReplayPath"/> and plays it from the first update.</summary>
    private void StartReplay()
    {
        if (ReplayPath is null || _installation is null) return;
        try
        {
            BeginSavedGame(SavedGame.FromJson(File.ReadAllText(ReplayPath)), replay: true);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            _status = $"Cannot replay {Path.GetFileName(ReplayPath)}: {error.Message}";
            RuntimeLog.Info(_status);
        }
    }

    private void BeginSavedGame(SavedGame saved, bool replay)
    {
        if (saved.War is { } war)
        {
            var settings = new SinglePlayerWarSettings(war.StorageCells, war.Artifacts, war.EruptingVents, war.RenewableVents,
                war.P7QuantityPercent, war.P7FlowPercent, war.CommanderRank);
            var launch = new SinglePlayerWarLaunch(saved.ScenarioName, saved.ScenarioName, war.Race, war.LocalTeamId, settings);
            // A session launch reruns the same session start from its saved rows.
            EnsureSinglePlayerMaps();
            if (war.Rows is { } rows && _installation is not null &&
                _singlePlayerMaps.FirstOrDefault(map => string.Equals(map.Stem, saved.ScenarioName, StringComparison.OrdinalIgnoreCase)) is { } map &&
                map.TryCreateSession(rows, war.LocalOwner, settings, (_simulationRules ??= SimulationRules.Load(_installation)).RandomTable, out var session))
                launch = session;
            _localPlayerTeam = launch.LocalTeamId;
            _selectedScenario = new ScenarioChoice(saved.ScenarioDirectory, saved.ScenarioName, WarLaunch: launch);
        }
        else if (saved.Campaign is { } campaign)
        {
            _selectedScenario = null;
            _grayRace = campaign.Gray;
            _training = campaign.Training;
            _campaignMission = campaign.Mission;
        }
        else
        {
            _selectedScenario = new ScenarioChoice(saved.ScenarioDirectory, saved.ScenarioName);
        }
        _pendingSavedGame = saved;
        _pendingIsReplay = replay;
        ShowScreen(MenuScreenId.Gameplay);
    }

    /// <summary>Called once the scenario's simulation exists: replays a pending save onto it.</summary>
    private void RestorePendingSave()
    {
        _journal = new CommandJournal(JournalCheckpointInterval);
        _replay = null;
        _replayGame = null;
        if (_pendingSavedGame is not { } saved || _scenarioSimulation is null) return;
        _pendingSavedGame = null;
        if (_pendingIsReplay)
        {
            _replay = new ReplayPlayer(saved);
            _replayGame = saved;
            _status = $"Replaying {saved.ScenarioDirectory}/{saved.ScenarioName}: {saved.Ticks} updates.";
            RuntimeLog.Info(_status);
            return;
        }
        var matches = saved.Replay(_scenarioSimulation);
        _journal = CommandJournal.Resume(saved.Steps, saved.Checkpoints, JournalCheckpointInterval);
        RuntimeLog.Info($"Loaded {saved.ScenarioDirectory}/{saved.ScenarioName} at tick {saved.Ticks}; digest {(matches ? "matches" : "differs")}.");
        _status = matches
            ? $"Game loaded at tick {saved.Ticks}."
            : $"Game loaded at tick {saved.Ticks}, but the replayed state differs from the save (game data changed?).";
    }

    /// <summary>
    /// One update of a replay: the saved commands, never the player's. When
    /// the replay reaches its last update it reports whether every
    /// checkpoint and the end digest matched, then the game pauses.
    /// </summary>
    private void StepReplay()
    {
        if (_replay is not { } replay || _replayGame is not { } game || _scenarioSimulation is null) return;
        replay.Step(_scenarioSimulation);
        if (!replay.Finished(_scenarioSimulation)) return;
        var check = replay.Result(_scenarioSimulation);
        _status = check.Matches
            ? $"Replay finished at update {game.Ticks}: {check.CheckpointsVerified} checkpoints and the end state match."
            : $"Replay diverged at update {check.FirstMismatchTick}.";
        RuntimeLog.Info(_status);
        _replay = null;
        _replayGame = null;
        _gameplayPaused = true;
    }
}
