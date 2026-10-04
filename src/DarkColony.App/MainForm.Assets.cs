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

/// <summary>Original background, FIN animation, and SPR frame decoding into cached GPU images.</summary>
public sealed partial class MainForm
{
    private string BackgroundName() => _screen switch
    {
        MenuScreenId.Main => "intro",
        MenuScreenId.NewGame => "choo",
        MenuScreenId.LoadGame => "loader",
        MenuScreenId.SinglePlayer => "tcpwait",
        MenuScreenId.Encyclopedia => "ency",
        MenuScreenId.NetworkOptions => "net",
        MenuScreenId.Story => "story",
        MenuScreenId.Gameplay => "intrface",
        _ => "intro",
    };

    private Image? Background()
    {
        if (_installation is null) return null;
        var name = BackgroundName();
        if (_backgrounds.TryGetValue(name, out var cached)) return cached;
        var path = _installation.DataFile("intrface", $"{name}.gif");
        if (!File.Exists(path)) return null;
        using var source = Image.FromFile(path);
        var copy = new Bitmap(source);
        _backgrounds[name] = copy;
        return copy;
    }

    private GpuImage GpuBackground(string name, Image background)
    {
        if (_gpuBackgrounds.TryGetValue(name, out var cached)) return cached;
        using var bitmap = new Bitmap(640, 480, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.DrawImage(background, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var bgra = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            var rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var source = y * Math.Abs(data.Stride) + x * 4;
                var destination = (y * bitmap.Width + x) * 4;
                rgba[destination] = bgra[source + 2];
                rgba[destination + 1] = bgra[source + 1];
                rgba[destination + 2] = bgra[source];
                rgba[destination + 3] = bgra[source + 3];
            }
            cached = new GpuImage(bitmap.Width, bitmap.Height, rgba);
            _gpuBackgrounds.Add(name, cached);
            return cached;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private GpuImage GpuColorKeyImage(string name, Image source)
    {
        if (_gpuColorKeyImages.TryGetValue(name, out var cached)) return cached;
        using var bitmap = new Bitmap(640, 480, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var bgra = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            var rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var sourceOffset = y * Math.Abs(data.Stride) + x * 4;
                var destination = (y * bitmap.Width + x) * 4;
                var red = bgra[sourceOffset + 2];
                var green = bgra[sourceOffset + 1];
                var blue = bgra[sourceOffset];
                rgba[destination] = red;
                rgba[destination + 1] = green;
                rgba[destination + 2] = blue;
                rgba[destination + 3] = red == 0 && green == 0 && blue == 0 ? (byte)0 : bgra[sourceOffset + 3];
            }
            cached = new GpuImage(bitmap.Width, bitmap.Height, rgba);
            _gpuColorKeyImages.Add(name, cached);
            return cached;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private GpuImage GpuBitmap(Bitmap bitmap)
    {
        if (_gpuBitmaps.TryGetValue(bitmap, out var cached)) return cached;
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[Math.Abs(data.Stride) * bitmap.Height];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            var rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var source = y * Math.Abs(data.Stride) + x * 4;
                var destination = (y * bitmap.Width + x) * 4;
                rgba[destination] = bgra[source + 2];
                rgba[destination + 1] = bgra[source + 1];
                rgba[destination + 2] = bgra[source];
                rgba[destination + 3] = bgra[source + 3];
            }
            cached = new GpuImage(bitmap.Width, bitmap.Height, rgba);
            _gpuBitmaps.Add(bitmap, cached);
            return cached;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private Bitmap? SpriteFrameBitmap(string spriteName, int frameIndex)
    {
        var key = $"sprite:{spriteName}:{frameIndex}";
        if (_animationFrames.TryGetValue(key, out var cached)) return cached;
        try
        {
            var sprite = LoadSprite(spriteName);
            if ((uint)frameIndex >= (uint)sprite.Frames.Count) return null;
            var frame = sprite.Frames[frameIndex];
            var bitmap = BitmapFromRgba(frame.Width, frame.Height, sprite.FrameRgba(frameIndex));
            _animationFrames[key] = bitmap;
            return bitmap;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FileNotFoundException)
        {
            _status = $"HUD sprite error: {error.Message}";
            return null;
        }
    }

    private bool DrawLoopAnimationCentered(
        Graphics graphics,
        string fileName,
        string animationName,
        Rectangle viewport,
        ulong ticksPerFrame)
    {
        var animation = Animation(fileName, animationName);
        if (animation is null) return false;
        var span = animation.LastFrame - animation.FirstFrame + 1;
        var age = _world.TickCount - _screenStartedAtTick;
        var frame = animation.FirstFrame + (ushort)((age / ticksPerFrame) % (ulong)span);
        var bitmap = AnimationBitmap(fileName, frame);
        if (bitmap is null) return false;

        var state = graphics.Save();
        graphics.SetClip(viewport);
        graphics.DrawImageUnscaled(
            bitmap,
            viewport.X + (viewport.Width - bitmap.Width) / 2,
            viewport.Y + (viewport.Height - bitmap.Height) / 2);
        graphics.Restore(state);
        return true;
    }

    private bool DrawLoopAnimation(
        Graphics graphics,
        string fileName,
        string animationName,
        int x,
        int y,
        ulong ticksPerFrame)
    {
        var animation = Animation(fileName, animationName);
        if (animation is null) return false;
        var span = animation.LastFrame - animation.FirstFrame + 1;
        var age = _world.TickCount - _screenStartedAtTick;
        var frame = animation.FirstFrame + (ushort)((age / ticksPerFrame) % (ulong)span);
        return DrawAnimationFrame(graphics, fileName, frame, x, y);
    }

    private bool DrawStoppedAnimation(Graphics graphics, string fileName, string animationName, int x, int y)
    {
        var animation = Animation(fileName, animationName);
        return animation is not null && DrawAnimationFrame(graphics, fileName, animation.FirstFrame, x, y);
    }

    private AnimationRange? Animation(string fileName, string animationName)
    {
        if (_installation is null) return null;
        try
        {
            if (!_animationDefinitions.TryGetValue(fileName, out var definition))
            {
                definition = AnimationDefinition.Load(_installation.DataFile("animate", fileName));
                _animationDefinitions[fileName] = definition;
            }

            return definition.Animations.FirstOrDefault(item => item.Name == animationName);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private bool DrawAnimationFrame(
        Graphics graphics,
        string fileName,
        int frameIndex,
        int x,
        int y,
        float opacity = 1f,
        bool remapWarControlPalette = false)
    {
        var bitmap = AnimationBitmap(fileName, frameIndex, remapWarControlPalette);
        if (bitmap is null) return false;
        // Interface source rectangles already specify the gadget origin. FIN
        // layer offsets were consumed while composing/cropping the bitmap and
        // must not be applied a second time here.
        if (opacity >= 1f && _activeCanvas is { } canvas)
            canvas.DrawForeground(GpuBitmap(bitmap), x, y);
        else if (opacity >= 1f) graphics.DrawImageUnscaled(bitmap, x, y);
        else
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
            graphics.DrawImage(bitmap, new Rectangle(x, y, bitmap.Width, bitmap.Height), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
        }
        return true;
    }

    private Bitmap? AnimationBitmap(string fileName, int frameIndex, bool remapWarControlPalette = false)
    {
        if (_installation is null) return null;
        var key = $"{fileName}:{frameIndex}:{(remapWarControlPalette ? "war-controls" : "base")}";
        try
        {
            if (_animationFrames.TryGetValue(key, out var cached)) return cached;
            if (!_animationDefinitions.TryGetValue(fileName, out var definition))
            {
                definition = AnimationDefinition.Load(_installation.DataFile("animate", fileName));
                _animationDefinitions[fileName] = definition;
            }

            var composite = definition.Compose(frameIndex, LoadSprite);
            if (remapWarControlPalette && fileName.Equals("knobe.fin", StringComparison.OrdinalIgnoreCase))
            {
                // `multie` uses the same knobe sprites as the green menus,
                // but dc.exe applies its UI palette bank before drawing them.
                // The captured native War screen maps the three base knobe
                // shades (12,36,0 / 28,77,0 / 48,117,0) to these exact
                // neutral/red shades.  Replace only those source colors:
                // recoloring every opaque pixel would destroy the composite's
                // black, grey, cyan, and glyph details.
                RemapWarControlPalette(composite.Rgba);
            }
            var bitmap = BitmapFromRgba(composite);
            _animationFrames[key] = bitmap;
            _animationOrigins[key] = new Point(composite.X, composite.Y);
            return bitmap;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentOutOfRangeException)
        {
            _status = $"Animation error: {error.Message}";
            return null;
        }
    }

    private Rectangle AnimationOpaqueBounds(string key, Bitmap bitmap)
    {
        if (_animationOpaqueBounds.TryGetValue(key, out var cached)) return cached;
        var left = bitmap.Width;
        var top = bitmap.Height;
        var right = -1;
        var bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).A == 0) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        var bounds = right < left
            ? new Rectangle(0, 0, bitmap.Width, bitmap.Height)
            : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        _animationOpaqueBounds[key] = bounds;
        return bounds;
    }

    private Sprite LoadSprite(string name)
    {
        if (_installation is null) throw new InvalidOperationException("Original data is not available.");
        if (_sprites.TryGetValue(name, out var cached)) return cached;
        foreach (var directory in new[] { "sprites", "intrface" })
        {
            var path = _installation.DataFile(directory, $"{name}.spr");
            if (!File.Exists(path)) continue;
            var sprite = Sprite.Load(path);
            _sprites[name] = sprite;
            return sprite;
        }

        throw new FileNotFoundException($"Missing animation sprite {name}.spr.");
    }

    private static Bitmap BitmapFromRgba(CompositeFrame frame)
        => BitmapFromRgba(frame.Width, frame.Height, frame.Rgba);

    private static Bitmap BitmapFromRgba(int width, int height, byte[] rgba)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[rgba.Length];
            for (var index = 0; index < rgba.Length; index += 4)
            {
                bgra[index] = rgba[index + 2];
                bgra[index + 1] = rgba[index + 1];
                bgra[index + 2] = rgba[index];
                bgra[index + 3] = rgba[index + 3];
            }

            Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static ushort LastVisibleFrame(AnimationRange animation) =>
        animation.LastFrame > animation.FirstFrame ? (ushort)(animation.LastFrame - 1) : animation.FirstFrame;
}
