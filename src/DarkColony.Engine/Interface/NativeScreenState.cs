using DarkColony.Engine.Assets;

namespace DarkColony.Engine.Interface;

/// <summary>
/// One gadget's animation (<c>gadget.c</c>): a FIN range on the stepper of
/// <see cref="NativeAnimationTiming"/>. Each draw of the gadget is one step
/// (<c>0x424850</c>).
/// </summary>
public sealed class GadgetAnimation
{
    private readonly int[] _ticks;
    private int _countdown;

    public GadgetAnimation(IReadOnlyList<int> frameTicks, GadgetAnimationMode mode)
    {
        ArgumentNullException.ThrowIfNull(frameTicks);
        if (frameTicks.Count == 0) throw new ArgumentException("A gadget needs a frame.", nameof(frameTicks));
        _ticks = [.. frameTicks];
        Start(mode);
    }

    public int FrameCount => _ticks.Length;

    /// <summary>The frame within the range, 0-based.</summary>
    public int Frame { get; private set; }

    public GadgetAnimationMode Mode { get; private set; }

    /// <summary>
    /// State 2 (<c>0x4267B4</c>): a one-off that ran past its last frame,
    /// which it keeps showing, or a stopped gadget.
    /// </summary>
    public bool Finished { get; private set; }

    /// <summary><c>0x4251CC</c>: play on from the current frame in <paramref name="mode"/>.</summary>
    public void Start(GadgetAnimationMode mode)
    {
        Mode = mode;
        Finished = mode == GadgetAnimationMode.Stopped;
    }

    /// <summary><c>0x424FDC</c>: back to the first frame.</summary>
    public void Rewind()
    {
        Frame = 0;
        _countdown = 0;
    }

    /// <summary>
    /// One stepper run: when the countdown is zero, the next frame and its
    /// ticks (a loop wraps to frame 0; a one-off past its last frame keeps
    /// it and finishes); then the countdown drops by one.
    /// </summary>
    public void Step()
    {
        if (Finished) return;
        if (_countdown == 0)
        {
            if (Frame + 1 < _ticks.Length) Frame++;
            else if (Mode == GadgetAnimationMode.OneOff)
            {
                Finished = true;
                return;
            }
            else Frame = 0;
            _countdown = _ticks[Frame];
        }
        _countdown--;
    }
}

/// <summary>
/// A menu screen's live widget state: which widgets show, the gadgets'
/// animations, and the <c>banim</c> build-up that runs before the screen
/// takes input.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Every slot starts visible and enabled (<c>0x4224E2</c>);
/// a <c>group</c> hides its members (<c>0x422C26</c>).</description></item>
/// <item><description>The event poll (<c>0x424144</c>) steps every visible,
/// enabled gadget once per 0x21 ms that passed, when more than 0x21 ms have
/// passed since its last pass.</description></item>
/// <item><description>The screen first runs its <c>banim</c> widgets in slot
/// order (<c>0x427BD4</c>, <c>0x427ADC</c>), redrawing the gadgets when more
/// than 16 ms have passed (<c>0x4242BC</c>). Each plays sound 0xBA as it
/// begins and as each next gadget starts, which happens when the current
/// one reaches frame 2. A finished gadget is hidden and the next of the
/// banim's buttons that is visible appears.</description></item>
/// </list>
/// </remarks>
public sealed class NativeScreenState
{
    public const int PollMilliseconds = 0x21;
    public const int ButtonAnimationRedrawMilliseconds = 16;

    private readonly NativeScreenDefinition _definition;
    private readonly Dictionary<int, GadgetAnimation> _gadgets = [];
    private readonly HashSet<int> _hidden = [];
    private readonly HashSet<int> _disabled = [];
    private readonly HashSet<int> _unrevealed = [];
    private readonly Queue<NativeWidget> _buttonAnimations = new();
    private NativeWidget? _buttonAnimation;
    private int _finished;
    private int _started;
    private int _nextButton;
    private long _lastPoll = -1;
    private long _lastRedraw = -1;

