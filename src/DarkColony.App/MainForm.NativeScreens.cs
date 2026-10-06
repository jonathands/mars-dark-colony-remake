using DarkColony.App.Audio;
using DarkColony.App.Ui;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Interface;

namespace DarkColony.App;

/// <summary>
/// The live state of a menu with an intrface definition: its gadgets, the
/// <c>banim</c> build-up of its buttons, and the new campaign screen's race
/// portraits (<c>0x401E50</c>, see <c>docs/reverse-engineering/interface-widgets.md</c>).
/// </summary>
public sealed partial class MainForm
{
    // newgamee: the race check buttons, the start buttons, the portraits and
    // the decorations that loop once the buttons are built.
    private const int HumanRaceButton = 0;
    private const int GrayRaceButton = 1;
    private const int StartTrainingButton = 2;
    private const int StartCampaignButton = 3;
    private const int HumanRezIn = 21;
    private const int HumanRezOut = 22;
    private const int HumanLoop = 23;
    private const int GrayRezIn = 24;
    private const int GrayRezOut = 25;
    private const int GrayLoop = 26;
    private const int LeaderNamePrompt = 18;
    private static readonly int[] NewGameDecorations = [13, 14, 15, 16, 29, 30, 31, 32, 33, 34, 35];

    private NativeScreenState? _nativeScreenState;
    private RacePortraits _racePortraits;
    private WaveOutStream? _rezSound;
    private readonly Dictionary<(string List, string Name), (string File, AnimationRange Range)?> _gadgetSources = [];

    /// <summary>The new campaign screen's portrait state (<c>0x401E50</c>'s ecx: 0, 1, 2, 3).</summary>
    private enum RacePortraits { HumanRezIn, GrayRezIn, HumanLoop, GrayLoop }

    /// <summary>Starts a menu's widget state when the screen opens.</summary>
    private void OpenNativeScreen(MenuScreenId screen)
    {
        _nativeScreenState = null;
        _rezSound?.Dispose();
        _rezSound = null;
        if (NativeMenuScreen(screen) is not { } native || NativeScreen(native.Name) is not { } definition) return;
        _nativeScreenState = new NativeScreenState(definition, name => GadgetSource(definition, name) is { } source
            ? NativeFrameTicks(source.File, source.Range)
            : null);
        switch (screen)
        {
            case MenuScreenId.NewGame:
                // 0x401EBC: a campaign shows START CAMPAIGN, training START TRAINING.
                _nativeScreenState.Show(_training ? StartCampaignButton : StartTrainingButton, false);
                break;
            case MenuScreenId.LoadGame:
                // loadge group 20: the Load Game title and LOAD.
                _nativeScreenState.Show(5, true);
                _nativeScreenState.Show(6, true);
                break;
        }
    }

    /// <summary>Steps the open menu's widgets; plays the build-up's sounds.</summary>
    private void UpdateNativeScreen()
    {
        if (_nativeScreenState is not { } state || _screen == MenuScreenId.Gameplay) return;
        var building = state.InButtonAnimation;
        for (var sounds = state.Update(Environment.TickCount64); sounds > 0; sounds--) PlayInterfaceSound(NativeWidgetRules.ButtonAnimationSound);
        if (building && !state.InButtonAnimation) NativeScreenBuilt(state);
        if (_screen == MenuScreenId.NewGame) UpdateRacePortraits(state);
    }

    /// <summary>What a screen's code does once its buttons are built.</summary>
    private void NativeScreenBuilt(NativeScreenState state)
    {
        if (_screen != MenuScreenId.NewGame) return;
        // 0x401F62-0x402010: the Human portrait rezzes in with REZIN.WAV, the
        // decorations loop, and the race buttons wait for the portrait.
        state.Show(HumanRezIn, true);
        state.Gadget(HumanRezIn)?.Start(GadgetAnimationMode.OneOff);
        PlayRezSound();
        foreach (var id in NewGameDecorations) state.Gadget(id)?.Start(GadgetAnimationMode.Loop);
        state.Enable(HumanRaceButton, false);
        state.Enable(GrayRaceButton, false);
        _racePortraits = RacePortraits.HumanRezIn;
    }

