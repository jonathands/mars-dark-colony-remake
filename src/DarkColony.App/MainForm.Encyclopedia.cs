using DarkColony.App.Rendering;
using DarkColony.App.Ui;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Data;
using DarkColony.Engine.Interface;
using System.Media;

namespace DarkColony.App;

/// <summary>
/// The encyclopedia (<c>0x402640</c>, menu <c>intrface/encycloe</c>). Each
/// entry opens two widgets. The article <c>encyclo/&lt;stem&gt;.txt</c> is a
/// mode 1 teletype at (20, 106), 238 x 356, interval 1. The preview
/// <c>encyclo/&lt;stem&gt;.spr</c> is a picture widget at (303, 13), interval 0x21.
/// </summary>
public sealed partial class MainForm
{
    private static readonly Rectangle EncyclopediaArticleBounds = new(20, 106, 238, 356);
    private static readonly Point EncyclopediaPreviewOrigin = new(303, 13);
    private const int EncyclopediaArticleInterval = 1;
    // 0x40284D: UP/DOWN repeat while held, once more than 0x42 ms have passed.
    private const int EncyclopediaScrollMilliseconds = 0x42;

    private string? _encyclopediaShownKey;
    private TeletypeText? _encyclopediaArticleText;
    private long _encyclopediaArticleOpenedAt;
    private int? _encyclopediaArticleTop;
    private long _encyclopediaLastScrollAt;
    private Sprite? _encyclopediaPreviewSprite;
    private PictureAnimation? _encyclopediaPreview;
    private bool _encyclopediaPreviewStopped;
    private readonly Dictionary<int, byte[]> _encyclopediaPreviewFrames = [];
    private NativeEncyclopediaLabels? _encyclopediaLabels;

    private IReadOnlyList<MenuButton> EncyclopediaButtons() =>
    [
        Button(0, 309, 446, 89, 25, "BACK", () => ShowScreen(MenuScreenId.Main)),
        Button(1, 464, 310, 89, 25, "HUMANS", () => SelectEncyclopediaCategory(1), _encyclopediaCategory == 1),
        Button(11, 464, 348, 89, 25, "ARTIFACTS", () => SelectEncyclopediaCategory(2), _encyclopediaCategory == 2),
        Button(2, 464, 386, 89, 25, "GRAYS", () => SelectEncyclopediaCategory(0), _encyclopediaCategory == 0),
        // 0x402835/0x402887: UP and DOWN scroll the article while held (see UpdateEncyclopedia).
        Button(3, 272, 102, 25, 25, "", () => { }, artName: "UP"),
        Button(4, 272, 441, 25, 25, "", () => { }, artName: "DOWN"),
        // 0x4027FB/0x402818: LEFT and RIGHT turn the preview backward or forward.
        Button(5, 343, 220, 23, 14, "", () => SetEncyclopediaPreviewCommand(PictureCommand.Backward), artName: "LEFT"),
        Button(6, 445, 220, 23, 14, "", () => SetEncyclopediaPreviewCommand(PictureCommand.Forward), artName: "RIGHT"),
        // 0x40290A/0x402A52: REW and FFW step to the previous and next entry, wrapping.
        Button(7, 511, 220, 27, 14, "", () => MoveEncyclopediaSelection(-1), artName: "REW"),
        Button(8, 601, 220, 27, 14, "", () => MoveEncyclopediaSelection(1), artName: "FFW"),
        // 0x4028D9/0x4028EF: the bare pushbuttons 9 and 10 stop and play the preview.
        Button(9, 370, 220, 23, 14, "", StopEncyclopediaPreview, artName: string.Empty),
        Button(10, 400, 220, 23, 14, "", PlayEncyclopediaPreview, artName: string.Empty),
    ];

    private void SelectEncyclopediaCategory(int category)
    {
        _encyclopediaCategory = category;
        _encyclopediaEntry = 0;
        ShowScreen(MenuScreenId.Encyclopedia);
        PlayEncyclopediaNarration();
    }

    private void MoveEncyclopediaSelection(int delta)
    {
        var category = Encyclopedia()?.Categories[_encyclopediaCategory];
        if (category is null || category.Entries.Count == 0) return;
        _encyclopediaEntry = (_encyclopediaEntry + delta + category.Entries.Count) % category.Entries.Count;
        _encyclopediaShownKey = null;
        PlayEncyclopediaNarration();
    }

