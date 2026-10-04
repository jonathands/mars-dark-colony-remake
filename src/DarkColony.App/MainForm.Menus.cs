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

/// <summary>Main menu, new game, campaign start, story, and shared menu drawing.</summary>
public sealed partial class MainForm
{
    private bool HandleNewGameKey(Keys key)
    {
        if (_screen != MenuScreenId.NewGame || key != Keys.Back || _leaderName.Length == 0) return false;
        _leaderName = _leaderName[..^1];
        _surface.Invalidate();
        return true;
    }

    private IReadOnlyList<MenuButton> MainButtons() =>
    [
        Button(0, 138, 314, 179, 25, "NEW CAMPAIGN", () => OpenNewGame(false)),
        Button(1, 138, 340, 179, 25, "TRAINING", () => OpenNewGame(true)),
        Button(2, 138, 366, 179, 25, "LOAD GAME", () =>
        {
            RefreshSaveFiles();
            ShowScreen(MenuScreenId.LoadGame);
        }),
        Button(3, 318, 314, 179, 25, "MULTI PLAYER WAR", () => ShowScreen(MenuScreenId.NetworkOptions)),
        Button(4, 318, 340, 179, 25, "SINGLE PLAYER WAR", () => ShowScreen(MenuScreenId.SinglePlayer)),
        Button(5, 318, 366, 179, 25, "ENCYCLOPEDIA", () => ShowScreen(MenuScreenId.Encyclopedia)),
        Button(16, 138, 392, 179, 25, "PLAY INTRO", () => _status = "Intro playback will be connected after media-source detection."),
        Button(12, 318, 392, 179, 25, "QUIT", Close),
    ];

    private IReadOnlyList<MenuButton> NewGameButtons() =>
    [
        Button(0, 193, 23, 90, 26, "HUMAN", () => SelectRace(false), !_grayRace),
        Button(1, 357, 23, 90, 26, "GRAY", () => SelectRace(true), _grayRace),
        Button(2, 399, 349, 179, 26, _training ? "START TRAINING" : "START CAMPAIGN", StartCampaign),
        Button(4, 521, 431, 90, 26, "BACK", () => ShowScreen(MenuScreenId.Main)),
    ];

    private IReadOnlyList<MenuButton> LoadButtons() =>
    [
        Button(4, 313, 447, 90, 26, "BACK", () => ShowScreen(MenuScreenId.Main)),
        Button(5, 403, 447, 90, 26, "LOAD", LoadSelectedSave),
    ];

    private IReadOnlyList<MenuButton> SinglePlayerButtons() =>
    [
        Button(0, 430, 452, 90, 26, "MENU", () => ShowScreen(MenuScreenId.Main)),
        Button(1, 530, 452, 90, 26, "READY", StartSinglePlayerWar),
        Button(2, 588, 194, 26, 26, "", () => SelectSinglePlayerMap(-1), artName: "UP"),
        Button(3, 588, 294, 26, 26, "", () => SelectSinglePlayerMap(1), artName: "DOWN"),
    ];

    private IReadOnlyList<MenuButton> NetworkButtons() =>
    [
        Button(0, 38, 41, 178, 24, "TCP/IP", () => _status = "TCP/IP selected."),
        Button(1, 38, 89, 178, 24, "IPX NETWORK", () => _status = "Legacy IPX is not supported."),
        Button(2, 38, 137, 178, 24, "MODEM", () => _status = "Legacy modem play is not supported."),
        Button(3, 38, 185, 178, 24, "SERIAL CABLE", () => _status = "Legacy serial play is not supported."),
        Button(4, 454, 377, 178, 24, "ACT AS SERVER", () => _status = "Modern networking is a later milestone."),
        Button(5, 454, 409, 178, 24, "CONNECT TO SERVER", () => _status = "Modern networking is a later milestone."),
        Button(6, 454, 441, 178, 24, "MAIN MENU", () => ShowScreen(MenuScreenId.Main)),
    ];

    // `intrface/storye` supplies the native scroll geometry and Next/Back
    // controls. Mission text comes from the scenario companion files rather
    // than being copied into UI code.
    private IReadOnlyList<MenuButton> StoryButtons() =>
    [
        Button(2, 602, 15, 26, 26, "", () => ScrollBriefing(-1), artName: "UP"),
        Button(3, 602, 408, 26, 26, "", () => ScrollBriefing(1), artName: "DOWN"),
        Button(4, 453, 449, 90, 26, "BACK", LeaveStoryBack),
        Button(5, 543, 449, 90, 26, "NEXT", LeaveStoryNext),
    ];

