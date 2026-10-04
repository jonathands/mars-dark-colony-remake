using DarkColony.App.Diagnostics;
using DarkColony.App.Ui;
using DarkColony.App.Rendering;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.Time;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;
using DarkColony.Engine.Commands;
using DarkColony.Engine.Movement;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Media;

namespace DarkColony.App;

/// <summary>The encyclopedia screen and its animated unit previews.</summary>
public sealed partial class MainForm
{
    private IReadOnlyList<MenuButton> EncyclopediaButtons() =>
    [
        Button(0, 309, 446, 89, 25, "BACK", () => ShowScreen(MenuScreenId.Main)),
        Button(1, 464, 310, 89, 25, "HUMANS", () => SelectEncyclopediaCategory(1), _encyclopediaCategory == 1),
        Button(11, 464, 348, 89, 25, "ARTIFACTS", () => SelectEncyclopediaCategory(2), _encyclopediaCategory == 2),
        Button(2, 464, 386, 89, 25, "GRAYS", () => SelectEncyclopediaCategory(0), _encyclopediaCategory == 0),
        Button(3, 272, 102, 25, 25, "", () => MoveEncyclopediaSelection(-1), artName: "UP"),
        Button(4, 272, 441, 25, 25, "", () => MoveEncyclopediaSelection(1), artName: "DOWN"),
        // `encycloe` identifies these exact native gadgets as LEFT/RIGHT/REW/FFW.
        Button(5, 343, 220, 23, 14, "", () => StepEncyclopediaPreview(-1), artName: "LEFT"),
        Button(6, 445, 220, 23, 14, "", () => StepEncyclopediaPreview(1), artName: "RIGHT"),
        Button(7, 511, 220, 27, 14, "", RewindEncyclopediaPreview, artName: "REW"),
        Button(8, 601, 220, 27, 14, "", FastForwardEncyclopediaPreview, artName: "FFW"),
    ];

    private void SelectEncyclopediaCategory(int category)
    {
        _encyclopediaCategory = category;
        _encyclopediaEntry = 0;
        ResetEncyclopediaPreview();
        ShowScreen(MenuScreenId.Encyclopedia);
        PlayEncyclopediaNarration();
    }

    private void MoveEncyclopediaSelection(int delta)
    {
        var category = Encyclopedia()?.Categories[_encyclopediaCategory];
        if (category is null || category.Entries.Count == 0) return;
        _encyclopediaEntry = (_encyclopediaEntry + delta + category.Entries.Count) % category.Entries.Count;
        ResetEncyclopediaPreview();
        _status = $"Encyclopedia: {category.Entries[_encyclopediaEntry].Name}";
        PlayEncyclopediaNarration();
    }

    private void ResetEncyclopediaPreview()
    {
        _encyclopediaPreviewFrameOffset = 0;
        _encyclopediaPreviewFacingIndex = 0;
        _encyclopediaPreviewPaused = false;
    }

    private void StepEncyclopediaPreview(int delta)
    {
        _encyclopediaPreviewFacingIndex = (_encyclopediaPreviewFacingIndex + delta + 8) % 8;
        _encyclopediaPreviewFrameOffset = 0;
        _encyclopediaPreviewPaused = false;
        _status = delta < 0 ? "Encyclopedia animation: turn left." : "Encyclopedia animation: turn right.";
    }

    private void RewindEncyclopediaPreview()
    {
        _encyclopediaPreviewFrameOffset = 0;
        _encyclopediaPreviewPaused = true;
        _status = "Encyclopedia animation: first frame.";
    }

