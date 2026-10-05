using System.Drawing;
using DarkColony.Presentation;
using static CheckHelpers;

/// <summary>Display layout, display settings and the gameplay HUD at other screen sizes.</summary>
internal static class PresentationChecks
{
    public static void Register(CheckSuite suite)
    {
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Presentation", name, tags, action);

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

            var chosen = Step(exclusive, DisplaySettingRow.Size);
            Equal((true, true, false, false),
                (DisplaySettingsEditor.NeedsConfirmation(settings, exclusive), DisplaySettingsEditor.NeedsConfirmation(exclusive, chosen),
                 DisplaySettingsEditor.NeedsConfirmation(chosen, chosen with { VSync = false }), DisplaySettingsEditor.NeedsConfirmation(chosen, borderless)));
        });

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
    }
}