    private void LeaveStoryBack()
    {
        if (_debriefOutcome is not null)
        {
            _debriefOutcome = null;
            ShowScreen(MenuScreenId.Main);
            return;
        }
        if (!_storyReturnsToGameplay)
        {
            ShowScreen(MenuScreenId.NewGame);
            return;
        }
        ResumeGameplayFromStory();
    }

    private void LeaveStoryNext()
    {
        if (_debriefOutcome is { } outcome)
        {
            _debriefOutcome = null;
            ContinueAfterMission(outcome.Victory);
            return;
        }
        if (!_storyReturnsToGameplay)
        {
            ShowScreen(MenuScreenId.Gameplay);
            return;
        }
        ResumeGameplayFromStory();
    }

    private void ResumeGameplayFromStory()
    {
        _storyReturnsToGameplay = false;
        _resumeGameplayFromStory = true;
        _gameplayPaused = _gameplayPausedBeforeStory;
        ShowScreen(MenuScreenId.Gameplay);
    }

    private MenuButton Button(
        int id,
        int x,
        int y,
        int width,
        int height,
        string label,
        Action action,
        bool selected = false,
        string? artName = null) =>
        new(id, new Rectangle(x, y, width, height), label, action, selected, artName);

    private void OpenNewGame(bool training)
    {
        _training = training;
        ShowScreen(MenuScreenId.NewGame);
    }

    private void SelectRace(bool gray)
    {
        _grayRace = gray;
        _singlePlayerMapIndex = 0;
        ShowScreen(MenuScreenId.NewGame);
    }

    private void StartCampaign()
    {
        _campaignMission = 1;
        BeginCampaignMission();
    }

    /// <summary>
    /// After a mission's outcome: a victory moves to the next campaign
    /// mission (or back to the main menu after the last), a defeat replays
    /// the same mission. Single-player War maps return to the main menu.
    /// </summary>
    private void ContinueAfterMission(bool victory)
    {
        if (_selectedScenario is not null)
        {
            ShowScreen(MenuScreenId.Main);
            return;
        }
        if (victory)
        {
            var next = CampaignScenario(_campaignMission + 1);
            if (_installation is null || !File.Exists(_installation.DataFile("scenario", next.Directory, $"{next.Name}.scn")))
            {
                _status = "Campaign complete.";
                ShowScreen(MenuScreenId.Main);
                return;
            }
            _campaignMission++;
        }
        BeginCampaignMission();
    }

    /// <summary>Shows a finished mission's outcome text (its .00N file) on the story screen.</summary>
    private void ShowMissionDebrief(MissionOutcome outcome)
    {
        _debriefOutcome = outcome;
        var key = outcome.OutcomeText.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
        _debriefText = _missionText?.Outcomes.TryGetValue(key, out var text) == true
            ? text
            : outcome.Victory ? "Mission complete." : "Mission failed.";
        _briefingScrollLine = 0;
        _storyReturnsToGameplay = false;
        ShowScreen(MenuScreenId.Story);
    }

    private void BeginCampaignMission()
    {
        _storyReturnsToGameplay = false;
        _resumeGameplayFromStory = false;
        _selectedScenario = null;
        _localPlayerTeam = 0;
        _status = $"{(_grayRace ? "Gray" : "Human")} {(_training ? "training" : "campaign")} terrain harness.";
        var scenario = GameplayScenario();
        var scenarioFile = _installation?.DataFile("scenario", scenario.Directory, $"{scenario.Name}.scn");
        _missionText = scenarioFile is not null && File.Exists(scenarioFile)
            ? ScenarioMissionText.LoadForScenario(scenarioFile)
            : null;
        _briefingScrollLine = 0;
        ShowScreen(_missionText is { Briefing.Length: > 0 } ? MenuScreenId.Story : MenuScreenId.Gameplay);
    }

    private void ScrollBriefing(int delta)
    {
        var lineCount = MissionBriefingLines().Count;
        _briefingScrollLine = Math.Clamp(_briefingScrollLine + delta * 3, 0, Math.Max(0, lineCount - 24));
        _surface.Invalidate();
    }

    private void DrawOpeningLogo(Graphics graphics)
    {
        var animation = Animation("dcss.fin", "DCSS");
        if (animation is null) return;
        var age = _world.TickCount - _screenStartedAtTick;
        var frame = animation.FirstFrame + (ushort)Math.Min((ulong)(animation.LastFrame - animation.FirstFrame), age / 2);
        DrawAnimationFrame(graphics, "dcss.fin", frame, 130, 0);
    }

