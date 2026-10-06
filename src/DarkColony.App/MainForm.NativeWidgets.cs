using DarkColony.Engine.Assets;
using DarkColony.Engine.Data;
using DarkColony.Engine.Interface;

namespace DarkColony.App;

/// <summary>
/// Interface widgets drawn the way <c>dc.exe</c>'s <c>widget.c</c> draws
/// them, from a screen's <c>intrface</c> definition: button pictures from the
/// screen's picture sheet, text in fixed font cells, lists and scroll bars in
/// the screen's named colours.
/// </summary>
public sealed partial class MainForm
{
    private readonly Dictionary<string, NativeScreenDefinition?> _nativeScreens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(NativeScreenDefinition Screen, string Palette, string? Name, int Fallback), Color> _nativeColours = [];
    private readonly Dictionary<(string Sheet, string Palette, int Frame, int Brightness), Bitmap?> _nativePictures = [];
    private readonly Dictionary<string, Sprite> _pictureSheets = new(StringComparer.OrdinalIgnoreCase);
    private NativeInterfaceColours? _interfaceColours;

    /// <summary>An interface definition such as <c>multie</c>, or null when it cannot be read.</summary>
    private NativeScreenDefinition? NativeScreen(string name)
    {
        if (_installation is null) return null;
        if (_nativeScreens.TryGetValue(name, out var screen)) return screen;
        try
        {
            screen = NativeScreenDefinition.Load(_installation.DataFile("intrface", name));
        }
        catch (Exception error) when (error is IOException or FormatException)
        {
            _status = $"Interface error: {error.Message}";
            screen = null;
        }
        _nativeScreens[name] = screen;
        return screen;
    }

    /// <summary>
    /// A named screen colour as the screen shows it: the nearest entry of
    /// the screen palette. <paramref name="fallbackIndex"/> stands in when
    /// the widget names no colour.
    /// </summary>
    private Color NativeColour(NativeScreenDefinition screen, string palette, string? name, int fallbackIndex)
    {
        if (_nativeColours.TryGetValue((screen, palette, name, fallbackIndex), out var cached)) return cached;
        _interfaceColours ??= NativeInterfaceColours.Load(_installation!.ExecutablePath);
        var colours = ScreenPalette(palette);
        var entry = colours[NativeInterfaceColours.Nearest(colours, NativeInterfaceColours.Resolve(_interfaceColours.For(screen), name, fallbackIndex))];
        var colour = Color.FromArgb(entry.Red, entry.Green, entry.Blue);
        _nativeColours[(screen, palette, name, fallbackIndex)] = colour;
        return colour;
    }

