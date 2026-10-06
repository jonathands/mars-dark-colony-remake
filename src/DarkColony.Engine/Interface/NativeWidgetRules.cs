using System.Buffers.Binary;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Data;

namespace DarkColony.Engine.Interface;

/// <summary>
/// How <c>dc.exe</c>'s interface widgets look: the rules of <c>button.c</c>,
/// <c>list.c</c> and the scroll bar, kept free of drawing so the checks can pin them.
/// </summary>
public static class NativeWidgetRules
{
    public const int NormalBrightness = NativeColourRemap.NormalBrightness;
    public const int MaximumBrightness = NativeColourRemap.Brightnesses - 1;

    /// <summary>
    /// Interface gadgets step when more than 16 ms of <c>timeGetTime</c> have
    /// passed since the last redraw (<c>0x4242E5</c>), so at most every 17 ms.
    /// </summary>
    public const int GadgetStepMilliseconds = 17;

    /// <summary>The sound of a pressed push or check button: sound 0x61, BUTTON.WAV (<c>0x426F78</c>, <c>0x4271D7</c>).</summary>
    public const int ButtonSound = 0x61;

    /// <summary>The sound of each gadget a <c>banim</c> starts: sound 0xBA, ACTIVE.WAV (<c>0x427AF4</c>).</summary>
    public const int ButtonAnimationSound = 0xBA;

    /// <summary>
    /// The picture and brightness of a push or check button (<c>0x426DE0</c>).
    /// A button is down while pressed (a check button while checked) and
    /// highlighted while the pointer is over it. A negative picture number is
    /// a brightness offset: "<c>-11 2</c>" shows picture 2 at 11, 11 plus the
    /// highlight brightness, and the pushed brightness plus 16 when down.
    /// The label draws at the same brightness.
    /// </summary>
    public static (int Picture, int Brightness) ButtonLook(NativeWidget button, bool down, bool highlighted, int brightPushed, int brightHighlight)
    {
        ArgumentNullException.ThrowIfNull(button);
        int picture, brightness;
        if (!down)
        {
            var extra = highlighted ? brightHighlight : 0;
            (picture, brightness) = button.FirstPicture < 0
                ? (button.SecondPicture, extra - button.FirstPicture)
                : (button.FirstPicture, extra + NormalBrightness);
        }
        else
        {
            (picture, brightness) = button.SecondPicture < 0
                ? (button.FirstPicture, brightPushed - button.SecondPicture)
                : (button.SecondPicture, brightPushed + NormalBrightness);
        }
        return (picture, Math.Clamp(brightness, 0, MaximumBrightness));
    }

    /// <summary>
    /// A scroll bar's thumb (<c>0x42AA2B</c>): the part of the bar from
    /// <paramref name="start"/> to <paramref name="end"/> of its range, or null
    /// when the range is empty. The bar itself is cleared to its background and
    /// framed in its foreground colour, which also fills the thumb.
    /// </summary>
    public static InterfaceRectangle? ScrollThumb(InterfaceRectangle bar, int range, int start, int end)
    {
        if (range <= 0) return null;
        var top = bar.Y + start * bar.Height / range;
        var bottom = bar.Y + end * bar.Height / range;
        top = Math.Clamp(top, bar.Y, bar.Y + bar.Height);
        bottom = Math.Clamp(bottom, bar.Y, bar.Y + bar.Height);
        return bottom > top ? new InterfaceRectangle(bar.X, top, bar.Width, bottom - top) : null;
    }

    /// <summary>
    /// Where a button's or label's text starts (<c>0x426AF0</c>). Text runs in
    /// cells of the font's cell width plus one; the measured width leaves out
    /// the last cell. Centred text is centred on that width, right-aligned
    /// text ends one cell inside the box, and left-aligned text starts one
    /// cell width in. The line is centred vertically.
    /// </summary>
    public static (int X, int Y) TextOrigin(InterfaceRectangle box, NativeTextAlign align, int length, int cellWidth, int fontHeight)
    {
        var width = Math.Max(0, length - 1) * (cellWidth + 1);
        var x = align switch
        {
            NativeTextAlign.Centre => box.X + (box.Width - width) / 2,
            NativeTextAlign.Right => box.X + box.Width - cellWidth - width,
            _ => box.X + cellWidth,
        };
        return (x, box.Y + (box.Height - fontHeight) / 2);
    }
}

/// <summary>
/// A text list (<c>list.c</c>): its rows, the first row shown and the
/// selected one. The list draws rows of the font's height from its top while
/// a whole row fits (<c>0x42A1E9</c>), so it shows height / row height rows.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>A press on a row selects it, or nothing past the last row (<c>0x42A38B</c>).</description></item>
/// <item><description>A push button bound with <c>list n step</c> moves the
/// first row shown by its step, not the selection (<c>0x426F57</c>).</description></item>
/// <item><description>The first row shown stays within 0 to the row count, so
/// the list can scroll until it is empty (<c>0x42A805</c>).</description></item>
/// <item><description>The bound scroll bar shows rows top to top + visible of
/// the count (<c>0x42A568</c>); dragging it centres that span on the pointer
/// (<c>0x42AB2F</c>).</description></item>
/// </list>
/// </remarks>
public sealed class NativeListView
{
    public NativeListView(int visibleRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(visibleRows);
        VisibleRows = visibleRows;
    }

    public int VisibleRows { get; }
    public int Count { get; private set; }
    public int Top { get; private set; }
    public int Selected { get; private set; } = -1;

