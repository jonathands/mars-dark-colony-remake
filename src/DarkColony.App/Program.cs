using DarkColony.Engine.Data;
using DarkColony.App.Ui;

namespace DarkColony.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        ApplicationConfiguration.Initialize();
        GameInstallation? installation;
        try
        {
            installation = InstallationLocator.Locate(arguments, Environment.CurrentDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(error.Message, "Dark Colony data error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var initialScreen = arguments.Any(argument =>
            argument.Equals("--single-player-war", StringComparison.OrdinalIgnoreCase))
            ? MenuScreenId.SinglePlayer
            : MenuScreenId.Main;
        Application.Run(new MainForm(installation, initialScreen));
    }
}