    /// <summary>
    /// A picture of the screen's sheet (<c>pictures intrface/knobe</c>) in the
    /// screen palette through colour 0 at <paramref name="brightness"/>, as
    /// the button draw (<c>0x426DE0</c>) blits it.
    /// </summary>
    private void DrawNativePicture(Graphics graphics, NativeScreenDefinition screen, string palette, int frame, int x, int y, int brightness)
    {
        if (_installation is null || screen.Pictures is not { } sheetPath) return;
        var key = (sheetPath, palette, frame, brightness);
        if (!_nativePictures.TryGetValue(key, out var bitmap))
        {
            bitmap = null;
            try
            {
                var name = Path.GetFileName(sheetPath);
                if (!_pictureSheets.TryGetValue(name, out var sheet))
                {
                    sheet = Sprite.Load(_installation.DataFile("intrface", $"{name}.spr"));
                    _pictureSheets[name] = sheet;
                }
                if ((uint)frame < (uint)sheet.Frames.Count && sheet.Frames[frame] is { Width: > 0, Height: > 0 } picture)
                {
                    _colourRemap ??= NativeColourRemap.Load(_installation.ExecutablePath);
                    var colours = ScreenPalette(palette);
                    var indices = picture.DecodeIndices();
                    var rgba = new byte[indices.Length * 4];
                    for (var pixel = 0; pixel < indices.Length; pixel++)
                    {
                        if (indices[pixel] == 0) continue;
                        var colour = _colourRemap.Map(colours, indices[pixel], 0, brightness);
                        rgba[pixel * 4] = colour.Red;
                        rgba[pixel * 4 + 1] = colour.Green;
                        rgba[pixel * 4 + 2] = colour.Blue;
                        rgba[pixel * 4 + 3] = 255;
                    }
                    bitmap = BitmapFromRgba(picture.Width, picture.Height, rgba);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                _status = $"Interface picture error: {error.Message}";
            }
            _nativePictures[key] = bitmap;
        }
        if (bitmap is not null) DrawNative(graphics, bitmap, x, y);
    }

    /// <summary>
    /// A push or check button (<c>0x426DE0</c>): its picture and its label at
    /// the brightness <see cref="NativeWidgetRules.ButtonLook"/> gives.
    /// <paramref name="label"/> replaces the widget's own text message.
    /// </summary>
    private void DrawNativeButton(Graphics graphics, NativeScreenDefinition screen, string palette, NativeWidget button,
        bool down, bool highlighted, string? label = null)
    {
        var (picture, brightness) = NativeWidgetRules.ButtonLook(button, down, highlighted, screen.BrightPushed, screen.BrightHighlight);
        if (picture >= 0) DrawNativePicture(graphics, screen, palette, picture, button.Bounds.X, button.Bounds.Y, brightness);
        var text = label ?? screen.Message(button.Message);
        if (!string.IsNullOrEmpty(text))
            DrawNativeText(graphics, screen, palette, button.Bounds, button.Align, text, button.Font, button.Remap, brightness);
    }

    /// <summary>A label's or button's text placed by <see cref="NativeWidgetRules.TextOrigin"/>.</summary>
    private void DrawNativeText(Graphics graphics, NativeScreenDefinition screen, string palette, InterfaceRectangle box,
        NativeTextAlign align, string text, int fontIndex, int remap, int brightness)
    {
        if (NativeFont(screen, fontIndex) is not { } font) return;
        var cell = font.Sprite.Frames[0];
        var (x, y) = NativeWidgetRules.TextOrigin(box, align, text.Length, cell.Width, cell.Height);
        DrawCellText(graphics, text, x, y, font, remap, brightness, palette);
    }

    private BitmapFont? NativeFont(NativeScreenDefinition screen, int fontIndex)
    {
        if (!screen.Fonts.TryGetValue(fontIndex, out var path)) return null;
        try
        {
            return LoadFont(Path.GetFileName(path));
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Font error: {error.Message}";
            return null;
        }
    }

    /// <summary>
    /// A text list (<c>0x42A150</c>): the box cleared to its background, then
    /// one row per font height from the first row shown while a whole row
    /// fits. The selected row is filled with the selection colour and its
    /// text drawn at brightness 0 (black); a row's text stops at the last
    /// whole cell.
    /// </summary>
    private void DrawNativeList(Graphics graphics, NativeScreenDefinition screen, string palette, NativeWidget list,
        NativeListView view, Func<int, string> rowText)
    {
        if (NativeFont(screen, list.Font) is not { } font) return;
        var bounds = ToRectangle(list.Bounds);
        FillNative(graphics, bounds, NativeColour(screen, palette, list.Background, 0));
        var cell = font.Sprite.Frames[0];
        var rowHeight = cell.Height;
        var cells = Math.Max(0, (bounds.Width - cell.Width) / (cell.Width + 1) + 1);
        var selection = NativeColour(screen, palette, list.SelectionBackground, 1);
        var y = bounds.Y;
        for (var row = view.Top; row < view.Count && y + rowHeight <= bounds.Bottom; row++, y += rowHeight)
        {
            var selected = row == view.Selected;
            if (selected) FillNative(graphics, new Rectangle(bounds.X, y, bounds.Width, rowHeight), selection);
            var text = rowText(row);
            if (text.Length > cells) text = text[..cells];
            DrawCellText(graphics, text, bounds.X, y, font, list.Remap, selected ? 0 : list.Intensity, palette);
        }
    }

    /// <summary>
    /// A scroll bar (<c>0x42A970</c>): cleared to its background, framed in
    /// its foreground colour (colour 1 when it names none), with the shown
    /// span of its range filled in that colour.
    /// </summary>
    private void DrawNativeScroll(Graphics graphics, NativeScreenDefinition screen, string palette, NativeWidget scroll,
        (int Range, int Start, int End) span)
    {
        var bounds = ToRectangle(scroll.Bounds);
        var foreground = NativeColour(screen, palette, scroll.Foreground, 1);
        FillNative(graphics, bounds, NativeColour(screen, palette, scroll.Background, 0));
        FillNative(graphics, new Rectangle(bounds.X, bounds.Y, bounds.Width, 1), foreground);
        FillNative(graphics, new Rectangle(bounds.X, bounds.Bottom - 1, bounds.Width, 1), foreground);
        FillNative(graphics, new Rectangle(bounds.X, bounds.Y, 1, bounds.Height), foreground);
        FillNative(graphics, new Rectangle(bounds.Right - 1, bounds.Y, 1, bounds.Height), foreground);
        if (NativeWidgetRules.ScrollThumb(scroll.Bounds, span.Range, span.Start, span.End) is { } thumb)
            FillNative(graphics, ToRectangle(thumb), foreground);
    }

    /// <summary>Whether the menu pointer is over <paramref name="bounds"/>.</summary>
    private bool PointerOver(InterfaceRectangle bounds) =>
        _menuPointer is { } pointer && _pointerInsideSurface && ToRectangle(bounds).Contains(pointer);

    /// <summary>Whether the primary button is held over <paramref name="bounds"/>.</summary>
    private bool PressedOver(InterfaceRectangle bounds) => PointerOver(bounds) && MouseButtons.HasFlag(MouseButtons.Left);

    private static Rectangle ToRectangle(InterfaceRectangle bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
}