    /// <summary>New rows (<c>0x42A5B0</c>); the first row shown and the selection keep their values.</summary>
    public void SetCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Count = count;
    }

    /// <summary><c>0x42A770</c>: the first row shown, held within 0 to the row count.</summary>
    public void SetTop(int top) => Top = Math.Clamp(top, 0, Count);

    /// <summary><c>0x42A8C4</c>: the selection, or -1 for none.</summary>
    public void Select(int row) => Selected = row >= 0 && row < Count ? row : -1;

    /// <summary>A <c>list n step</c> push button.</summary>
    public void Step(int step) => SetTop(Top + step);

    /// <summary>The row under a press <paramref name="offset"/> pixels below the list's top, or -1 past the last.</summary>
    public int RowAt(int offset, int rowHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowHeight);
        var row = offset / rowHeight + Top;
        return row >= 0 && row < Count ? row : -1;
    }

    /// <summary>The bound scroll bar's range and the span it shows.</summary>
    public (int Range, int Start, int End) ScrollSpan => (Count, Top, Top + VisibleRows);

    /// <summary>
    /// A drag on the bound scroll bar <paramref name="offset"/> pixels below
    /// its top: the span is centred on the pointer (half its size, rounded
    /// toward zero, above it), and the list's first row follows the span's start.
    /// </summary>
    public void Drag(int offset, int barHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(barHeight);
        var (range, start, end) = ScrollSpan;
        if (range <= 0) return;
        var position = offset * range / barHeight;
        var start2 = position - (end - start) / 2;
        SetTop(start2 * Count / range);
    }
}

/// <summary>
/// The interface's named colours. Every screen starts with the five records
/// at <c>0x479420</c> ({red, green, blue, name pointer}: erase, ui,
/// ui_highlight, ui_active, textfg); a <c>colour</c> line overrides a name it
/// repeats and adds a new one after them (<c>0x421790</c>).
/// </summary>
public sealed class NativeInterfaceColours
{
    public const uint DefaultsAddress = 0x479420;
    public const int DefaultCount = 5;

    private NativeInterfaceColours(IReadOnlyList<(string Name, byte Red, byte Green, byte Blue)> defaults) => Defaults = defaults;

    public IReadOnlyList<(string Name, byte Red, byte Green, byte Blue)> Defaults { get; }

    public static NativeInterfaceColours Load(string executablePath) => FromImage(PeImage.Load(executablePath));

    public static NativeInterfaceColours FromImage(PeImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var records = image.AtVirtualAddress(DefaultsAddress, DefaultCount * 16).ToArray();
        var defaults = new List<(string, byte, byte, byte)>();
        for (var index = 0; index < DefaultCount; index++)
        {
            var record = records.AsSpan(index * 16);
            var name = image.AtVirtualAddress(BinaryPrimitives.ReadUInt32LittleEndian(record[12..]), 16).ToArray();
            var length = Array.IndexOf(name, (byte)0);
            defaults.Add((System.Text.Encoding.ASCII.GetString(name, 0, length < 0 ? name.Length : length),
                (byte)BinaryPrimitives.ReadInt32LittleEndian(record), (byte)BinaryPrimitives.ReadInt32LittleEndian(record[4..]),
                (byte)BinaryPrimitives.ReadInt32LittleEndian(record[8..])));
        }
        return new NativeInterfaceColours(defaults);
    }

    /// <summary>The colour table of <paramref name="screen"/>, by index.</summary>
    public IReadOnlyList<(string Name, byte Red, byte Green, byte Blue)> For(NativeScreenDefinition screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var table = Defaults.ToList();
        foreach (var colour in screen.DeclaredColours)
        {
            var index = table.FindIndex(entry => entry.Name == colour.Name);
            if (index >= 0) table[index] = colour;
            else table.Add(colour);
        }
        return table;
    }

    /// <summary>
    /// The colour named <paramref name="name"/>, or the one at
    /// <paramref name="fallbackIndex"/> when the widget names none (a list,
    /// scroll bar or unmasked gadget clears to index 0; a selection or scroll
    /// thumb is index 1).
    /// </summary>
    public static (byte Red, byte Green, byte Blue) Resolve(IReadOnlyList<(string Name, byte Red, byte Green, byte Blue)> table, string? name, int fallbackIndex)
    {
        ArgumentNullException.ThrowIfNull(table);
        var index = name is null ? fallbackIndex : Enumerable.Range(0, table.Count).FirstOrDefault(entry => table[entry].Name == name, fallbackIndex);
        var colour = table[Math.Clamp(index, 0, table.Count - 1)];
        return (colour.Red, colour.Green, colour.Blue);
    }

    /// <summary>
    /// The screen palette entry a colour shows as: the nearest by squared
    /// distance, the first on a tie. The captured War lobby draws "ui"
    /// (194, 35, 7) as entry 97, (203, 23, 23).
    /// </summary>
    public static int Nearest(IReadOnlyList<VgaColor> palette, (byte Red, byte Green, byte Blue) colour)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var index = 0; index < palette.Count; index++)
        {
            var (red, green, blue) = (palette[index].Red - colour.Red, palette[index].Green - colour.Green, palette[index].Blue - colour.Blue);
            var distance = red * red + green * green + blue * blue;
            if (distance >= bestDistance) continue;
            best = index;
            bestDistance = distance;
        }
        return best;
    }
}
