using DarkColony.Engine.Audio;
using DarkColony.Engine.Data;
using DarkColony.App.Diagnostics;
using DarkColony.App.Ui;

namespace DarkColony.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        RuntimeLog.Initialize(arguments);
        RuntimeLog.InstallExceptionHandlers();
        FrameProfiler.Initialize(arguments);
        ApplicationConfiguration.Initialize();
        GameInstallation? installation;
        try
        {
            installation = InstallationLocator.Locate(arguments, Environment.CurrentDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.ShowError("Dark Colony data error", error.Message);
            RuntimeLog.Info($"Exiting with code {RuntimeLog.ExitDataError}.");
            return RuntimeLog.ExitDataError;
        }

        RuntimeLog.Info(installation is null
            ? "No original installation found; menus run without game data."
            : $"Original installation: {installation.RootPath}");
        var initialScreen = arguments.Any(argument =>
            argument.Equals("--single-player-war", StringComparison.OrdinalIgnoreCase))
            ? MenuScreenId.SinglePlayer
            : MenuScreenId.Main;
        // --reveal-map starts with the Ctrl+F12 diagnostic on (no fog, every actor drawn).
        var revealMap = arguments.Any(argument => argument.Equals("--reveal-map", StringComparison.OrdinalIgnoreCase));
        // --outcome-after N victory|defeat ends every mission N seconds after it loads.
        var outcomeIndex = Array.FindIndex(arguments, argument => argument.Equals("--outcome-after", StringComparison.OrdinalIgnoreCase));
        int? outcomeAfter = outcomeIndex >= 0 && outcomeIndex + 1 < arguments.Length && int.TryParse(arguments[outcomeIndex + 1], out var seconds) ? seconds : null;
        var forcedVictory = outcomeIndex >= 0 && outcomeIndex + 2 < arguments.Length &&
                            arguments[outcomeIndex + 2].Equals("victory", StringComparison.OrdinalIgnoreCase);
        // Display settings: the port's own (docs/DISPLAY_MODES_PLAN.md). Flags
        // override the file for this run only.
        // --display-settings <file> uses another settings file (tests never touch the player's).
        var displaySettingsIndex = Array.FindIndex(arguments, argument => argument.Equals("--display-settings", StringComparison.OrdinalIgnoreCase));
        var displaySettingsPath = displaySettingsIndex >= 0 && displaySettingsIndex + 1 < arguments.Length
            ? Path.GetFullPath(arguments[displaySettingsIndex + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DarkColonyPort", "display.json");
        var displaySettings = DarkColony.Presentation.DisplaySettings.Load(displaySettingsPath, out var displayProblem)
            .WithArguments(arguments, out var displayFlagProblems);
        if (displayProblem is not null) RuntimeLog.Info($"Display settings: {displayProblem}");
        foreach (var problem in displayFlagProblems) RuntimeLog.Info($"Display flag ignored: {problem}");
        Application.Run(new MainForm(installation, initialScreen)
        {
            DisplaySettings = displaySettings,
            DisplaySettingsPath = displaySettingsPath,
            // --display-cycle N switches display modes N times and exits (leak test).
            DisplayCycles = Array.FindIndex(arguments, argument => argument.Equals("--display-cycle", StringComparison.OrdinalIgnoreCase)) is var cycle and >= 0 &&
                cycle + 1 < arguments.Length && int.TryParse(arguments[cycle + 1], out var cycles) && cycles > 0 ? cycles : null,
            RevealMap = revealMap,
            ForcedOutcomeAfterSeconds = outcomeAfter,
            ForcedVictory = forcedVictory,
            CdImagePath = CdImageLocator.Locate(arguments, installation?.RootPath),
            NoMusic = arguments.Any(argument => argument.Equals("--no-music", StringComparison.OrdinalIgnoreCase)),
            NoVideo = arguments.Any(argument => argument.Equals("--no-video", StringComparison.OrdinalIgnoreCase)),
            // --net-port N hosts and joins network games on port N.
            NetworkPort = Array.FindIndex(arguments, argument => argument.Equals("--net-port", StringComparison.OrdinalIgnoreCase)) is var net and >= 0 &&
                net + 1 < arguments.Length && int.TryParse(arguments[net + 1], out var port) ? port : MainForm.DefaultNetworkPort,
            // --grant-p7 N gives the local player N P7 when a Single Player War starts (diagnostic; no saving).
            GrantP7 = Array.FindIndex(arguments, argument => argument.Equals("--grant-p7", StringComparison.OrdinalIgnoreCase)) is var grant and >= 0 &&
                grant + 1 < arguments.Length && int.TryParse(arguments[grant + 1], out var amount) && amount > 0 ? amount : null,
            // --replay <file.dcsave> watches a saved game from its first update.
            ReplayPath = Array.FindIndex(arguments, argument => argument.Equals("--replay", StringComparison.OrdinalIgnoreCase)) is var replay and >= 0 && replay + 1 < arguments.Length
                ? arguments[replay + 1]
                : null,
            // --load <file.dcsave> opens a saved game at its last update.
            LoadPath = Array.FindIndex(arguments, argument => argument.Equals("--load", StringComparison.OrdinalIgnoreCase)) is var load and >= 0 && load + 1 < arguments.Length
                ? arguments[load + 1]
                : null,
        });
        RuntimeLog.Info("Exited normally.");
        return 0;
    }
}
