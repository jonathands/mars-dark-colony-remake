namespace DarkColony.Engine.Audio;

/// <summary>
/// Finds the user's CD image for the music: <c>--cd-image &lt;cue&gt;</c>, then
/// <c>DARKCOLONY_CD_IMAGE</c>, then a CUE sheet with audio from track 2 on, in
/// the installation folder or the folder above it. <c>--no-music</c> turns
/// the music off.
/// </summary>
public static class CdImageLocator
{
    public static string? Locate(IReadOnlyList<string> arguments, string? installationRoot)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(argument => argument.Equals("--no-music", StringComparison.OrdinalIgnoreCase))) return null;
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals("--cd-image", StringComparison.OrdinalIgnoreCase)) return arguments[index + 1];
        }

        var environmentPath = Environment.GetEnvironmentVariable("DARKCOLONY_CD_IMAGE");
        if (!string.IsNullOrWhiteSpace(environmentPath)) return environmentPath;
        if (installationRoot is null) return null;

        var root = Path.GetFullPath(installationRoot);
        foreach (var directory in new[] { root, Path.GetDirectoryName(root) })
        {
            if (directory is null || !Directory.Exists(directory)) continue;
            foreach (var cue in Directory.EnumerateFiles(directory, "*.cue").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (CdMusic.Pass(CueSheet.Load(cue)).Count > 0) return cue;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
                {
                    // Not a usable music image; keep looking.
                }
            }
        }
        return null;
    }
}
