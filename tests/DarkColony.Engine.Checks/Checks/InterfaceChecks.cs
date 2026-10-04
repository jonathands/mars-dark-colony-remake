using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Missions;
using DarkColony.Engine.Movement;
using DarkColony.Engine.World;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Commands;
using DarkColony.Engine.Interface;
using DarkColony.Engine.Audio;
using DarkColony.Engine.Video;
using DarkColony.Engine.Network;
using System.Buffers.Binary;
using static CheckHelpers;

/// <summary>Menus, text widgets, colour remapping, options, CD music, and video.</summary>
internal static class InterfaceChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Interface", name, tags, action);

        Check("maine catalog gadgets draw their own picture and name themselves in the 15-character strip", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var maine = InterfaceDefinition.Load(install.DataFile("intrface", "maine"));
            var buttons = Sprite.Load(install.DataFile("intrface", "mainbut.spr"));
            // The picture is the gadget's frame, not its id: the Exploiter
            // (87) is frame 8, the Barracks (80) frame 20, the Gray Brozaar (46)
            // frame 15 and Human Weapon +1 (110) frame 47.
            Equal((8, 20, 15, 47), (maine.Controls[87].Frame, maine.Controls[80].Frame, maine.Controls[46].Frame, maine.Controls[110].Frame));
            var catalog = maine.Groups[84].Values.Concat(maine.Groups[95].Values).Concat(maine.Groups[53].Values)
                .Concat(maine.Groups[45].Values).Concat(maine.Groups[96].Values).Concat(maine.Groups[61].Values)
                .Where(id => maine.Controls.TryGetValue(id, out var control) && control.Kind == InterfaceControlKind.Count)
                .ToArray();
            Equal(80, catalog.Length);
            foreach (var id in catalog)
            {
                var control = maine.Controls[id];
                var frame = buttons.Frames[control.Frame!.Value];
                if ((frame.Width, frame.Height) != (59, 41)) throw new InvalidOperationException($"gadget {id}: frame {control.Frame} is {frame.Width}x{frame.Height}");
                // The hovered gadget's textmsg (name and price) fits UI 79.
                var text = maine.LabelFor(id) ?? throw new InvalidOperationException($"gadget {id} has no textmsg");
                if (text.Length > maine.Controls[79].Bounds.Width) throw new InvalidOperationException($"gadget {id}: '{text}' exceeds UI 79");
            }
            Equal("Exploiter 1500", maine.LabelFor(87) ?? string.Empty);

            // The tabs have no picture of their own; pictures 3-5 are one
            // 110x12 strip whose frame shows the pressed tab.
            Equal((null as int?, null as int?, null as int?), (maine.Controls[0].Frame, maine.Controls[1].Frame, maine.Controls[2].Frame));
            Equal((77, 78, 79), (maine.Controls[3].Frame!.Value, maine.Controls[4].Frame!.Value, maine.Controls[5].Frame!.Value));
            Equal(new InterfaceRectangle(521, 96, 110, 12), maine.Controls[3].Bounds);
            Equal((110, 12), (buttons.Frames[77].Width, buttons.Frames[77].Height));
        }, CheckTags.Data);

        Check("native viewport hotkeys resolve the installed Human and Gray unit pairs", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var expected = new[]
            {
                "TRSC,TRSC,TRSC,TRSC,GRAY,GRAY,GRAY,GRAY",
                "TRSC,GRAY", "REAP,SCYT", "BEON,ZISP", "SCGM,ORTU",
                "BARR,ATRIL", "ENGI,SLOM", "SARG,PSYC", "EXPL,SLUG", "TURR,XENO",
            };
            for (var functionKey = 1; functionKey <= expected.Length; functionKey++)
                Equal(expected[functionKey - 1], string.Join(',', NativeUnitSelectionHotkeys.EntityIds(functionKey)
                    .Select(entityId => entities[entityId].Code)));
        }, CheckTags.Data);

        Check("menu bitmap font uses shipped metrics", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var font = new BitmapFont(
                Sprite.Load(install.DataFile("intrface", "mfonto5.spr")),
                frameOffset: 31,
                lineHeight: 14);
            Equal(123, font.Sprite.Frames.Count);
            Equal(34, font.FrameIndex('A'));
            // Extent plus the native one-pixel spacing (captured menu and lobby).
            Equal(8, font.Advance('A'));
            Equal(8, font.Advance(' '));
            Equal(6, font.Advance('!'));
        }, CheckTags.Data);

        Check("main menu credits teletype wraps like the native window", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var font = Sprite.Load(install.DataFile("intrface", "mfonto5.spr"));
            // 0x404BAD: x 178, y 200, 280 x 100; frame 0 is the 7 x 14 cell.
            var (columns, lastRow) = TeletypeText.Layout(280, 100, font.Frames[0].Width, font.Frames[0].Height);
            Equal(34, columns);
            Equal(5, lastRow);
            var credits = TeletypeText.Parse(File.ReadAllBytes(install.DataFile("intrface", "credits.txt")), columns, lastRow);
            Equal(72, credits.LineCount);
            Equal(72 * 34, credits.Cells.Count);
            string Line(int line) => new([.. credits.Cells.Skip(line * columns).Take(columns).Select(cell => (char)cell.Character)]);
            // 34-character lines fit exactly once text mode drops the CRs.
            Equal("DARK COLONY ......................", Line(0));
            Equal((byte)2, credits.Cells[0].Colour);
            Equal("PROGRAMMING.......................", Line(5));
            Equal("          Andy Brownbill          ", Line(6));
            Equal((byte)0, credits.Cells[6 * columns + 10].Colour);
            // The native capture (menu-130543.png) shows lines 55-59 under a blank line 60.
            Equal("          Cyrus Harris            ", Line(55));
            Equal("          Dave Wallick            ", Line(59));
            Equal("Visit the Dark Colony web site at ", Line(62));
            Equal((byte)7, credits.Cells[62 * columns].Colour);
            Equal(true, Enumerable.Range(64, 8).All(line => Line(line).Trim().Length == 0));
            Equal(1, new TeletypeCell((byte)'\n', 0).Glyph);
            Equal(34, new TeletypeCell((byte)'A', 0).Glyph);
        }, CheckTags.Data);

        Check("main menu credits teletype types, scrolls and restarts", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var credits = TeletypeText.Parse(File.ReadAllBytes(install.DataFile("intrface", "credits.txt")), 34, 5);
            Equal(0, credits.Visible(0).Count);
            Equal(1L, TeletypeText.StepsAfter(0));
            Equal(2L, TeletypeText.StepsAfter(6));
            // The first step draws cells 4..0 with the trail 1F, 1C, 18, 14, 10.
            var first = credits.Visible(1);
            Equal("D:16 A:20 R:24 K:28", string.Join(' ', first.Select(glyph => $"{(char)glyph.Cell.Character}:{glyph.Brightness}")));
            Equal(true, credits.Typed(1));
            // Cell 203 ends row 5: that step scrolls one line and repaints it at 0x10.
            var scrollStep = 203 - 3;
            Equal(0, credits.TopLine(scrollStep - 1));
            Equal(1, credits.TopLine(scrollStep));
            var scrolled = credits.Visible(scrollStep);
            Equal(true, scrolled.All(glyph => glyph.Row <= 4));
            Equal(true, scrolled.Where(glyph => glyph.Row == 4).All(glyph => glyph.Brightness == TeletypeText.NormalBrightness || glyph.Column >= 30));
            Equal(TeletypeText.NormalBrightness, scrolled.Single(glyph => glyph.Row == 4 && glyph.Column == 33).Brightness);
            Equal(0x1C, scrolled.Single(glyph => glyph.Row == 4 && glyph.Column == 32).Brightness);
            // The last step clears the window; the next one starts over.
            Equal(2449L, credits.StepsPerCycle);
            Equal(0, credits.Visible(credits.StepsPerCycle).Count);
            Equal(string.Join(',', first), string.Join(',', credits.Visible(credits.StepsPerCycle + 1)));
            Equal(false, credits.Typed(credits.StepsPerCycle - 1));
            Equal(67, credits.TopLine(credits.StepsPerCycle - 1));
        }, CheckTags.Data);

        Check("encyclopedia widgets: the preview turns on the native clock and the article types once", () =>
        {
            // 0x428C3C state 8: the first update steps at once, then one step per 0x21 + 1 ms.
            var preview = new PictureAnimation(frameCount: 5);
            preview.Advance(1_000);
            Equal(2, preview.Frame);
            preview.Advance(1_033);
            Equal(2, preview.Frame);
            preview.Advance(1_034);
            Equal(3, preview.Frame);
            preview.Advance(1_102);
            Equal(1, preview.Frame);
            preview.Command = PictureCommand.Backward;
            preview.Step();
            Equal(4, preview.Frame);
            preview.Command = PictureCommand.Hold;
            preview.Step();
            Equal(4, preview.Frame);

            var install = GameInstallation.Open(dataPath);
            var font = Sprite.Load(install.DataFile("intrface", "mfonto5.spr")).Frames[0];
            var (columns, lastRow) = TeletypeText.Layout(238, 356, font.Width, font.Height);
            Equal(29, columns);
            Equal(22, lastRow);
            var article = TeletypeText.Parse(File.ReadAllBytes(Path.Combine(install.RootPath, "encyclo", "troop.txt")), columns, lastRow, repeat: false);
            Equal(false, article.Finished(article.StepsPerCycle - 1));
            Equal(true, article.Finished(article.StepsPerCycle));
            // Mode 1 keeps the picture: after the end the window rests one line past the text.
            var final = article.Visible(article.StepsPerCycle + 100);
            Equal(true, final.Count > 0);
            var top = article.TopLine(article.StepsPerCycle + 100);
            Equal(Math.Max(0, article.LineCount - lastRow), top);
            Equal(1L + 100 / 2, TeletypeText.StepsAfter(100, interval: 1));
            // 0x4281B6 caps the top at lines - rows - 1; 0x42814E stops at 0.
            if (article.LineCount > lastRow)
            {
                Equal(article.LineCount - lastRow - 1, article.ScrollDown(top));
                Equal(top - 1, article.ScrollUp(top));
            }
            Equal(0, article.ScrollUp(1));
            Equal(true, article.VisibleScrolled(0).All(glyph => glyph.Brightness == TeletypeText.NormalBrightness));
            Equal((byte)1, article.Cells[0].Colour);

            var labels = NativeEncyclopediaLabels.Load(install.ExecutablePath);
            Equal("Earth Mars Human Forces Gray Forces Alien Artifacts",
                $"{labels.Earth} {labels.Mars} {labels.HumanForces} {labels.GrayForces} {labels.AlienArtifacts}");
            var troop = Sprite.Load(Path.Combine(install.RootPath, "encyclo", "troop.spr"));
            Equal("320x200", $"{troop.Frames[0].Width}x{troop.Frames[0].Height}");
        }, CheckTags.Data);

        Check("game options step like lopte and set the update interval", () =>
        {
            var install = GameInstallation.Open(dataPath);
            // 0x478CC4: detail 2, sound 5, CD 5, interval 66 ms.
            var options = GameOptions.Load(install.ExecutablePath);
            Equal(new GameOptions(100, 5, 5, 2), options);
            Equal(FixedStepClock.NativeDefaultIntervalMilliseconds, options.UpdateIntervalMilliseconds);
            Equal(55, options.Faster().Faster().UpdateIntervalMilliseconds);
            Equal(200, GameOptions.SpeedFromInterval(33));
            Equal(90, GameOptions.SpeedFromInterval(70));
            var slowest = options;
            for (var step = 0; step < 20; step++) slowest = slowest.Slower();
            Equal(10, slowest.SpeedPercent);
            Equal(660, slowest.UpdateIntervalMilliseconds);
            Equal(2, options.MoreDetail().Detail);
            Equal(0, options.LessDetail().LessDetail().LessDetail().Detail);
            Equal(10, Enumerable.Range(0, 8).Aggregate(options, (current, _) => current.CdLouder()).CdVolume);
            Equal(0, GameOptions.AuxVolume(0));
            Equal(5 * 0x1800, GameOptions.AuxVolume(5));
            Equal(0xF000, GameOptions.AuxVolume(10));
        }, CheckTags.Data);

        Check("CD music plays the image from track 2 on the native poll", () =>
        {
            const string cue = """
                FILE "disc.bin" BINARY
                  TRACK 01 MODE1/2352
                    INDEX 01 00:00:00
                  TRACK 02 AUDIO
                    INDEX 01 00:02:00
                  TRACK 03 AUDIO
                    INDEX 01 00:03:10
                """;
            var sheet = CueSheet.Parse(cue, name => (name, 300L * CueSheet.SectorBytes));
            Equal(3, sheet.Tracks.Count);
            Equal(new CueTrack(1, false, "disc.bin", 0, 150L * CueSheet.SectorBytes), sheet.Tracks[0]);
            Equal(new CueTrack(2, true, "disc.bin", 150L * CueSheet.SectorBytes, 85L * CueSheet.SectorBytes), sheet.Tracks[1]);
            Equal(new CueTrack(3, true, "disc.bin", 235L * CueSheet.SectorBytes, 65L * CueSheet.SectorBytes), sheet.Tracks[2]);
            Equal("2,3", string.Join(',', CdMusic.Pass(sheet).Select(track => track.Number)));
            // 0x431F0F: poll once more than 5000 ms have passed; the first loop polls.
            var poll = new CdMusicPoll();
            Equal(true, poll.Due(10_000));
            Equal(false, poll.Due(15_000));
            Equal(true, poll.Due(15_001));
            Equal(false, poll.Due(20_001));

            var folder = Path.Combine(Path.GetTempPath(), $"dc-cue-{Environment.ProcessId}");
            var install = Path.Combine(folder, "Dark Colony");
            Directory.CreateDirectory(install);
            try
            {
                File.WriteAllText(Path.Combine(folder, "disc.cue"), cue);
                File.WriteAllBytes(Path.Combine(folder, "disc.bin"), new byte[300 * CueSheet.SectorBytes]);
                Equal(Path.Combine(folder, "disc.cue"), CdImageLocator.Locate([], install) ?? "");
                Equal("x.cue", CdImageLocator.Locate(["--cd-image", "x.cue"], install) ?? "");
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }, CheckTags.Data);

        Check("Cinepak decodes V1, V4 and inter-coded blocks", () =>
        {
            static byte[] Frame(params byte[][] strips)
            {
                var body = strips.SelectMany(strip => strip).ToArray();
                var length = 10 + body.Length;
                return [0, (byte)(length >> 16), (byte)(length >> 8), (byte)length, 0, 4, 0, 4, 0, (byte)strips.Length, .. body];
            }
            static byte[] Strip(byte id, params byte[][] chunks)
            {
                var body = chunks.SelectMany(chunk => chunk).ToArray();
                var length = 12 + body.Length;
                // y1 = 0: the strip starts below the previous one, y2 its height.
                return [id, (byte)(length >> 16), (byte)(length >> 8), (byte)length, 0, 0, 0, 0, 0, 4, 0, 4, .. body];
            }
            static byte[] Chunk(byte id, params byte[] data) =>
                [id, (byte)((data.Length + 4) >> 16), (byte)((data.Length + 4) >> 8), (byte)(data.Length + 4), .. data];
            string Pixels(CinepakDecoder decoder) => string.Join(' ', Enumerable.Range(0, 16).Select(pixel =>
                $"{decoder.Frame[pixel * 3]},{decoder.Frame[pixel * 3 + 1]},{decoder.Frame[pixel * 3 + 2]}"));

            var decoder = new CinepakDecoder(4, 4);
            // One colour V1 entry: Y 100..130, U 10, V -20 -> (Y - 40, Y + 15, Y + 20), scaled to 4x4.
            decoder.Decode(Frame(Strip(0x10, Chunk(0x22, 100, 110, 120, 130, 10, unchecked((byte)-20)), Chunk(0x32, 0))));
            var p0 = "60,115,120"; var p1 = "70,125,130"; var p2 = "80,135,140"; var p3 = "90,145,150";
            Equal($"{p0} {p0} {p1} {p1} {p0} {p0} {p1} {p1} {p2} {p2} {p3} {p3} {p2} {p2} {p3} {p3}", Pixels(decoder));
            // An inter chunk whose block bit is clear keeps the picture.
            decoder.Decode(Frame(Strip(0x11, Chunk(0x31, 0, 0, 0, 0))));
            Equal($"{p0} {p0} {p1} {p1} {p0} {p0} {p1} {p1} {p2} {p2} {p3} {p3} {p2} {p2} {p3} {p3}", Pixels(decoder));
            // Grey V4 entries k = (10k .. 10k + 3); flag bit set picks V4, one entry per 2x2 quadrant.
            decoder.Decode(Frame(Strip(0x10,
                Chunk(0x24, 0, 1, 2, 3, 10, 11, 12, 13, 20, 21, 22, 23, 30, 31, 32, 33),
                Chunk(0x30, 0x80, 0, 0, 0, 0, 1, 2, 3))));
            static string Grey(int value) => $"{value},{value},{value}";
            Equal(string.Join(' ', new[] { 0, 1, 10, 11, 2, 3, 12, 13, 20, 21, 30, 31, 22, 23, 32, 33 }.Select(Grey)), Pixels(decoder));
        }, CheckTags.Data);

        Check("scene lists name each campaign mission's victory and defeat videos", () =>
        {
            var install = GameInstallation.Open(dataPath);
            Equal("gtscene.txt", SceneList.FileName(gray: true, training: true));
            var human = SceneList.Load(install.DataFile("gamestat", SceneList.FileName(gray: false, training: false)));
            Equal(8, human.Names.Count);
            Equal(15, human.Missions.Count);
            var first = human.Find("human/human01.scn") ?? throw new InvalidOperationException("human01 record missing.");
            Equal("RED LANDING", first.Title);
            Equal("scenario/human/human01", first.ScenarioPath);
            Equal(true, first.VictoryVideo.StartsWith("avi/", StringComparison.OrdinalIgnoreCase));
            Equal(3, first.Remaining.Count);
            Equal(7, SceneList.Load(install.DataFile("gamestat", SceneList.FileName(gray: false, training: true))).Missions.Count);

            // The disc's own videos, when the user's CD image is beside the installation (ffmpeg hashes).
            var cue = CdImageLocator.Locate([], install.RootPath);
            if (cue is null)
            {
                Console.WriteLine("  CD image: none found; the disc video decode is not compared.");
                return;
            }
            var files = CdImageFiles.Open(CueSheet.Load(cue)) ?? throw new InvalidOperationException("CD image has no ISO 9660 track.");
            using var stream = files.OpenFile("dc/avi/htran3.avi") ?? throw new InvalidOperationException("dc/avi/htran3.avi missing from the CD image.");
            var avi = AviFile.Read(stream);
            Equal("cvid 320x180 89 66667", $"{avi.VideoHandler} {avi.Width}x{avi.Height} {avi.VideoFrames.Count} {avi.MicrosecondsPerFrame}");
            Equal(new AviAudioFormat(1, 11025, 8), avi.AudioFormat ?? throw new InvalidOperationException("No audio."));
            Equal(65411, avi.Audio.Length);
            var video = new CinepakDecoder(avi.Width, avi.Height);
            var hashes = new List<string>();
            for (var frame = 0; frame < avi.VideoFrames.Count; frame++)
            {
                video.Decode(avi.VideoFrames[frame]);
                if (frame is 0 or 44 or 88) hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(video.Frame))[..16].ToLowerInvariant());
            }
            Equal("32725a91154c0fd5 08f238c60e45f950 24593bb7d99275f5", string.Join(' ', hashes));
            Console.WriteLine($"  CD image: {cue}");
        }, CheckTags.Data);

        Check("native colour remap reproduces dc16 interface text", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var remap = NativeColourRemap.Load(install.ExecutablePath);
            Equal("47,61,65,66,67,254", string.Join(',', remap.DimRamp));
            var palette = GifPalette.Load(install.DataFile("intrface", "intro.gif"));
            // Font ramp 138-143: colour c reads index - 6 (7 - c).
            Equal(palette[97], remap.Map(palette, 139, 0, NativeColourRemap.NormalBrightness));
            Equal(palette[109], remap.Map(palette, 139, 2, NativeColourRemap.NormalBrightness));
            Equal(palette[139], remap.Map(palette, 139, 7, NativeColourRemap.NormalBrightness));
            Equal(palette[61], remap.Map(palette, 139, 5, NativeColourRemap.NormalBrightness));
            Equal(new VgaColor(203, 23, 23), palette[97]);
            // Brightness scales and caps: the main-menu labels draw at 11, the teletype lead at 0x1F.
            var label = remap.Map(palette, 139, 0, 11);
            Equal(new VgaColor(139, 15, 15), label);
            Equal(new VgaColor(255, 44, 44), remap.Map(palette, 139, 0, 0x1F));
            // dc16 packs RGB565; the capture's label pixels are (140,12,8).
            static byte Expand(int value, int bits) => (byte)(value << (8 - bits) | value >> (2 * bits - 8));
            Equal(new VgaColor(140, 12, 8), new VgaColor(Expand(label.Red >> 3, 5), Expand(label.Green >> 2, 6), Expand(label.Blue >> 3, 5)));
            // Other indices blend toward their luminance by colour / 11.
            var red = palette[82];
            var luminance = (3 * red.Red + 6 * red.Green + red.Blue) * 7;
            Equal(new VgaColor((byte)((40 * red.Red + luminance) / 110), (byte)((40 * red.Green + luminance) / 110), (byte)((40 * red.Blue + luminance) / 110)),
                remap.Map(palette, 82, 7, NativeColourRemap.NormalBrightness));
            Equal(palette[82], remap.Map(palette, 82, 0, NativeColourRemap.NormalBrightness));
        }, CheckTags.Data);

        Check("encyclopedia catalog preserves shipped identities", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var catalog = EncyclopediaCatalog.Load(install.DataFile("intrface", "encyclo.txt"));
            Equal(3, catalog.Categories.Count);
            Equal(10, catalog.Categories[0].Entries.Count);
            Equal(10, catalog.Categories[1].Entries.Count);
            Equal(5, catalog.Categories[2].Entries.Count);
            Equal("Exploiter", catalog.Categories[1].Entries[5].Name);
            Equal(15, catalog.Categories[1].Entries[5].NativeId);
            var cyborgArticle = EncyclopediaArticle.Load(install.RootPath, "encyclo/cybo");
            Equal("REMOTE ASSASSINATION CYBORG", cyborgArticle.Lines[0].Text);
            Equal(1, cyborgArticle.Lines[0].PaletteIndex);
            Equal(true, cyborgArticle.Lines.Any(line => line.Text.Contains("NAPALM", StringComparison.Ordinal)));
            Equal(true, File.Exists(Path.Combine(install.RootPath, "encyclo", "cybo.wav")));
            var parsed = EncyclopediaArticle.Parse("~1TITLE\n~0NAME: ~4VALUE\n");
            Equal("TITLE", parsed.Lines[0].Text);
            Equal(1, parsed.Lines[0].PaletteIndex);
            Equal("NAME: VALUE", parsed.Lines[1].Text);
            Equal(0, parsed.Lines[1].PaletteIndex);
            Equal(2, parsed.Lines[1].Segments.Count);
            Equal("NAME: ", parsed.Lines[1].Segments[0].Text);
            Equal(0, parsed.Lines[1].Segments[0].PaletteIndex);
            Equal("VALUE", parsed.Lines[1].Segments[1].Text);
            Equal(4, parsed.Lines[1].Segments[1].PaletteIndex);
            var layout = File.ReadAllText(install.DataFile("intrface", "encycloe"));
            Equal(true, layout.Contains("gadget  22      0       343     220", StringComparison.Ordinal));
            Equal(true, layout.Contains("gadget  23      0       445     220", StringComparison.Ordinal));
            Equal(true, layout.Contains("gadget  24      0       511     220", StringComparison.Ordinal));
            Equal(true, layout.Contains("gadget  25      0       601     220", StringComparison.Ordinal));
            Equal(true, EncyclopediaUnitIdentityCatalog.TryGetEntityId(catalog.Categories[1].Entries.Single(entry => entry.ResourceStem.EndsWith("cybo", StringComparison.Ordinal)), out var sargeId));
            Equal(4, sargeId);
            Equal(true, EncyclopediaUnitIdentityCatalog.TryGetEntityId(catalog.Categories[0].Entries.Single(entry => entry.ResourceStem.EndsWith("psych", StringComparison.Ordinal)), out var psyId));
            Equal(12, psyId);
        }, CheckTags.Data);

        Check("menu FIN animations compose", () =>
        {
            var install = GameInstallation.Open(dataPath);
            Sprite LoadSprite(string name)
            {
                foreach (var directory in new[] { "sprites", "intrface" })
                {
                    var path = install.DataFile(directory, $"{name}.spr");
                    if (File.Exists(path)) return Sprite.Load(path);
                }

                throw new FileNotFoundException($"Missing {name}.spr");
            }

            foreach (var (file, animationName) in new[]
            {
                ("dcss.fin", "DCSS"),
                ("knobe.fin", "LARGEBUTTON"),
                ("hcar.fin", "HLOOP"),
                ("acar.fin", "ALOOP"),
                ("acom.fin", "HCOM"),
                ("acom.fin", "ACOM"),
                ("gray.fin", "GRAYSTAND0"),
                ("atril.fin", "ATRILSTAND0"),
                ("scyth.fin", "SCYTHSTAND0"),
                ("ortu.fin", "ORTUMOVE0"),
                ("psyc.fin", "PSYCSTAND0"),
                ("slug.fin", "SLUGSTAND0"),
                ("xeno.fin", "XENOSTAND0"),
                ("slom.fin", "SLOMSTAND0"),
                ("sauc.fin", "EASY2"),
                ("zisp.fin", "ZISPSTAND0"),
                ("trooper1.fin", "TROOPER1STAND6"),
                ("barr.fin", "BARRSTAND0"),
                ("reap.fin", "REAPSTAND0"),
                ("scgm.fin", "SCGMMOVE0"),
                ("cyborg.fin", "CYBORGSTAND0"),
                ("expl.fin", "EXPLSTAND0"),
                ("turr.fin", "TURRSTAND0"),
                ("engi.fin", "ENGISTAND0"),
                ("drop.fin", "DROPSTAND0"),
                ("beon.fin", "BEONMOVE0"),
                ("tektara.fin", "TEKTARA"),
                ("mactor.fin", "MACTORSTAND0"),
                ("lens.fin", "LENSSTAND0"),
                ("luna.fin", "LUNAMOVE0"),
                ("hyyk.fin", "HYYKDEPLOY0"),
            })
            {
                var definition = AnimationDefinition.Load(install.DataFile("animate", file));
                var animation = definition.Animations.Single(item => item.Name == animationName);
                var frame = definition.Compose(animation.FirstFrame, LoadSprite);
                if (frame.Width <= 1 || frame.Height <= 1) throw new InvalidOperationException($"{animationName} composed empty.");
            }
        }, CheckTags.Data);
    }
}