    /// <param name="frameTicks">The ticks of each frame of a gadget's animation, or null when the screen's FIN files lack it.</param>
    public NativeScreenState(NativeScreenDefinition definition, Func<string, IReadOnlyList<int>?> frameTicks)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(frameTicks);
        _definition = definition;
        foreach (var widget in definition.Widgets)
        {
            switch (widget.Kind)
            {
                case NativeWidgetKind.Gadget when widget.Animation is { } name && frameTicks(name) is { Count: > 0 } ticks:
                    _gadgets[widget.Id] = new GadgetAnimation(ticks, widget.Mode);
                    break;
                case NativeWidgetKind.Group:
                    _hidden.Add(widget.Id);
                    foreach (var member in widget.Members) _hidden.Add(member);
                    break;
                case NativeWidgetKind.ButtonAnimation:
                    _buttonAnimations.Enqueue(widget);
                    foreach (var button in widget.Buttons) _unrevealed.Add(button);
                    break;
            }
        }
    }

    public NativeScreenDefinition Definition => _definition;

    /// <summary>Whether a build-up is still running; the screen takes no input meanwhile.</summary>
    public bool InButtonAnimation => _buttonAnimation is not null || _buttonAnimations.Count > 0;

    public GadgetAnimation? Gadget(int id) => _gadgets.GetValueOrDefault(id);

    /// <summary>Whether the widget draws: visible, and not a button its build-up has yet to show.</summary>
    public bool Shows(int id) => !_hidden.Contains(id) && !_unrevealed.Contains(id);

    public bool Enabled(int id) => !_disabled.Contains(id);

    /// <summary><c>0x424394</c>: show or hide a widget.</summary>
    public void Show(int id, bool visible)
    {
        if (visible) _hidden.Remove(id);
        else _hidden.Add(id);
    }

    public void Enable(int id, bool enabled)
    {
        if (enabled) _disabled.Remove(id);
        else _disabled.Add(id);
    }

    /// <summary>
    /// Advances the screen to <paramref name="now"/> (milliseconds) and returns
    /// how many times the build-up's sound (0xBA, ACTIVE.WAV) starts.
    /// </summary>
    public int Update(long now)
    {
        if (InButtonAnimation) return UpdateButtonAnimation(now);
        if (_lastPoll < 0) _lastPoll = now;
        if (now - _lastPoll <= PollMilliseconds) return 0;
        var steps = (now - _lastPoll) / PollMilliseconds;
        // A long stall would otherwise replay whole loops; their phase is all that shows.
        for (var step = 0L; step < Math.Min(steps, 256); step++) StepGadgets();
        _lastPoll = now;
        return 0;
    }

    private int UpdateButtonAnimation(long now)
    {
        var sounds = 0;
        if (_buttonAnimation is null)
        {
            _buttonAnimation = _buttonAnimations.Dequeue();
            (_finished, _started, _nextButton) = (0, 0, 0);
            sounds++;
        }
        if (_lastRedraw < 0) _lastRedraw = now;
        if (now - _lastRedraw > ButtonAnimationRedrawMilliseconds)
        {
            _lastRedraw = now;
            StepGadgets();
        }

        var gadgets = _buttonAnimation.Gadgets;
        var buttons = _buttonAnimation.Buttons;
        if (_started + 1 < gadgets.Count && Gadget(gadgets[_started]) is { Frame: 2 })
        {
            Gadget(gadgets[_started + 1])?.Start(GadgetAnimationMode.OneOff);
            _started++;
            sounds++;
        }
        while (_finished < gadgets.Count && Gadget(gadgets[_finished]) is not { Finished: false })
        {
            Show(gadgets[_finished], false);
            _finished++;
            while (_nextButton < buttons.Count && _hidden.Contains(buttons[_nextButton])) _nextButton++;
            if (_nextButton < buttons.Count) _unrevealed.Remove(buttons[_nextButton++]);
        }
        if (_finished >= gadgets.Count)
        {
            foreach (var button in buttons) _unrevealed.Remove(button);
            _buttonAnimation = null;
            _lastPoll = now;
        }
        return sounds;
    }

    private void StepGadgets()
    {
        foreach (var (id, gadget) in _gadgets)
            if (!_hidden.Contains(id) && !_disabled.Contains(id)) gadget.Step();
    }
}