    /// <summary>
    /// HUMAN or GRAY (<c>0x4020A8</c>-<c>0x402223</c>): unless that race's
    /// portrait already loops, REZIN.WAV restarts, the portraits are hidden
    /// and rewound, and the chosen race rezzes in while the other rezzes out.
    /// </summary>
    private void SelectRace(bool gray)
    {
        if (_nativeScreenState is { } state && (state.InButtonAnimation || !state.Enabled(HumanRaceButton))) return;
        _grayRace = gray;
        RefreshButtons();
        if (_nativeScreenState is not { } screen || _racePortraits == (gray ? RacePortraits.GrayLoop : RacePortraits.HumanLoop)) return;
        PlayRezSound();
        ResetPortraits(screen);
        foreach (var id in gray ? (int[])[HumanRezOut, GrayRezIn] : [HumanRezIn, GrayRezOut])
        {
            screen.Show(id, true);
            screen.Gadget(id)?.Start(GadgetAnimationMode.OneOff);
        }
        screen.Enable(HumanRaceButton, false);
        screen.Enable(GrayRaceButton, false);
        _racePortraits = gray ? RacePortraits.GrayRezIn : RacePortraits.HumanRezIn;
    }

    /// <summary>0x402228-0x402324: once both portraits finish, the chosen race loops and the buttons work again.</summary>
    private void UpdateRacePortraits(NativeScreenState state)
    {
        if (state.InButtonAnimation || _racePortraits is RacePortraits.HumanLoop or RacePortraits.GrayLoop) return;
        var gray = _racePortraits == RacePortraits.GrayRezIn;
        int[] playing = gray ? [HumanRezOut, GrayRezIn] : [HumanRezIn, GrayRezOut];
        if (!playing.All(id => state.Gadget(id) is not { Finished: false })) return;
        ResetPortraits(state);
        var loop = gray ? GrayLoop : HumanLoop;
        state.Show(loop, true);
        state.Gadget(loop)?.Start(GadgetAnimationMode.Loop);
        state.Enable(HumanRaceButton, true);
        state.Enable(GrayRaceButton, true);
        _racePortraits = gray ? RacePortraits.GrayLoop : RacePortraits.HumanLoop;
    }

    /// <summary>0x401C48: hide and rewind portraits 21-26.</summary>
    private static void ResetPortraits(NativeScreenState state)
    {
        for (var id = HumanRezIn; id <= GrayLoop; id++)
        {
            state.Show(id, false);
            state.Gadget(id)?.Rewind();
        }
    }