    /// <summary>
    /// LEFT/RIGHT keep their direction after release, unless pushbutton 9
    /// stopped the preview. Then the loop end (<c>0x402F67</c>) puts it back on
    /// hold, so it only turns while the button is held.
    /// </summary>
    private void SetEncyclopediaPreviewCommand(PictureCommand command)
    {
        if (_encyclopediaPreview is { } preview && !_encyclopediaPreviewStopped) preview.Command = command;
    }

    private void StopEncyclopediaPreview()
    {
        _encyclopediaPreviewStopped = true;
        if (_encyclopediaPreview is { } preview) preview.Command = PictureCommand.Hold;
    }

    private void PlayEncyclopediaPreview()
    {
        _encyclopediaPreviewStopped = false;
        if (_encyclopediaPreview is { } preview) preview.Command = PictureCommand.Forward;
    }

    private void PlayEncyclopediaNarration()
    {
        if (_installation is null || Encyclopedia()?.Categories[_encyclopediaCategory] is not { } category ||
            (uint)_encyclopediaEntry >= (uint)category.Entries.Count) return;
        try
        {
            var entry = category.Entries[_encyclopediaEntry];
            var relativePath = Path.ChangeExtension(entry.ResourceStem.Replace('/', Path.DirectorySeparatorChar), ".WAV");
            var path = Path.Combine(_installation.RootPath, relativePath);
            if (File.Exists(path)) new SoundPlayer(path).Play();
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Encyclopedia narration error: {error.Message}";
        }
    }

    private EncyclopediaCatalog? Encyclopedia()
    {
        if (_encyclopedia is not null || _installation is null) return _encyclopedia;
        try
        {
            _encyclopedia = EncyclopediaCatalog.Load(_installation.DataFile("intrface", "encyclo.txt"));
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Encyclopedia error: {error.Message}";
        }

        return _encyclopedia;
    }

    private void DrawEncyclopedia(Graphics graphics)
    {
        var catalog = Encyclopedia();
        if (catalog is null || _installation is null) return;
        var category = catalog.Categories[_encyclopediaCategory];
        _encyclopediaEntry = Math.Clamp(_encyclopediaEntry, 0, category.Entries.Count - 1);
        var selected = category.Entries[_encyclopediaEntry];
        var key = $"{_encyclopediaCategory}:{_encyclopediaEntry}:{_screenStartedAtTick}";
        if (key != _encyclopediaShownKey)
        {
            _encyclopediaShownKey = key;
            OpenEncyclopediaEntry(selected);
        }

        var now = Environment.TickCount64;
        UpdateEncyclopedia(now);
        DrawEncyclopediaLabels(graphics, selected);
        DrawEncyclopediaArticle(graphics, now);
        DrawEncyclopediaPreview(graphics);
    }