    private void FastForwardEncyclopediaPreview()
    {
        _encyclopediaPreviewFrameOffset = int.MaxValue;
        _encyclopediaPreviewPaused = true;
        _status = "Encyclopedia animation: last frame.";
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
        if (catalog is null) return;
        var category = catalog.Categories[_encyclopediaCategory];
        _encyclopediaEntry = Math.Clamp(_encyclopediaEntry, 0, category.Entries.Count - 1);
        var selected = category.Entries[_encyclopediaEntry];
        DrawMenuText(graphics, selected.Name.ToUpperInvariant(), new Rectangle(18, 18, 260, 18));
        DrawEncyclopediaArticle(graphics, selected);
        DrawEncyclopediaEntity(graphics, selected);
        DrawEncyclopediaRuntimeStats(graphics, selected);
    }

    private void DrawEncyclopediaRuntimeStats(Graphics graphics, EncyclopediaEntry entry)
    {
        if (_installation is null || !EncyclopediaUnitIdentityCatalog.TryGetEntityId(entry, out var entityId)) return;
        try
        {
            _entityCatalog ??= EntityCatalog.Load(_installation.DataFile("gamestat", "gamestat.txt"));
            if ((uint)entityId >= (uint)_entityCatalog.Entities.Count) return;
            var entity = _entityCatalog[entityId];
            // `encycloe` declares its only nearby in_text at (307,287), 16 chars.
            // Keep this compact and derived from the runtime catalog, leaving
            // the source article prose untouched in its own panel.
            DrawMenuText(graphics, $"HP{entity.Health} SP{entity.MovementSpeed}", new Rectangle(307, 287, 154, 14),
                center: false, remap: Color.FromArgb(91, 203, 0));
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Encyclopedia stat error: {error.Message}";
        }
    }

