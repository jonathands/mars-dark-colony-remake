namespace DarkColony.Engine.Data;

public sealed record GameInstallation(string RootPath)
{
    public string ExecutablePath => Path.Combine(RootPath, "dc.exe");
    public string GameStatPath => Path.Combine(RootPath, "gamestat");
    public string SpritePath => Path.Combine(RootPath, "sprites");
    public string ScenarioPath => Path.Combine(RootPath, "scenario");

    public static GameInstallation Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var installation = new GameInstallation(Path.GetFullPath(path));
        var missing = new[]
        {
            installation.ExecutablePath,
            installation.GameStatPath,
            installation.SpritePath,
            installation.ScenarioPath,
        }.Where(candidate => !File.Exists(candidate) && !Directory.Exists(candidate)).ToArray();

        if (missing.Length != 0)
        {
            throw new DirectoryNotFoundException(
                $"Not a complete Dark Colony installation. Missing: {string.Join(", ", missing)}");
        }

        return installation;
    }

    public string DataFile(params string[] components) =>
        Path.Combine(new[] { RootPath }.Concat(components).ToArray());
}
