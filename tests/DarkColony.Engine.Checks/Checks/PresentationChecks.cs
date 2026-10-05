using System.Drawing;
using DarkColony.Engine.Data;
using DarkColony.Presentation;
using static CheckHelpers;

/// <summary>Display layout, display settings and the gameplay HUD at other screen sizes.</summary>
internal static class PresentationChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Presentation", name, tags, action);

        Check("the classic gameplay screen is the original 640x480 layout, unmoved", () =>
        {
            var screen = GameplayScreen.Classic;
            Equal((new Rectangle(4, 6, 512, 448), new Size(516, 458), new Point(260, 230), Point.Empty),
                (screen.Viewport, screen.WorldArea, screen.ViewCentre, screen.PopupOffset));
            for (var y = 0; y < 480; y++) if (screen.SourceRow(y) != y) throw new InvalidOperationException($"row {y}");
            for (var x = 0; x < 640; x++) if (screen.SourceColumn(x) != x) throw new InvalidOperationException($"column {x}");
            Equal(new Rectangle(519, 6, 96, 84), screen.Anchor(new Rectangle(519, 6, 96, 84)));
            Equal(true, Throws(() => new GameplayScreen(new Size(639, 480))));
        });

        Check("a larger gameplay screen anchors the HUD to its edges and repeats plain bands of the picture", () =>
        {
            var screen = new GameplayScreen(new Size(1280, 720));
            Equal((new Size(640, 240), new Rectangle(4, 6, 1152, 688), new Size(1156, 698), new Point(580, 350), new Point(320, 120)),
                (screen.Extra, screen.Viewport, screen.WorldArea, screen.ViewCentre, screen.PopupOffset));
            // Minimap and tabs move right; the identity strip, BUILD and P7 also move down;
            // the message buttons and lower readouts only move down.
            Equal((new Point(1159, 6), new Point(1161, 96), new Point(1160, 644), new Point(1164, 696), new Point(4, 700), new Point(10, 665)),
                (screen.Anchor(new Point(519, 6)), screen.Anchor(new Point(521, 96)), screen.Anchor(new Point(520, 404)),
                 screen.Anchor(new Point(524, 456)), screen.Anchor(new Point(4, 460)), screen.Anchor(new Point(10, 425))));
            // The picture: itself before the split, shifted after the inserted stretch,
            // and the band in between, phased to end on the column just before the
            // split (640 extra columns: the stretch starts at band column 360).
            Equal((399, 360, 399, 400, 639), (screen.SourceColumn(399), screen.SourceColumn(400), screen.SourceColumn(1039), screen.SourceColumn(1040), screen.SourceColumn(1279)));
            Equal((391, 391, 392, 479), (screen.SourceRow(391), screen.SourceRow(631), screen.SourceRow(632), screen.SourceRow(719)));
            for (var x = 400; x < 1040; x++)
                if (screen.SourceColumn(x) is < 200 or >= 400) throw new InvalidOperationException($"column {x} -> {screen.SourceColumn(x)}");
            for (var y = 392; y < 632; y++)
                if (screen.SourceRow(y) is < 342 or >= 392) throw new InvalidOperationException($"row {y} -> {screen.SourceRow(y)}");
        });

        Check("maine's controls land on the anchored HUD at three screen sizes, clear of the world view", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var maine = InterfaceDefinition.Load(install.DataFile("intrface", "maine"));
            foreach (var size in new[] { new Size(640, 480), new Size(1280, 720), new Size(1920, 1080) })
            {
                var screen = new GameplayScreen(size);
                var layout = GameplayHudLayout.Load(install, screen);
                var (dw, dh) = (screen.Extra.Width, screen.Extra.Height);
                Equal((new Point(518 + dw, 112), new Point(520 + dw, 404 + dh), new Point(50, 462 + dh), new Point(4, 460 + dh), new Point(521 + dw, 96)),
                    (layout.Stop.Bounds.Location, layout.PanelIdentity.Origin, layout.MessageStatus.Origin, layout.LastMessage.Bounds.Location, layout.TabStrip.Location));
                Equal(new Rectangle(518 + dw, 112, 59, 41), layout.CatalogBounds(87, Rectangle.Empty));
                // Every panel gadget right of the view stays right of it and on the screen.
                foreach (var control in maine.Controls.Values.Where(control => control.Bounds.X >= 516))
                {
                    var bounds = screen.Anchor(new Rectangle(control.Bounds.X, control.Bounds.Y, control.Bounds.Width, control.Bounds.Height));
                    if (bounds.X < screen.Viewport.Right || bounds.Right > size.Width || bounds.Y < 0 || bounds.Y >= size.Height)
                        throw new InvalidOperationException($"{size.Width}x{size.Height}: control {control.Id} at {bounds}");
                }
            }
        }, CheckTags.Data);

        Check("the classic picture on a 640x480 output is drawn 1:1", () =>
        {
            var layout = DisplayLayout.Compute(new Size(640, 480), new Size(640, 480), ScaleMode.Integer);
            Equal(new Rectangle(0, 0, 640, 480), layout.Destination);
            Equal((true, 1), (layout.PixelExact, layout.IntegerScale));
            for (var y = 0; y < 480; y += 7)
            for (var x = 0; x < 640; x += 5)
                Equal(new Point(x, y), layout.ToLogical(new Point(x, y)));
            Equal(DisplayLayout.Classic, layout);
        });

        Check("integer scaling takes the largest whole scale and centres the picture between bars", () =>
        {
            var full = DisplayLayout.Compute(new Size(640, 480), new Size(1920, 1080), ScaleMode.Integer);
            Equal((new Rectangle(320, 60, 1280, 960), 2, true), (full.Destination, full.IntegerScale, full.PixelExact));
            var wide = DisplayLayout.Compute(new Size(960, 540), new Size(1920, 1080), ScaleMode.Integer);
            Equal((new Rectangle(0, 0, 1920, 1080), 2), (wide.Destination, wide.IntegerScale));
            // An output smaller than the picture is fitted instead, and filtered.
            var small = DisplayLayout.Compute(new Size(640, 480), new Size(600, 400), ScaleMode.Integer);
            Equal((new Rectangle(33, 0, 533, 400), false, 0), (small.Destination, small.PixelExact, small.IntegerScale));
            Equal(Rectangle.Empty, DisplayLayout.Compute(new Size(640, 480), new Size(0, 0), ScaleMode.Integer).Destination);
        });

        Check("fit keeps the picture's shape and stretch fills the output", () =>
        {
            var fit = DisplayLayout.Compute(new Size(640, 480), new Size(1920, 1080), ScaleMode.Fit);
            Equal((new Rectangle(240, 0, 1440, 1080), false, new Size(2, 2)), (fit.Destination, fit.PixelExact, fit.Prescale));
            var tall = DisplayLayout.Compute(new Size(640, 480), new Size(1000, 1200), ScaleMode.Fit);
            Equal(new Rectangle(0, 225, 1000, 750), tall.Destination);
            var stretch = DisplayLayout.Compute(new Size(640, 480), new Size(1920, 1080), ScaleMode.Stretch);
            Equal((new Rectangle(0, 0, 1920, 1080), new Size(3, 2)), (stretch.Destination, stretch.Prescale));
        });

        Check("output points map to the logical pixel they show, and bars clamp to the nearest edge", () =>
        {
            Size[] outputs = [new(640, 480), new(1280, 960), new(1920, 1080), new(1366, 768), new(2560, 1440), new(800, 600), new(3840, 2160)];
            Size[] logicals = [new(640, 480), new(960, 540), new(1280, 720)];
            foreach (var mode in Enum.GetValues<ScaleMode>())
            foreach (var output in outputs)
            foreach (var logical in logicals)
            {
                var layout = DisplayLayout.Compute(logical, output, mode);
                // A picture shrunk below its size cannot keep every pixel.
                if (layout.Destination.Width >= logical.Width && layout.Destination.Height >= logical.Height)
                for (var y = 0; y < logical.Height; y += 13)
                for (var x = 0; x < logical.Width; x += 11)
                {
                    var point = new Point(x, y);
                    if (layout.ToLogical(layout.ToOutput(point)) != point)
                        throw new InvalidOperationException($"{layout}: {point} -> {layout.ToOutput(point)} -> {layout.ToLogical(layout.ToOutput(point))}");
                }
                var destination = layout.Destination;
                Equal(Point.Empty, layout.ToLogical(new Point(destination.X - 1, destination.Y - 1)));
                Equal(new Point(logical.Width - 1, logical.Height - 1), layout.ToLogical(new Point(output.Width + 50, output.Height + 50)));
                if (destination.Width < logical.Width || destination.Height < logical.Height) continue;
                Equal(new Point(logical.Width - 1, logical.Height - 1), layout.ToLogical(new Point(destination.Right - 1, destination.Bottom - 1)));
                Equal(Point.Empty, layout.ToLogical(destination.Location));
            }
        });

        Check("the Video panel's arrows cycle each row and only a new exclusive mode asks to be kept", () =>
        {
            DisplayModeChoice[] modes = [new(800, 600, 60), new(1280, 720, 60), new(1920, 1080, 144)];
            var settings = new DisplaySettings();
            DisplaySettings Step(DisplaySettings value, DisplaySettingRow row, int direction = 1) => DisplaySettingsEditor.Step(value, row, direction, modes);
            string Value(DisplaySettings value, DisplaySettingRow row) => DisplaySettingsEditor.Value(value, row);

            Equal(("WINDOW", "AUTO", "INTEGER", "CLASSIC", "ON", "FULLSCREEN"),
                (Value(settings, DisplaySettingRow.Mode), Value(settings, DisplaySettingRow.Size), Value(settings, DisplaySettingRow.Scaling),
                 Value(settings, DisplaySettingRow.View), Value(settings, DisplaySettingRow.VSync), Value(settings, DisplaySettingRow.PointerLock)));
            Equal(("FULLSCREEN", "EXCLUSIVE", "EXCLUSIVE"),
                (Value(Step(settings, DisplaySettingRow.Mode), DisplaySettingRow.Mode),
                 Value(Step(Step(settings, DisplaySettingRow.Mode), DisplaySettingRow.Mode), DisplaySettingRow.Mode),
                 Value(Step(settings, DisplaySettingRow.Mode, -1), DisplaySettingRow.Mode)));
            // Windowed sizes are the window scales; 8 wraps to automatic.
            Equal(("1X", "AUTO"), (Value(Step(settings, DisplaySettingRow.Size), DisplaySettingRow.Size),
                Value(Step(settings with { WindowScale = 8 }, DisplaySettingRow.Size), DisplaySettingRow.Size)));
            // Borderless keeps the desktop's size; exclusive steps through the monitor's modes after "desktop".
            var borderless = settings with { Mode = WindowMode.Borderless };
            Equal((borderless, "DESKTOP"), (Step(borderless, DisplaySettingRow.Size), Value(borderless, DisplaySettingRow.Size)));
            var exclusive = settings with { Mode = WindowMode.Exclusive };
            Equal(("DESKTOP", "800X600", "1920X1080"), (Value(exclusive, DisplaySettingRow.Size),
                Value(Step(exclusive, DisplaySettingRow.Size), DisplaySettingRow.Size), Value(Step(exclusive, DisplaySettingRow.Size, -1), DisplaySettingRow.Size)));
            Equal(new DisplayModeChoice(1920, 1080, 144), Step(exclusive, DisplaySettingRow.Size, -1).ExclusiveMode ?? default);
            Equal(("FIT", "AUTO", "800X600", "OFF", "ALWAYS", "NEVER"),
                (Value(Step(settings, DisplaySettingRow.Scaling), DisplaySettingRow.Scaling),
                 Value(Step(settings, DisplaySettingRow.View), DisplaySettingRow.View),
                 Value(Step(Step(settings, DisplaySettingRow.View), DisplaySettingRow.View), DisplaySettingRow.View),
                 Value(Step(settings, DisplaySettingRow.VSync), DisplaySettingRow.VSync),
                 Value(Step(settings, DisplaySettingRow.PointerLock), DisplaySettingRow.PointerLock),
                 Value(Step(settings, DisplaySettingRow.PointerLock, -1), DisplaySettingRow.PointerLock)));
            Equal("1920X1080", Value(Step(settings, DisplaySettingRow.View, -1), DisplaySettingRow.View));
            Equal(("ORIGINAL", "MODERN", "ORIGINAL"), (Value(settings, DisplaySettingRow.Mouse),
                Value(Step(settings, DisplaySettingRow.Mouse), DisplaySettingRow.Mouse),
                Value(Step(Step(settings, DisplaySettingRow.Mouse, -1), DisplaySettingRow.Mouse), DisplaySettingRow.Mouse)));
            // The intro plays, and the FPS counter and the debugging guides are
            // hidden, until a row turns them over.
            Equal(("ON", "OFF", "OFF", "ON"),
                (Value(settings, DisplaySettingRow.IntroVideo), Value(settings, DisplaySettingRow.ShowFps),
                 Value(Step(settings, DisplaySettingRow.IntroVideo), DisplaySettingRow.IntroVideo),
                 Value(Step(settings, DisplaySettingRow.ShowFps, -1), DisplaySettingRow.ShowFps)));
            Equal(("DEBUG GUIDES", "OFF", "ON", "OFF"),
                (DisplaySettingsEditor.Label(DisplaySettingRow.Guides), Value(settings, DisplaySettingRow.Guides),
                 Value(Step(settings, DisplaySettingRow.Guides), DisplaySettingRow.Guides),
                 Value(Step(Step(settings, DisplaySettingRow.Guides), DisplaySettingRow.Guides, -1), DisplaySettingRow.Guides)));

            var chosen = Step(exclusive, DisplaySettingRow.Size);
            Equal((true, true, false, false),
                (DisplaySettingsEditor.NeedsConfirmation(settings, exclusive), DisplaySettingsEditor.NeedsConfirmation(exclusive, chosen),
                 DisplaySettingsEditor.NeedsConfirmation(chosen, chosen with { VSync = false }), DisplaySettingsEditor.NeedsConfirmation(chosen, borderless)));
        });

        static bool Throws(Action action)
        {
            try { action(); return false; }
            catch (ArgumentOutOfRangeException) { return true; }
        }

        Check("display settings parse their flags, reject bad values and survive a save", () =>
        {
            var settings = new DisplaySettings().WithArguments(
                ["--data", "x", "--fullscreen", "--window-scale", "3", "--scale-mode", "fit", "--view", "1280x720", "--vsync", "off", "--confine-cursor", "on"],
                out var problems);
            Equal(0, problems.Count);
            Equal((WindowMode.Borderless, 3, ScaleMode.Fit, "1280x720", false, (bool?)true),
                (settings.Mode, settings.WindowScale, settings.Scale, settings.View, settings.VSync, settings.ConfineCursor));
            // Unset, the pointer is confined in fullscreen only.
            Equal((false, true, false), (new DisplaySettings().ConfinesCursor, new DisplaySettings { Mode = WindowMode.Borderless }.ConfinesCursor,
                new DisplaySettings { Mode = WindowMode.Exclusive, ConfineCursor = false }.ConfinesCursor));
            Equal(new Size(1280, 720), settings.GameplayViewFor(new Size(1920, 1080)));

            var exclusive = settings.WithArguments(["--exclusive", "1024x768@75"], out _);
            Equal((WindowMode.Exclusive, (DisplayModeChoice?)new DisplayModeChoice(1024, 768, 75)), (exclusive.Mode, exclusive.ExclusiveMode));

            var rejected = settings.WithArguments(["--window-scale", "12", "--view", "320x200", "--scale-mode", "blur", "--exclusive", "huge"], out problems);
            Equal((4, 3, "1280x720", ScaleMode.Fit, WindowMode.Borderless), (problems.Count, rejected.WindowScale, rejected.View, rejected.Scale, rejected.Mode));
            var extras = settings.WithArguments(["--intro-video", "off", "--show-fps", "on", "--show-fps", "maybe", "--mouse", "modern", "--mouse", "left",
                "--guides", "on", "--guides", "loud"], out problems);
            Equal((3, false, true, MouseControls.Modern, true), (problems.Count, extras.IntroVideo, extras.ShowFps, extras.Mouse, extras.ShowGuides));
            Equal(false, new DisplaySettings().ShowGuides);

            // auto divides the output by the largest whole scale that keeps 640x480.
            var auto = new DisplaySettings { View = "auto" };
            Equal((new Size(960, 540), new Size(853, 480), new Size(1366, 768), new Size(640, 480)),
                (auto.GameplayViewFor(new Size(1920, 1080)), auto.GameplayViewFor(new Size(2560, 1440)),
                 auto.GameplayViewFor(new Size(1366, 768)), auto.GameplayViewFor(new Size(600, 400))));
            Equal((true, false, true), (new DisplaySettings().IsClassicView, auto.IsClassicView, new DisplaySettings { View = "640x480" }.IsClassicView));

            var path = Path.Combine(Path.GetTempPath(), $"dc-display-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
            try
            {
                exclusive.Save(path);
                Equal(exclusive, DisplaySettings.Load(path, out var problem));
                Equal(true, problem is null);
                File.WriteAllText(path, "{ not json");
                Equal(new DisplaySettings(), DisplaySettings.Load(path, out problem));
                Equal(true, problem is not null);
                File.WriteAllText(path, """{ "mode": "borderless", "windowScale": 40, "view": "10x10" }""");
                var repaired = DisplaySettings.Load(path, out problem);
                Equal((WindowMode.Borderless, 0, "classic", true), (repaired.Mode, repaired.WindowScale, repaired.View, problem is not null));
                Equal(new DisplaySettings(), DisplaySettings.Load(path + ".missing", out problem));
                Equal(true, problem is null);
            }
            finally
            {
                File.Delete(path);
            }
        });

        Check("the sprite atlas packs images inside its pages, apart, each with its transparent gutter", () =>
        {
            var packer = new AtlasPacker(pageSize: 256, largestPacked: 64);
            var random = new Random(7);
            var placed = new List<(int Page, Rectangle Cell)>();
            for (var image = 0; image < 600; image++)
            {
                var (width, height) = (random.Next(1, 65), random.Next(1, 65));
                Equal(true, packer.TryPack(width, height, out var slot));
                // The image and its gutter: inside the page, and clear of every other.
                var cell = new Rectangle(slot.X - AtlasPacker.Gutter, slot.Y - AtlasPacker.Gutter, width + 2 * AtlasPacker.Gutter, height + 2 * AtlasPacker.Gutter);
                Equal(true, new Rectangle(0, 0, 256, 256).Contains(cell));
                if (placed.Any(other => other.Page == slot.Page && other.Cell.IntersectsWith(cell)))
                    throw new InvalidOperationException($"image {image} at {cell} on page {slot.Page} overlaps another");
                placed.Add((slot.Page, cell));
            }
            Equal(true, packer.PageCount > 1);
            // Larger than the largest packed image: a texture of its own.
            Equal((false, false), (packer.TryPack(65, 1, out _), packer.TryPack(1, 65, out _)));
            // Reset refills the first page from its corner.
            packer.Reset();
            Equal(true, packer.TryPack(10, 10, out var first));
            Equal(new AtlasSlot(0, AtlasPacker.Gutter, AtlasPacker.Gutter), first);
        });

        Check("a thick line's row spans cover exactly its Bresenham squares, each pixel once", () =>
        {
            // The rasterizer the canvas used before the spans: a thickness-square
            // at every Bresenham step, as a set of pixels.
            static HashSet<Point> Squares(Point start, Point end, int thickness)
            {
                var pixels = new HashSet<Point>();
                var (minX, minY) = (Math.Min(start.X, end.X), Math.Min(start.Y, end.Y));
                var (dx, dy) = (end.X - start.X, end.Y - start.Y);
                var (width, height) = (Math.Abs(dx) + thickness, Math.Abs(dy) + thickness);
                var x = dx < 0 ? width - thickness : 0;
                var y = dy < 0 ? height - thickness : 0;
                var targetX = dx < 0 ? 0 : width - thickness;
                var targetY = dy < 0 ? 0 : height - thickness;
                var stepX = Math.Abs(targetX - x);
                var stepY = -Math.Abs(targetY - y);
                var directionX = x < targetX ? 1 : -1;
                var directionY = y < targetY ? 1 : -1;
                var error = stepX + stepY;
                while (true)
                {
                    for (var offsetY = 0; offsetY < thickness; offsetY++)
                    for (var offsetX = 0; offsetX < thickness; offsetX++)
                        pixels.Add(new Point(minX + x + offsetX, minY + y + offsetY));
                    if (x == targetX && y == targetY) break;
                    var twiceError = error * 2;
                    if (twiceError >= stepY) { error += stepY; x += directionX; }
                    if (twiceError <= stepX) { error += stepX; y += directionY; }
                }
                return pixels;
            }

            var random = new Random(11);
            for (var line = 0; line < 400; line++)
            {
                var start = new Point(random.Next(-50, 50), random.Next(-50, 50));
                var end = line < 4 ? start : new Point(random.Next(-50, 50), random.Next(-50, 50));
                var thickness = 1 + line % 3;
                var spans = PixelLine.Spans(start, end, thickness);
                var covered = spans.SelectMany(span => Enumerable.Range(span.X, span.Width).Select(x => new Point(x, span.Y))).ToList();
                Equal(true, spans.All(span => span.Height == 1) && spans.Select(span => span.Y).Distinct().Count() == spans.Count);
                Equal(covered.Count, covered.Distinct().Count());
                if (!Squares(start, end, thickness).SetEquals(covered))
                    throw new InvalidOperationException($"line {start}->{end} x{thickness}: spans differ from the squares");
            }
        });
    }
}
