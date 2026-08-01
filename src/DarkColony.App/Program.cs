using DarkColony.Engine.Data;

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

        Application.Run(new MainForm(installation));
    }
}