    private void DrawEncyclopediaArticle(Graphics graphics, EncyclopediaEntry entry)
    {
        if (_installation is null) return;
        try
        {
            if (!_encyclopediaArticles.TryGetValue(entry.ResourceStem, out var article))
            {
                article = EncyclopediaArticle.Load(_installation.RootPath, entry.ResourceStem);
                _encyclopediaArticles.Add(entry.ResourceStem, article);
            }

            var row = 0;
            foreach (var sourceLine in article.Lines)
            {
                foreach (var line in WrapEncyclopediaLine(sourceLine.Text, maximumCharacters: 31))
                {
                    if (row >= 24) return;
                    DrawEncyclopediaArticleLine(graphics, line, sourceLine.Segments, 19, 103 + row * 14);
                    row++;
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FileNotFoundException)
        {
            _status = $"Encyclopedia article error: {error.Message}";
        }
    }

    private static IEnumerable<string> WrapEncyclopediaLine(string source, int maximumCharacters)
    {
        if (source.Length == 0) return [string.Empty];
        var words = source.Split(' ', StringSplitOptions.None);
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (current.Length != 0 && candidate.Length > maximumCharacters)
            {
                lines.Add(current);
                current = word;
            }
            else current = candidate;
        }
        if (current.Length != 0) lines.Add(current);
        return lines;
    }

    private void DrawEncyclopediaArticleLine(Graphics graphics, string line, IReadOnlyList<EncyclopediaArticleSegment> sourceSegments, int x, int y)
    {
        var remaining = line;
        var cursor = x;
        foreach (var segment in sourceSegments)
        {
            if (remaining.Length == 0) break;
            var take = Math.Min(segment.Text.Length, remaining.Length);
            var text = remaining[..take];
            DrawMenuText(graphics, text, new Rectangle(cursor, y, 238 - (cursor - x), 14), center: false,
                remap: EncyclopediaTextColor(segment.PaletteIndex));
            cursor += MenuTextWidth(text);
            remaining = remaining[take..];
        }
    }

    private static Color EncyclopediaTextColor(int paletteIndex) => paletteIndex switch
    {
        0 => Color.FromArgb(238, 63, 63),
        1 => Color.FromArgb(55, 142, 255),
        4 => Color.FromArgb(95, 238, 70),
        _ => Color.FromArgb(215, 225, 195),
    };

    private void DrawEncyclopediaEntity(Graphics graphics, EncyclopediaEntry entry)
    {
        var animation = entry.NativeId switch
        {
            0 => ("gray.fin", "GRAYSTAND0"),
            1 => ("atril.fin", "ATRILSTAND0"),
            2 => ("scyth.fin", "SCYTHSTAND0"),
            3 => ("ortu.fin", "ORTUMOVE0"),
            4 => ("psyc.fin", "PSYCSTAND0"),
            5 => ("slug.fin", "SLUGSTAND0"),
            6 => ("xeno.fin", "XENOSTAND0"),
            7 => ("slom.fin", "SLOMSTAND0"),
            8 => ("sauc.fin", "EASY2"),
            9 => ("zisp.fin", "ZISPSTAND0"),
            10 => ("trooper1.fin", "TROOPER1STAND6"),
            11 => ("barr.fin", "BARRSTAND0"),
            12 => ("reap.fin", "REAPSTAND0"),
            13 => ("scgm.fin", "SCGMMOVE0"),
            14 => ("cyborg.fin", "CYBORGSTAND0"),
            15 => ("expl.fin", "EXPLSTAND0"),
            16 => ("turr.fin", "TURRSTAND0"),
            17 => ("engi.fin", "ENGISTAND0"),
            18 => ("drop.fin", "DROPSTAND0"),
            19 => ("beon.fin", "BEONMOVE0"),
            20 => ("tektara.fin", "TEKTARA"),
            21 => ("mactor.fin", "MACTORSTAND0"),
            22 => ("lens.fin", "LENSSTAND0"),
            23 => ("luna.fin", "LUNAMOVE0"),
            24 => ("hyyk.fin", "HYYKDEPLOY0"),
            _ => ((string File, string Name)?)null,
        };
        if (animation is { } value)
        {
            DrawEncyclopediaAnimationCentered(
                graphics,
                value.File,
                value.Name,
                new Rectangle(304, 8, 328, 208),
                3);
        }
    }

    private bool DrawEncyclopediaAnimationCentered(
        Graphics graphics,
        string fileName,
        string animationName,
        Rectangle viewport,
        ulong ticksPerFrame)
    {
        var animation = EncyclopediaFacingAnimation(fileName, animationName);
        if (animation is null) return false;
        var span = animation.LastFrame - animation.FirstFrame + 1;
        var offset = _encyclopediaPreviewPaused
            ? Math.Clamp(_encyclopediaPreviewFrameOffset, 0, span - 1)
            : (int)((_world.TickCount - _screenStartedAtTick) / ticksPerFrame % (ulong)span);
        var bitmap = AnimationBitmap(fileName, (ushort)(animation.FirstFrame + offset));
        if (bitmap is null) return false;
        var state = graphics.Save();
        graphics.SetClip(viewport);
        graphics.DrawImageUnscaled(bitmap,
            viewport.X + (viewport.Width - bitmap.Width) / 2,
            viewport.Y + (viewport.Height - bitmap.Height) / 2);
        graphics.Restore(state);
        return true;
    }

    private AnimationRange? EncyclopediaFacingAnimation(string fileName, string defaultAnimationName)
    {
        // The normal FIN stand families use named even facing sectors (0..14).
        // LEFT/RIGHT therefore change the selected named family, rather than
        // treating adjacent logical frames as a fake time animation. Some
        // shipped names contain typos or incomplete sectors, so fall back to
        // the entry's documented default when an exact direction is absent.
        var trailingDigit = defaultAnimationName.Length - 1;
        while (trailingDigit >= 0 && char.IsAsciiDigit(defaultAnimationName[trailingDigit])) trailingDigit--;
        if (trailingDigit == defaultAnimationName.Length - 1) return Animation(fileName, defaultAnimationName);
        var prefix = defaultAnimationName[..(trailingDigit + 1)];
        var desiredName = $"{prefix}{_encyclopediaPreviewFacingIndex * 2}";
        return Animation(fileName, desiredName) ?? Animation(fileName, defaultAnimationName);
    }
}
