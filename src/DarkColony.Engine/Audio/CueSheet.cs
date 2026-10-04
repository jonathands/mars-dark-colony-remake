using System.Globalization;

namespace DarkColony.Engine.Audio;

/// <summary>A track of a raw CD image: its file and the byte range from its INDEX 01 to the next track (or the file end).</summary>
public sealed record CueTrack(int Number, bool Audio, string FilePath, long Offset, long Length);

/// <summary>
/// A CUE sheet over raw 2352-byte-sector images (BIN/CUE), enough to stream
/// the Dark Colony disc's Red Book tracks: 44.1 kHz, 16-bit, stereo,
/// little-endian PCM (<c>BINARY</c> files).
/// </summary>
public sealed class CueSheet
{
    public const int SectorBytes = 2352;
    public const int FramesPerSecond = 75;

    private CueSheet(IReadOnlyList<CueTrack> tracks) => Tracks = tracks;

    public IReadOnlyList<CueTrack> Tracks { get; }

    public IEnumerable<CueTrack> AudioTracks => Tracks.Where(track => track.Audio);

    public static CueSheet Load(string cuePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
        return Parse(File.ReadAllText(cuePath), file =>
        {
            var path = Path.Combine(directory, file);
            return (path, new FileInfo(path).Length);
        });
    }

    /// <param name="resolveFile">Maps a FILE name to its path and byte length.</param>
    public static CueSheet Parse(string text, Func<string, (string Path, long Length)> resolveFile)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(resolveFile);
        var starts = new List<(int Number, bool Audio, string Path, long FileLength, long? Frame)>();
        (string Path, long Length)? file = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                var name = Unquote(line[5..line.LastIndexOf(' ')].Trim());
                var kind = line[(line.LastIndexOf(' ') + 1)..];
                if (!kind.Equals("BINARY", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"CUE file type {kind} is not a little-endian BINARY image.");
                file = resolveFile(name);
            }
            else if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
            {
                if (file is null) throw new InvalidDataException("CUE TRACK precedes any FILE.");
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var mode = parts[2].ToUpperInvariant();
                if (mode is not ("AUDIO" or "MODE1/2352" or "MODE2/2352"))
                    throw new InvalidDataException($"CUE track mode {mode} is not a 2352-byte sector.");
                starts.Add((int.Parse(parts[1], CultureInfo.InvariantCulture), mode == "AUDIO", file.Value.Path, file.Value.Length, null));
            }
            else if (line.StartsWith("INDEX 01 ", StringComparison.OrdinalIgnoreCase) && starts.Count > 0)
            {
                var time = line[9..].Trim().Split(':');
                if (time.Length != 3) throw new InvalidDataException($"CUE index time {line[9..]} is not mm:ss:ff.");
                var frame = (int.Parse(time[0], CultureInfo.InvariantCulture) * 60 + int.Parse(time[1], CultureInfo.InvariantCulture)) * FramesPerSecond
                    + int.Parse(time[2], CultureInfo.InvariantCulture);
                starts[^1] = starts[^1] with { Frame = frame };
            }
        }

        var tracks = new List<CueTrack>();
        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index];
            if (start.Frame is not { } frame) throw new InvalidDataException($"CUE track {start.Number} has no INDEX 01.");
            var offset = frame * SectorBytes;
            var end = index + 1 < starts.Count && starts[index + 1].Path == start.Path && starts[index + 1].Frame is { } next
                ? next * SectorBytes
                : start.FileLength;
            if (end < offset) throw new InvalidDataException($"CUE track {start.Number} ends before it starts.");
            tracks.Add(new CueTrack(start.Number, start.Audio, start.Path, offset, end - offset));
        }
        return new CueSheet(tracks);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
