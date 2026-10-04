namespace DarkColony.Engine.Interface;

/// <summary>A teletype grid cell: the byte read from the file and its <c>~N</c> colour.</summary>
public readonly record struct TeletypeCell(byte Character, byte Colour)
{
    /// <summary>
    /// The font frame the native draw uses: the byte minus 0x1F, where
    /// anything past 0x7A (control bytes wrap below zero) becomes frame 1,
    /// the space.
    /// </summary>
    public int Glyph
    {
        get
        {
            var glyph = unchecked((byte)(Character - 0x1F));
            return glyph > 0x7A ? 1 : glyph;
        }
    }
}

/// <summary>A typed cell, positioned in the window's cell grid, with the brightness it was last drawn at.</summary>
public readonly record struct TeletypeGlyph(int Column, int Row, TeletypeCell Cell, int Brightness);

/// <summary>
/// The executable's "TTY" text window (<c>0x428514</c> builds it,
/// <c>0x427C34</c> runs it once per menu loop), which types the main-menu
/// credits.
/// </summary>
/// <remarks>
/// <para>
/// The file is read in text mode ("rt", so CR-LF becomes LF). <c>~N</c>
/// (N = 0-7) sets the colour of the bytes that follow. Lines wrap into a grid
/// of <see cref="Columns"/> cells: a scan looks at up to Columns + 1 bytes and
/// breaks at the last space, or at the first newline. Grid lines keep being
/// added, from the blank padding after the text, until the copied byte count
/// exceeds the bytes read.
/// </para>
/// <para>
/// Every step (one per menu loop, once more than 5 ms have passed) draws
/// the next character. Four trailing cursors redraw the previous ones, so the
/// last five cells show brightness 0x1F, 0x1C, 0x18, 0x14, then 0x10. When the
/// lead cursor runs past the last row the window clears and redraws one line
/// lower, all at 0x10. Once the last cursor passes the end the window clears
/// and typing restarts (the main menu's repeat mode 2).
/// </para>
/// </remarks>
public sealed class TeletypeText
{
    /// <summary>Steps need strictly more than 5 ms between them (<c>0x427DF8</c>).</summary>
    public const int StepMilliseconds = 6;

    public const int NormalBrightness = 0x10;
    private static readonly int[] TrailBrightness = [0x1F, 0x1C, 0x18, 0x14];
    private const int TextBufferSize = 0x1F40;

    private readonly TeletypeCell[] _cells;

    private TeletypeText(TeletypeCell[] cells, int columns, int lastRow, int lineCount)
    {
        _cells = cells;
        Columns = columns;
        LastRow = lastRow;
        LineCount = lineCount;
    }

    public int Columns { get; }

    /// <summary>The bottom row index (rows 0..LastRow are visible).</summary>
    public int LastRow { get; }

    public int LineCount { get; }

    public IReadOnlyList<TeletypeCell> Cells => _cells;

    /// <summary>Steps in one pass, the last of which clears the window for the restart.</summary>
    public long StepsPerCycle => _cells.Length + 1L;

