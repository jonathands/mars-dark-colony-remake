using DarkColony.Engine.Assets;
using DarkColony.Engine.Data;
using DarkColony.Engine.Interface;
using System.Media;

namespace DarkColony.App;

/// <summary>The main-menu credits teletype and the native glyph colour remap.</summary>
public sealed partial class MainForm
{
    // 0x404BAD opens intrface/credits.txt in mfonto5 at (178, 200), 280 x 100,
    // repeat mode 2 (start over once the text has scrolled away).
    private static readonly Rectangle CreditsTeletypeBounds = new(178, 200, 280, 100);

    // 0x427EB1 plays sound2.dat 0x84 (BEEP.WAV) for each visible cell a step draws.
    private const int TeletypeBeepSound = 0x84;

    private TeletypeText? _creditsTeletype;
    private bool _creditsTeletypeUnavailable;
    private NativeColourRemap? _colourRemap;
    private IReadOnlyList<VgaColor>? _menuPalette;
    private readonly Dictionary<(int Glyph, int Colour, int Brightness), Bitmap> _nativeGlyphs = [];
    private long _creditsOpenedAtMilliseconds;
    private long _creditsLastStep;
    private SoundPlayer? _teletypeBeep;
    private long _teletypeBeepEndsAtMilliseconds;
    private long _teletypeBeepMilliseconds;

    private BitmapFont LoadMenuFont()
    {
        if (_installation is null) throw new InvalidOperationException("No installation.");
        return _menuFont ??= new BitmapFont(
            Sprite.Load(_installation.DataFile("intrface", "mfonto5.spr")),
            frameOffset: 31,
            lineHeight: 14);
    }

    private void ResetCreditsTeletype()
    {
        _creditsOpenedAtMilliseconds = Environment.TickCount64;
        _creditsLastStep = 0;
    }

    private void DrawCreditsTeletype(Graphics graphics)
    {
        if (_installation is null || _creditsTeletypeUnavailable) return;
        try
        {
            var font = LoadMenuFont();
            var cell = font.Sprite.Frames[0];
            if (_creditsTeletype is null)
            {
                var (columns, lastRow) = TeletypeText.Layout(CreditsTeletypeBounds.Width, CreditsTeletypeBounds.Height, cell.Width, cell.Height);
                _creditsTeletype = TeletypeText.Parse(File.ReadAllBytes(_installation.DataFile("intrface", "credits.txt")), columns, lastRow);
            }

            if (_activeCanvas is { } canvas) canvas.FillForeground(CreditsTeletypeBounds, Color.Black);
            else graphics.FillRectangle(Brushes.Black, CreditsTeletypeBounds);

            var now = Environment.TickCount64;
            var steps = TeletypeText.StepsAfter(now - _creditsOpenedAtMilliseconds);
            foreach (var glyph in _creditsTeletype.Visible(steps))
            {
                var frame = font.Sprite.Frames[glyph.Cell.Glyph];
                var bitmap = NativeGlyphBitmap(font, glyph.Cell.Glyph, glyph.Cell.Colour, glyph.Brightness);
                if (bitmap is null) continue;
                var x = CreditsTeletypeBounds.X + glyph.Column * (cell.Width + 1) + frame.AnchorX;
                var y = CreditsTeletypeBounds.Y + glyph.Row * (cell.Height + 1) + frame.AnchorY;
                if (_activeCanvas is { } target) target.DrawForeground(GpuBitmap(bitmap), x, y);
                else graphics.DrawImageUnscaled(bitmap, x, y);
            }

            if (steps != _creditsLastStep)
            {
                _creditsLastStep = steps;
                if (_creditsTeletype.Typed(steps)) PlayTeletypeBeep(now);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _creditsTeletypeUnavailable = true;
            _status = $"Credits error: {error.Message}";
        }
    }

    /// <summary>
    /// A font frame drawn through <see cref="NativeColourRemap"/> with the
    /// menu palette (intro.gif); palette index 0 stays transparent, as the
    /// native blitters skip it.
    /// </summary>
    private Bitmap? NativeGlyphBitmap(BitmapFont font, int glyph, int colour, int brightness)
    {
        if (_installation is null) return null;
        if (_nativeGlyphs.TryGetValue((glyph, colour, brightness), out var cached)) return cached;
        var frame = font.Sprite.Frames[glyph];
        if (frame.Width == 0 || frame.Height == 0) return null;
        _colourRemap ??= NativeColourRemap.Load(_installation.ExecutablePath);
        _menuPalette ??= GifPalette.Load(_installation.DataFile("intrface", "intro.gif"));
        var indices = frame.DecodeIndices();
        var rgba = new byte[indices.Length * 4];
        for (var pixel = 0; pixel < indices.Length; pixel++)
        {
            if (indices[pixel] == 0) continue;
            var color = _colourRemap.Map(_menuPalette, indices[pixel], colour, brightness);
            rgba[pixel * 4] = color.Red;
            rgba[pixel * 4 + 1] = color.Green;
            rgba[pixel * 4 + 2] = color.Blue;
            rgba[pixel * 4 + 3] = 255;
        }
        var bitmap = BitmapFromRgba(frame.Width, frame.Height, rgba);
        _nativeGlyphs[(glyph, colour, brightness)] = bitmap;
        return bitmap;
    }

    /// <summary>BEEP.WAV, ignored while the previous one still plays.</summary>
    private void PlayTeletypeBeep(long now)
    {
        if (_installation is null || now < _teletypeBeepEndsAtMilliseconds) return;
        try
        {
            if (_teletypeBeep is null)
            {
                _soundCatalog ??= SoundCatalog.Load(_installation.DataFile("sound"));
                if (!_soundCatalog.Sounds.TryGetValue(TeletypeBeepSound, out var sound)) return;
                var path = Path.Combine(_installation.RootPath, sound.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) return;
                _teletypeBeepMilliseconds = WaveMilliseconds(path);
                _teletypeBeep = new SoundPlayer(path);
                _teletypeBeep.Load();
            }
            _teletypeBeep.Play();
            _teletypeBeepEndsAtMilliseconds = now + _teletypeBeepMilliseconds;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException or InvalidOperationException)
        {
            _status = $"Sound error: {error.Message}";
        }
    }

    /// <summary>A PCM WAV's length: its data chunk over the format's byte rate.</summary>
    private static long WaveMilliseconds(string path)
    {
        var data = File.ReadAllBytes(path);
        var byteRate = 0;
        for (var offset = 12; offset + 8 <= data.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(data, offset, 4);
            var size = BitConverter.ToInt32(data, offset + 4);
            if (id == "fmt " && size >= 12) byteRate = BitConverter.ToInt32(data, offset + 16);
            if (id == "data" && byteRate > 0) return size * 1000L / byteRate;
            offset += 8 + size + (size & 1);
        }
        throw new InvalidDataException($"{path} has no PCM data chunk.");
    }
}