    private void DrawInnerMenuAssets(Graphics graphics)
    {
        switch (_screen)
        {
            case MenuScreenId.NewGame:
                // newgamee gadgets 21-26: both race portraits occupy their own
                // 112x232 viewport. The source declares every listed portrait
                // gadget anim_stopped; REZ ranges remain transition evidence.
                DrawStoppedAnimation(graphics, "hcar.fin", "HLOOP", 27, 23);
                DrawStoppedAnimation(graphics, "acar.fin", "ALOOP", 500, 23);
                break;

            case MenuScreenId.SinglePlayer:
                // The native free-war lobby is intrface/multie, over tcpwait,
                // not shumane. CHAA supplies its structural foreground layer;
                // the player, map, and option values below are data-driven.
                DrawStoppedAnimation(graphics, "chaa.fin", "CHAA", 0, 0);
                break;
        }
    }

    private int MenuTextWidth(string text) => _menuFont?.Measure(text) ?? text.Length * 7;

    private void DrawButton(Graphics graphics, MenuButton button, int sequenceIndex)
    {
        var hovered = _hoveredButton == button.Id;
        var pressed = _pressedButton == button.Id;
        var bright = button.Selected || hovered;
        var artName = button.ArtName ?? (button.Bounds.Width >= 170 ? "LARGEBUTTON" : "MEDBUTTON");
        var art = Animation("knobe.fin", artName);
        var drewOriginal = false;
        if (art is not null)
        {
            ushort frame;
            var dimmed = false;
            if (_screen == MenuScreenId.Main)
            {
                var age = (long)(_world.TickCount - _screenStartedAtTick) - sequenceIndex * 2L;
                // Several one-off ranges end with an empty FIN sentinel.
                // LARGE/MED/SMALL BUTTON build up to the frame before the last
                // visible one (black with a double red outline, held by its
                // long delay); the last visible frame is the highlight. The
                // native capture shows the resting button there.
                var lastVisible = LastVisibleFrame(art);
                var rest = Math.Max(art.FirstFrame, lastVisible - 1);
                var buildUp = Math.Clamp(age, 0L, (long)(rest - art.FirstFrame));
                frame = (pressed || bright) && buildUp == rest - art.FirstFrame
                    ? (ushort)lastVisible
                    : (ushort)(art.FirstFrame + buildUp);
                dimmed = frame != lastVisible;
            }
            else if (pressed || bright)
            {
                frame = (ushort)Math.Max(art.FirstFrame, LastVisibleFrame(art) - 1);
            }
            else frame = art.FirstFrame;
            drewOriginal = DrawAnimationFrame(
                graphics,
                "knobe.fin",
                frame,
                button.Bounds.X,
                button.Bounds.Y,
                remapWarControlPalette: _screen == MenuScreenId.SinglePlayer,
                dimmed: dimmed);
        }

        if (!drewOriginal)
        {
            using var fill = new SolidBrush(Color.FromArgb(pressed ? 210 : bright ? 175 : 125, 8, 17, 12));
            using var border = new Pen(pressed ? Color.FromArgb(110, 160, 90) : bright ? Color.FromArgb(155, 230, 115) : Color.FromArgb(76, 118, 72));
            graphics.FillRectangle(fill, button.Bounds);
            graphics.DrawRectangle(border, button.Bounds.X, button.Bounds.Y, button.Bounds.Width - 1, button.Bounds.Height - 1);
        }

        // Captured native text: War (159,19,19), main menu (140,12,8).
        var warButtonText = _screen switch
        {
            MenuScreenId.SinglePlayer => Color.FromArgb(159, 19, 19),
            MenuScreenId.Main => Color.FromArgb(140, 12, 8),
            _ => (Color?)null,
        };
        if (!DrawMenuText(graphics, button.Label, button.Bounds, remap: warButtonText, shaded: _screen == MenuScreenId.Main))
        {
            using var font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(pressed ? Color.FromArgb(145, 170, 135) : Color.FromArgb(205, 226, 195));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(button.Label, font, brush, button.Bounds, format);
        }
    }

