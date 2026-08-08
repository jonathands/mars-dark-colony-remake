using System.Globalization;

namespace DarkColony.Engine.Data;

/// <summary>
/// One data-defined <c>boomstat.txt</c> area-effect template. The first square
/// is retained as the authored radial percentage pattern. The second fixed
/// three-by-three square is structurally distinct in every shipped template,
/// but its runtime purpose remains untraced.
/// </summary>
public sealed record AreaEffectTemplate(
    int Id,
    int PatternSize,
    IReadOnlyList<string> EffectNames,
    IReadOnlyList<IReadOnlyList<int>> DamagePattern,
    IReadOnlyList<IReadOnlyList<int>> UnknownPattern);

/// <summary>Decoder for the installed <c>gamestat/boomstat.txt</c> effect templates.</summary>
public sealed class AreaEffectCatalog
{
    private AreaEffectCatalog(IReadOnlyDictionary<int, AreaEffectTemplate> templates) => Templates = templates;

    public IReadOnlyDictionary<int, AreaEffectTemplate> Templates { get; }
    public bool TryGet(int id, out AreaEffectTemplate template) => Templates.TryGetValue(id, out template!);

    public static AreaEffectCatalog Load(string path) => Parse(File.ReadAllText(path));

    public static AreaEffectCatalog Parse(string text)
    {
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length != 0 && !line.StartsWith('%'))
            .ToArray();
        if (lines.Length == 0 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var declared))
            throw new InvalidDataException("boomstat.txt has no declared template count.");

        var cursor = 1;
        var templates = new Dictionary<int, AreaEffectTemplate>();
        while (cursor < lines.Length)
        {
            var header = Integers(lines[cursor++]);
            if (header.Length != 2 || header[1] <= 0)
                throw new InvalidDataException("Area-effect template header must contain ID and positive pattern size.");
            var effects = new List<string>();
            while (cursor < lines.Length && !lines[cursor].Equals("NONE", StringComparison.OrdinalIgnoreCase))
                effects.Add(lines[cursor++]);
            if (cursor == lines.Length) throw new InvalidDataException($"Area-effect template {header[0]} has no NONE terminator.");
            cursor++;

            var damage = ReadSquare(header[1], header[0], "damage");
            var unknown = ReadSquare(3, header[0], "trailing");
            if (!templates.TryAdd(header[0], new AreaEffectTemplate(header[0], header[1], effects, damage, unknown)))
                throw new InvalidDataException($"Duplicate area-effect template {header[0]}.");
        }
        if (templates.Count != declared)
            throw new InvalidDataException($"boomstat.txt declares {declared} templates but contains {templates.Count}.");
        return new AreaEffectCatalog(templates);

        IReadOnlyList<IReadOnlyList<int>> ReadSquare(int size, int templateId, string name)
        {
            var square = new List<IReadOnlyList<int>>(size);
            for (var row = 0; row < size; row++)
            {
                if (cursor == lines.Length) throw new InvalidDataException($"Area-effect template {templateId} ends in its {name} pattern.");
                var values = Integers(lines[cursor++]);
                if (values.Length != size)
                    throw new InvalidDataException($"Area-effect template {templateId} {name} row {row} has {values.Length} values; expected {size}.");
                square.Add(values);
            }
            return square;
        }
    }

    private static int[] Integers(string line)
    {
        var values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new int[values.Length];
        for (var index = 0; index < values.Length; index++)
            if (!int.TryParse(values[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[index]))
                return [];
        return result;
    }
}
