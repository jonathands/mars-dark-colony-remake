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
        Application.Run(new MainForm(installation, initialScreen) { RevealMap = revealMap });
        RuntimeLog.Info("Exited normally.");
        return 0;
    }
}
