namespace DarkColony.Engine.Data;

public sealed record EncyclopediaEntry(int NativeId, string Name, string ResourceStem);

public sealed record EncyclopediaCategory(string Name, IReadOnlyList<EncyclopediaEntry> Entries);

public sealed class EncyclopediaCatalog
{
    private EncyclopediaCatalog(IReadOnlyList<EncyclopediaCategory> categories) => Categories = categories;

    public IReadOnlyList<EncyclopediaCategory> Categories { get; }

    public static EncyclopediaCatalog Load(string path)
    {
        var lines = File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length != 0)
            .ToArray();
        var position = 0;
        var categoryNames = new[] { "GRAYS", "HUMANS", "ARTIFACTS" };
        var categories = new List<EncyclopediaCategory>(categoryNames.Length);
        var nativeId = 0;
        foreach (var categoryName in categoryNames)
        {
            var count = ReadInt(lines, ref position, $"{categoryName} entry count");
            var entries = new List<EncyclopediaEntry>(count);
            for (var index = 0; index < count; index++)
            {
                if (position + 1 >= lines.Length) throw new InvalidDataException($"Truncated {categoryName} encyclopedia entry.");
                var name = lines[position++].TrimEnd(':').Trim();
                var resource = lines[position++];
                entries.Add(new EncyclopediaEntry(nativeId, name, resource));
                nativeId = ReadInt(lines, ref position, $"{categoryName} next native id");
            }

            categories.Add(new EncyclopediaCategory(categoryName, entries));
        }

        if (position != lines.Length) throw new InvalidDataException("Unexpected trailing encyclopedia catalog data.");
        return new EncyclopediaCatalog(categories);
    }

    private static int ReadInt(string[] lines, ref int position, string field)
    {
        if (position >= lines.Length || !int.TryParse(lines[position++], out var value))
        {
            throw new InvalidDataException($"Invalid {field}.");
        }

        return value;
    }
}
