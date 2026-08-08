namespace DarkColony.Engine.Data;

/// <summary>
/// One display line from an original <c>ENCYCLO/*.TXT</c> article. The source
/// uses inline <c>~0</c>, <c>~1</c>, and <c>~4</c> palette controls; the first
/// control is retained as a presentation hint while the readable text remains
/// engine/data-layer neutral.
/// </summary>
public sealed record EncyclopediaArticleSegment(string Text, int PaletteIndex);

public sealed record EncyclopediaArticleLine(IReadOnlyList<EncyclopediaArticleSegment> Segments)
{
    public string Text => string.Concat(Segments.Select(segment => segment.Text));
    public int PaletteIndex => Segments.FirstOrDefault()?.PaletteIndex ?? 1;
}

/// <summary>Decoder for the external per-entry encyclopedia text resources.</summary>
public sealed class EncyclopediaArticle
{
    private EncyclopediaArticle(IReadOnlyList<EncyclopediaArticleLine> lines) => Lines = lines;

    public IReadOnlyList<EncyclopediaArticleLine> Lines { get; }

    public static EncyclopediaArticle Load(string installationRoot, string resourceStem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceStem);
        var relativePath = Path.ChangeExtension(resourceStem.Replace('/', Path.DirectorySeparatorChar), ".TXT");
        return Parse(File.ReadAllText(Path.Combine(installationRoot, relativePath)));
    }

    public static EncyclopediaArticle Parse(string text)
    {
        var lines = new List<EncyclopediaArticleLine>();
        foreach (var raw in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var palette = 1;
            var segments = new List<EncyclopediaArticleSegment>();
            var characters = new List<char>(raw.Length);
            void Flush()
            {
                if (characters.Count == 0) return;
                segments.Add(new EncyclopediaArticleSegment(new string(characters.ToArray()), palette));
                characters.Clear();
            }
            for (var index = 0; index < raw.Length; index++)
            {
                if (raw[index] == '~' && index + 1 < raw.Length && char.IsAsciiDigit(raw[index + 1]))
                {
                    Flush();
                    palette = raw[index + 1] - '0';
                    index++;
                    continue;
                }
                characters.Add(raw[index]);
            }
            Flush();
            if (segments.Count != 0)
            {
                var last = segments[^1];
                segments[^1] = last with { Text = last.Text.TrimEnd() };
            }
            lines.Add(new EncyclopediaArticleLine(segments));
        }
        return new EncyclopediaArticle(lines);
    }
}