    /// <summary>sound/newsnd.dat sound 1, REZIN.WAV at -1000; a new one stops the last (0x4020EB).</summary>
    private void PlayRezSound()
    {
        if (_installation is null) return;
        _rezSound?.Dispose();
        _rezSound = null;
        try
        {
            foreach (var line in File.ReadLines(_installation.DataFile("sound", "newsnd.dat")))
            {
                var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 3 || words[0] != "1") continue;
                var path = Path.Combine(_installation.RootPath, words[1].Replace(".\\", string.Empty, StringComparison.Ordinal).Replace('\\', Path.DirectorySeparatorChar));
                _rezSound = PlayInterfaceWave(path, int.Parse(words[2], System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException)
        {
            _status = $"Sound error: {error.Message}";
        }
    }

    /// <summary>
    /// The open menu's gadgets and labels in slot order. The War lobby draws
    /// its player rows and labels itself, so only its button build-up draws here.
    /// </summary>
    private void DrawNativeScreenWidgets(Graphics graphics)
    {
        if (_nativeScreenState is not { } state || NativeMenuScreen(_screen) is not { } native) return;
        var definition = state.Definition;
        foreach (var widget in definition.Widgets)
        {
            if (!state.Shows(widget.Id)) continue;
            if (widget.Kind == NativeWidgetKind.Gadget && (_screen != MenuScreenId.SinglePlayer || widget.Animation is "MEDBUTTON") &&
                state.Gadget(widget.Id) is { } gadget && widget.Animation is { } name && GadgetSource(definition, name) is { } source)
            {
                if (!widget.Masked) FillNative(graphics, ToRectangle(widget.Bounds), NativeColour(definition, native.Palette, widget.Background, 0));
                // The captured War lobby shows knobe's green build-up shades as red and grey.
                DrawAnimationFrame(graphics, source.File, source.Range.FirstFrame + gadget.Frame, widget.Bounds.X, widget.Bounds.Y,
                    remapWarControlPalette: _screen == MenuScreenId.SinglePlayer);
            }
            else if (widget.Kind == NativeWidgetKind.Scroll && NativeScrollSpan(widget.Id) is { } span)
            {
                DrawNativeScroll(graphics, definition, native.Palette, widget, span);
            }
            else if (widget.Kind == NativeWidgetKind.Label && _screen != MenuScreenId.SinglePlayer && definition.Message(widget.Message) is { } text)
            {
                var intensity = _screen == MenuScreenId.NewGame && widget.Id == LeaderNamePrompt ? LeaderPromptIntensity() : widget.Intensity;
                DrawNativeText(graphics, definition, native.Palette, widget.Bounds, widget.Align, text, widget.Font, widget.Remap, intensity);
            }
        }
    }

    /// <summary>
    /// The span a screen's own scroll bar shows. The encyclopedia sets
    /// scroll 37 to its article (<c>0x402783</c>, <c>0x402876</c>): the line
    /// count, from the first line shown to that plus the rows, and from line 0
    /// until the article is scrolled. The story screen's bar 1 follows the
    /// briefing the same way (an approximation: its code is not traced).
    /// </summary>
    private (int Range, int Start, int End)? NativeScrollSpan(int id)
    {
        switch (_screen)
        {
            case MenuScreenId.Encyclopedia when id == 37 && _encyclopediaArticleText is { } article:
            {
                var top = _encyclopediaArticleTop ?? 0;
                return (article.LineCount, top, top + article.LastRow + 1);
            }
            case MenuScreenId.Story when id == 1:
                return (MissionBriefingLines().Count, _briefingScrollLine, _briefingScrollLine + BriefingVisibleLines);
            default:
                return null;
        }
    }

    /// <summary>
    /// newgamee label 18 pulses (0x402329): its intensity climbs 0 to 31 and
    /// back, one step per pass of the screen loop, which waits for the display
    /// flip. The port takes 60 passes a second.
    /// </summary>
    private static int LeaderPromptIntensity()
    {
        var step = (int)(Environment.TickCount64 * 60 / 1000 % 62);
        return step <= 31 ? step : 62 - step;
    }

    /// <summary>
    /// The FIN range a gadget plays: the first file of the screen's list
    /// (its <c>animation</c> line, else the list named after its background,
    /// such as <c>choo.dat</c>) that has the animation.
    /// </summary>
    private (string File, AnimationRange Range)? GadgetSource(NativeScreenDefinition definition, string name)
    {
        if (_installation is null) return null;
        var list = definition.Animations is { } named ? Path.GetFileName(named) : $"{Path.GetFileName(definition.Background ?? "intro")}.dat";
        if (_gadgetSources.TryGetValue((list, name), out var cached)) return cached;
        (string File, AnimationRange Range)? source = null;
        try
        {
            var path = _installation.DataFile("intrface", list);
            IEnumerable<string> files = File.Exists(path) ? File.ReadLines(path) : [];
            foreach (var file in files.Select(line => line.Trim()).Where(line => line.EndsWith(".fin", StringComparison.OrdinalIgnoreCase)).Append("knobe.fin"))
            {
                if (Animation(file, name) is not { } range) continue;
                source = (file, range);
                break;
            }
        }
        catch (IOException error)
        {
            _status = $"Interface animation error: {error.Message}";
        }
        _gadgetSources[(list, name)] = source;
        return source;
    }

    private int[] NativeFrameTicks(string file, AnimationRange range)
    {
        if (_nativeFrameTicks.TryGetValue((file, range.FirstFrame, range.LastFrame), out var ticks)) return ticks;
        ticks = _animationDefinitions.TryGetValue(file, out var definition)
            ? NativeAnimationTiming.FrameTicks(definition, range.FirstFrame, range.LastFrame)
            : [.. Enumerable.Repeat(3, Math.Max(1, range.LastFrame - range.FirstFrame + 1))];
        _nativeFrameTicks[(file, range.FirstFrame, range.LastFrame)] = ticks;
        return ticks;
    }
}
