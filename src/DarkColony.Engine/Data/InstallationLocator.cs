namespace DarkColony.Engine.Data;

public static class InstallationLocator
{
    public static GameInstallation? Locate(IReadOnlyList<string> arguments, string workingDirectory)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--data", StringComparison.OrdinalIgnoreCase))
            {
                return GameInstallation.Open(arguments[index + 1]);
            }
        }

        var environmentPath = Environment.GetEnvironmentVariable("DARKCOLONY_DATA");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return GameInstallation.Open(environmentPath);
        }

        var adjacent = Path.GetFullPath(Path.Combine(workingDirectory, "..", "Dark Colony"));
        return File.Exists(Path.Combine(adjacent, "dc.exe")) ? GameInstallation.Open(adjacent) : null;
    }
}
