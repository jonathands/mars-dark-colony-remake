namespace DarkColony.Engine.Data;

/// <summary>Original sound2.dat IDs and slist.dat weighted owner/category lists.</summary>
public sealed record SoundDefinition(int Id, string RelativePath, IReadOnlyList<int> Parameters);

public sealed class SoundCatalog
{
    private readonly IReadOnlyDictionary<(int OwnerId, string Category), IReadOnlyList<int>> lists;

    private SoundCatalog(IReadOnlyDictionary<int, SoundDefinition> sounds, IReadOnlyDictionary<(int, string), IReadOnlyList<int>> lists)
    {
        Sounds = sounds;
        this.lists = lists;
    }

    public IReadOnlyDictionary<int, SoundDefinition> Sounds { get; }

    public IReadOnlyList<int> For(int ownerId, string category) =>
        lists.GetValueOrDefault((ownerId, category.ToUpperInvariant()), []);

    public static SoundCatalog Load(string soundDirectory) => Parse(
        File.ReadAllText(Path.Combine(soundDirectory, "sound2.dat")),
        File.ReadAllText(Path.Combine(soundDirectory, "slist.dat")));

    public static SoundCatalog Parse(string catalogText, string listText)
    {
        var sounds = new Dictionary<int, SoundDefinition>();
        foreach (var raw in catalogText.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || words[0].StartsWith('%')) continue;
            var id = int.Parse(words[0]);
            var relative = words[1].Replace(".\\", string.Empty, StringComparison.Ordinal).Replace('\\', '/');
            sounds[id] = new SoundDefinition(id, relative, words.Skip(2).Select(int.Parse).ToArray());
        }

        var lists = new Dictionary<(int, string), IReadOnlyList<int>>();
        foreach (var raw in listText.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var words = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 3 || words[0].StartsWith('%')) continue;
            var owner = int.Parse(words[0]);
            var ids = words.Skip(2).Select(int.Parse).TakeWhile(id => id != -1).ToArray();
            lists[(owner, words[1].ToUpperInvariant())] = ids;
        }
        return new SoundCatalog(sounds, lists);
    }
}