    private void OpenEncyclopediaEntry(EncyclopediaEntry entry)
    {
        var root = _installation!.RootPath;
        _encyclopediaArticleText = null;
        _encyclopediaArticleTop = null;
        _encyclopediaArticleOpenedAt = Environment.TickCount64;
        _encyclopediaPreviewSprite = null;
        _encyclopediaPreview = null;
        _encyclopediaPreviewStopped = false;
        _encyclopediaPreviewFrames.Clear();
        try
        {
            var font = LoadMenuFont().Sprite.Frames[0];
            var (columns, lastRow) = TeletypeText.Layout(EncyclopediaArticleBounds.Width, EncyclopediaArticleBounds.Height, font.Width, font.Height);
            var article = Path.Combine(root, entry.ResourceStem.Replace('/', Path.DirectorySeparatorChar) + ".txt");
            if (File.Exists(article)) _encyclopediaArticleText = TeletypeText.Parse(File.ReadAllBytes(article), columns, lastRow, repeat: false);
            var preview = Path.Combine(root, entry.ResourceStem.Replace('/', Path.DirectorySeparatorChar) + ".spr");
            if (File.Exists(preview))
            {
                _encyclopediaPreviewSprite = Sprite.Load(preview);
                if (_encyclopediaPreviewSprite.Frames.Count >= 2) _encyclopediaPreview = new PictureAnimation(_encyclopediaPreviewSprite.Frames.Count);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Encyclopedia entry error: {error.Message}";
        }
    }

    private void UpdateEncyclopedia(long now)
    {
        if (_encyclopediaPreview is { } preview)
        {
            // 0x4278A8: a held LEFT/RIGHT sets the direction for this update.
            if (_pressedButton == 5) preview.Command = PictureCommand.Backward;
            else if (_pressedButton == 6) preview.Command = PictureCommand.Forward;
            preview.Advance(now);
            if (_encyclopediaPreviewStopped) preview.Command = PictureCommand.Hold;
        }

        if (_pressedButton is 3 or 4 && _encyclopediaArticleText is { } article &&
            now - _encyclopediaLastScrollAt > EncyclopediaScrollMilliseconds)
        {
            _encyclopediaLastScrollAt = now;
            var steps = TeletypeText.StepsAfter(now - _encyclopediaArticleOpenedAt, EncyclopediaArticleInterval);
            // The teletype only scrolls once typing is done (state 2).
            if (article.Finished(steps))
            {
                var top = _encyclopediaArticleTop ?? article.TopLine(steps);
                _encyclopediaArticleTop = _pressedButton == 3 ? article.ScrollUp(top) : article.ScrollDown(top);
            }
        }
    }

    private void DrawEncyclopediaLabels(Graphics graphics, EncyclopediaEntry entry)
    {
        if (_installation is null) return;
        try
        {
            _encyclopediaLabels ??= NativeEncyclopediaLabels.Load(_installation.ExecutablePath);
            // encycloe: in_text 17 (20,20) in font 1, 16 (307,287), 12 (570,340) and 13 (570,417).
            DrawCellText(graphics, entry.Name, 20, 20, LoadFont("mfonto2"), colour: 2, palette: "ency");
            DrawCellText(graphics, _encyclopediaLabels.CategoryTitle(_encyclopediaCategory), 307, 287, LoadMenuFont(), colour: 2, palette: "ency");
            DrawCellText(graphics, _encyclopediaLabels.Earth, 570, 340, LoadMenuFont(), colour: 4, palette: "ency");
            DrawCellText(graphics, _encyclopediaLabels.Mars, 570, 417, LoadMenuFont(), colour: 4, palette: "ency");
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Encyclopedia label error: {error.Message}";
        }
    }

    private void DrawEncyclopediaArticle(Graphics graphics, long now)
    {
        if (_encyclopediaArticleText is not { } article) return;
        var font = LoadMenuFont();
        var cell = font.Sprite.Frames[0];
        FillNative(graphics, EncyclopediaArticleBounds, Color.Black);
        var steps = TeletypeText.StepsAfter(now - _encyclopediaArticleOpenedAt, EncyclopediaArticleInterval);
        var glyphs = _encyclopediaArticleTop is { } top && article.Finished(steps) ? article.VisibleScrolled(top) : article.Visible(steps);
        foreach (var glyph in glyphs)
        {
            var frame = font.Sprite.Frames[glyph.Cell.Glyph];
            var bitmap = NativeGlyphBitmap(font, glyph.Cell.Glyph, glyph.Cell.Colour, glyph.Brightness, "ency");
            if (bitmap is null) continue;
            DrawNative(graphics, bitmap,
                EncyclopediaArticleBounds.X + glyph.Column * (cell.Width + 1) + frame.AnchorX,
                EncyclopediaArticleBounds.Y + glyph.Row * (cell.Height + 1) + frame.AnchorY);
        }
    }

    /// <summary>The box (frame 0's size) cleared to black, then the current frame at its anchor.</summary>
    private void DrawEncyclopediaPreview(Graphics graphics)
    {
        if (_encyclopediaPreviewSprite is not { } sprite || _encyclopediaPreview is not { } preview) return;
        var box = sprite.Frames[0];
        FillNative(graphics, new Rectangle(EncyclopediaPreviewOrigin, new Size(box.Width, box.Height)), Color.Black);
        var frame = sprite.Frames[preview.Frame];
        if (frame.Width == 0 || frame.Height == 0) return;
        if (!_encyclopediaPreviewFrames.TryGetValue(preview.Frame, out var rgba))
        {
            rgba = sprite.FrameRgba(preview.Frame, palette: ScreenPalette("ency"));
            _encyclopediaPreviewFrames[preview.Frame] = rgba;
        }
        var x = EncyclopediaPreviewOrigin.X + frame.AnchorX;
        var y = EncyclopediaPreviewOrigin.Y + frame.AnchorY;
        if (_activeCanvas is { } canvas) canvas.DrawForeground(new GpuImage(frame.Width, frame.Height, rgba, transient: true), x, y);
        else
        {
            using var bitmap = BitmapFromRgba(frame.Width, frame.Height, rgba);
            graphics.DrawImageUnscaled(bitmap, x, y);
        }
    }
}
