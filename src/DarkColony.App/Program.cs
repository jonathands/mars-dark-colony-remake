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
        Application.Run(new MainForm(installation, initialScreen)
        {
            RevealMap = revealMap,
            ForcedOutcomeAfterSeconds = outcomeAfter,
            ForcedVictory = forcedVictory,
            CdImagePath = CdImageLocator.Locate(arguments, installation?.RootPath),
            NoMusic = arguments.Any(argument => argument.Equals("--no-music", StringComparison.OrdinalIgnoreCase)),
            NoVideo = arguments.Any(argument => argument.Equals("--no-video", StringComparison.OrdinalIgnoreCase)),
            // --replay <file.dcsave> watches a saved game from its first update.
            ReplayPath = Array.FindIndex(arguments, argument => argument.Equals("--replay", StringComparison.OrdinalIgnoreCase)) is var replay and >= 0 && replay + 1 < arguments.Length
                ? arguments[replay + 1]
                : null,
        });
        RuntimeLog.Info("Exited normally.");
        return 0;
    }
}