    /// <summary>The grid size <c>0x428514</c> derives from the font's cell (frame 0) and one pixel of spacing.</summary>
    public static (int Columns, int LastRow) Layout(int width, int height, int cellWidth, int cellHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellHeight);
        var columns = (width - width / cellWidth) / cellWidth;
        var lastRow = (height - height / cellHeight) / cellHeight - 1;
        return (columns, lastRow);
    }

    public static TeletypeText Parse(ReadOnlySpan<byte> file, int columns, int lastRow)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegative(lastRow);
        var characters = new List<byte>();
        var colours = new List<byte>();
        byte colour = 0;
        for (var index = 0; index < file.Length; index++)
        {
            var value = file[index];
            if (value == 0x1A) break;
            if (value == '\r' && index + 1 < file.Length && file[index + 1] == '\n') continue;
            if (value == '~')
            {
                var digit = index + 1 < file.Length ? file[++index] - '0' : -1;
                if (digit is < 0 or > 7) throw new InvalidDataException($"Teletype colour code at byte {index} is not ~0 to ~7.");
                colour = (byte)digit;
                continue;
            }
            characters.Add(value);
            colours.Add(colour);
        }
        if (characters.Count > TextBufferSize) throw new InvalidDataException($"Teletype text exceeds the native {TextBufferSize}-byte buffer.");

        byte CharacterAt(int position) => position < characters.Count ? characters[position] : (byte)' ';
        byte ColourAt(int position) => position < colours.Count ? colours[position] : (byte)0;

        var cells = new List<TeletypeCell>();
        var total = characters.Count;
        var copied = 0;
        var breakAt = 0;
        var position = 0;
        var lines = 0;
        do
        {
            var start = position;
            for (var scanned = 0; scanned <= columns; scanned++, position++)
            {
                var value = CharacterAt(position);
                if (value == ' ') breakAt = position;
                if (value == '\n')
                {
                    breakAt = position;
                    break;
                }
            }
            // The native scan keeps a stale break here and never finishes.
            if (breakAt < start) throw new InvalidDataException($"Teletype line at byte {start} has no space or newline within {columns + 1} bytes.");
            var length = breakAt - start;
            for (var column = 0; column < columns; column++)
            {
                if (column < length)
                {
                    cells.Add(new TeletypeCell(CharacterAt(start + column), ColourAt(start + column)));
                    copied++;
                }
                else cells.Add(new TeletypeCell((byte)' ', ColourAt(start + column)));
            }
            position = breakAt + 1;
            lines++;
            if (cells.Count > TextBufferSize) throw new InvalidDataException($"Teletype grid exceeds the native {TextBufferSize} cells.");
        }
        while (copied <= total);

        return new TeletypeText([.. cells], columns, lastRow, lines);
    }

    /// <summary>Steps taken <paramref name="milliseconds"/> after the window opened: the first follows at once.</summary>
    public static long StepsAfter(long milliseconds) => milliseconds < 0 ? 0 : 1 + milliseconds / StepMilliseconds;

    /// <summary>The first grid line in the window after <paramref name="steps"/> steps.</summary>
    public int TopLine(long steps)
    {
        var lead = LeadCell(steps);
        return lead < 0 ? 0 : Math.Max(0, (lead + 1) / Columns - LastRow);
    }

    /// <summary>The non-blank cells on screen after <paramref name="steps"/> steps.</summary>
    public IReadOnlyList<TeletypeGlyph> Visible(long steps)
    {
        var lead = LeadCell(steps);
        if (lead < 0) return [];
        var top = TopLine(steps);
        var scrolled = (lead + 1) % Columns == 0 && (lead + 1) / Columns - LastRow > 0;
        var glyphs = new List<TeletypeGlyph>();
        for (var row = 0; row <= LastRow; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var position = (top + row) * Columns + column;
                if (position > lead) return glyphs;
                if (position >= _cells.Length) continue;
                var cell = _cells[position];
                if (cell.Glyph == 1) continue;
                var behind = lead - position;
                var brightness = behind < TrailBrightness.Length && !(scrolled && behind == 0)
                    ? TrailBrightness[behind]
                    : NormalBrightness;
                glyphs.Add(new TeletypeGlyph(column, row, cell, brightness));
            }
        }
        return glyphs;
    }

    /// <summary>Whether the step that leads to <paramref name="steps"/> typed a visible character (it plays BEEP).</summary>
    public bool Typed(long steps)
    {
        var lead = LeadCell(steps);
        if (lead < 0) return false;
        for (var behind = 0; behind <= TrailBrightness.Length; behind++)
        {
            var position = lead - behind;
            if (position >= 0 && position < _cells.Length && _cells[position].Glyph != 1) return true;
        }
        return false;
    }

    /// <summary>The cell the lead cursor drew on the latest step, or -1 while the window is clear.</summary>
    private int LeadCell(long steps)
    {
        if (steps <= 0) return -1;
        var phase = steps % StepsPerCycle;
        return phase == 0 ? -1 : (int)phase + TrailBrightness.Length - 1;
    }
}