    /// <param name="shaded">
    /// Keep the glyphs' shading: each cyan tone (0, v, v) becomes the tint
    /// times v / 203, as the native main-menu capture shows; otherwise every
    /// glyph pixel takes the tint.
    /// </param>
    private bool DrawMenuText(Graphics graphics, string text, Rectangle bounds, bool center = true, Color? remap = null, bool shaded = false)
    {
        if (_installation is null) return false;
        try
        {
            _menuFont ??= new BitmapFont(
                Sprite.Load(_installation.DataFile("intrface", "mfonto5.spr")),
                frameOffset: 31,
                lineHeight: 14);

            var cursor = center ? bounds.X + (bounds.Width - _menuFont.Measure(text)) / 2 : bounds.X;
            var lineTop = bounds.Y + (bounds.Height - _menuFont.LineHeight) / 2;
            foreach (var character in text)
            {
                var frameIndex = _menuFont.FrameIndex(character);
                var frame = _menuFont.Glyph(character);
                if (frame is null) continue;
                if (frame.Width != 0 && frame.Height != 0)
                {
                    Bitmap? bitmap;
                    var cacheKey = remap is { } color ? $"{frameIndex}:{color.ToArgb()}:{shaded}" : string.Empty;
                    var found = remap is null
                        ? _fontGlyphs.TryGetValue(frameIndex, out bitmap)
                        : _remappedFontGlyphs.TryGetValue(cacheKey, out bitmap);
                    if (!found)
                    {
                        var rgba = _menuFont.Sprite.FrameRgba(frameIndex);
                        if (remap is { } tint)
                        {
                            rgba = (byte[])rgba.Clone();
                            for (var pixel = 0; pixel < rgba.Length; pixel += 4)
                            {
                                if (rgba[pixel + 3] == 0) continue;
                                var level = shaded ? Math.Min(255, (int)rgba[pixel + 1]) : 203;
                                rgba[pixel] = (byte)(tint.R * level / 203);
                                rgba[pixel + 1] = (byte)(tint.G * level / 203);
                                rgba[pixel + 2] = (byte)(tint.B * level / 203);
                            }
                        }
                        bitmap = BitmapFromRgba(frame.Width, frame.Height, rgba);
                        if (remap is null) _fontGlyphs[frameIndex] = bitmap;
                        else _remappedFontGlyphs[cacheKey] = bitmap;
                    }

                    if (_activeCanvas is { } canvas)
                        canvas.DrawForeground(GpuBitmap(bitmap!), cursor + frame.AnchorX, lineTop + frame.AnchorY);
                    else
                        graphics.DrawImageUnscaled(bitmap!, cursor + frame.AnchorX, lineTop + frame.AnchorY);
                }

                cursor += _menuFont.Advance(character);
            }

            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException)
        {
            _status = $"Font error: {error.Message}";
            return false;
        }
    }

    private void DrawPanelText(Graphics graphics, string text, Rectangle bounds)
    {
        if (_activeCanvas is { } canvas)
        {
            var panelFill = Color.FromArgb(140, 0, 0, 0);
            var panelBorder = Color.FromArgb(85, 125, 80);
            canvas.FillForeground(bounds, panelFill);
            canvas.FillForeground(new Rectangle(bounds.X, bounds.Y, bounds.Width, 1), panelBorder);
            canvas.FillForeground(new Rectangle(bounds.X, bounds.Bottom - 1, bounds.Width, 1), panelBorder);
            canvas.FillForeground(new Rectangle(bounds.X, bounds.Y, 1, bounds.Height), panelBorder);
            canvas.FillForeground(new Rectangle(bounds.Right - 1, bounds.Y, 1, bounds.Height), panelBorder);
            DrawMenuText(graphics, text, bounds, remap: Color.FromArgb(175, 200, 170));
            return;
        }
        using var fill = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
        using var border = new Pen(Color.FromArgb(85, 125, 80));
        using var font = new Font(FontFamily.GenericMonospace, 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(175, 200, 170));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(border, bounds);
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private void DrawMissionBriefing(Graphics graphics)
    {
        // storye's scroll control is x=610/y=48/h=353. Its content area is
        // therefore deliberately kept clear of the native arrows at right.
        var lines = MissionBriefingLines();
        const int firstY = 48;
        const int lineHeight = 14;
        const int visibleLines = 25;
        for (var row = 0; row < visibleLines; row++)
        {
            var index = _briefingScrollLine + row;
            if ((uint)index >= (uint)lines.Count) break;
            DrawMenuText(graphics, lines[index], new Rectangle(60, firstY + row * lineHeight, 530, lineHeight),
                center: false, remap: Color.FromArgb(175, 220, 155));
        }
    }

    private IReadOnlyList<string> MissionBriefingLines()
    {
        var source = _debriefOutcome is not null ? _debriefText : _missionText?.Briefing ?? string.Empty;
        var lines = new List<string>();
        foreach (var paragraph in source.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var words = paragraph.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var line = string.Empty;
            foreach (var word in words)
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                // The source story panel is 530 logical pixels wide. The
                // shipped bitmap font averages below eight pixels here; a
                // conservative 66-character wrap prevents clipping while
                // preserving the original authored newlines.
                if (candidate.Length > 66 && line.Length != 0)
                {
                    lines.Add(line);
                    line = word;
                }
                else line = candidate;
            }
            if (line.Length != 0) lines.Add(line);
        }

        return lines;
    }

    private void DrawNewGameLeaderName(Graphics graphics)
    {
        // newgamee in_text 5: 205,308, width 17. The source uses this same
        // persistent leader value that shumane later shows in in_text 5.
        DrawMenuText(graphics, _leaderName, new Rectangle(205, 308, 170, 18), center: false);
    }
}
