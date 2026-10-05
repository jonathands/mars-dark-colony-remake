using System.Drawing;
using DarkColony.Engine.Data;

namespace DarkColony.Presentation;

/// <summary>
/// Source-layout identity for the common unit controls in
/// <c>intrface/maine</c> group 40.  Keep screen coordinates, source gadget
/// IDs, and mainbut frames together so the gameplay adapter cannot drift into
/// an unrelated port-only HUD layout.
/// </summary>
public sealed record GameplayHudButton(int UiId, Rectangle Bounds, int Frame, string Label);
public sealed record GameplayHudReadout(int UiId, Point Origin, int CharacterCapacity);

/// <remarks>
/// Every rectangle is placed on <see cref="Screen"/>: the authored 640x480
/// position, anchored to the right and bottom edges of a larger view
/// (<see cref="GameplayScreen.Anchor(Rectangle)"/>), so drawing and hit tests agree.
/// </remarks>
public sealed class GameplayHudLayout
{
    private readonly InterfaceDefinition? source;

    private GameplayHudLayout(InterfaceDefinition? source, GameplayScreen screen)
    {
        this.source = source;
        Screen = screen;
        BuildTab = Tab(source, 0, 3, "BUILD", new Rectangle(518, 92, 40, 20), 77);
        ResearchTab = Tab(source, 1, 4, "RESEARCH", new Rectangle(557, 92, 41, 20), 78);
        OptionsTab = Tab(source, 2, 5, "OPTIONS", new Rectangle(598, 92, 40, 20), 79);
        // The tab buttons have no picture of their own (frame -1). Pictures
        // 3-5 draw all three tabs as one strip over them; its frame shows
        // which tab is pressed.
        TabStrip = screen.Anchor(source?.Controls.TryGetValue(3, out var strip) == true ? ToRectangle(strip.Bounds) : new Rectangle(521, 96, 110, 12));
        Stop = Button(source, 150, "STOP", new Rectangle(518, 112, 59, 41), 62);
        MoveOnly = Button(source, 33, "MOVE", new Rectangle(518, 153, 59, 41), 63);
        MoveAndAttack = Button(source, 35, "MOVE & ATTACK", new Rectangle(518, 194, 59, 41), 65);
        Waypoints = Button(source, 36, "WAYPOINT", new Rectangle(518, 235, 59, 41), 66);
        Contextual = Button(source, 37, "DEPLOY", new Rectangle(518, 276, 59, 41), 74);
        Secondary = Button(source, 143, "SECOND ATTACK", new Rectangle(518, 317, 59, 41), 2);
        Quit = Button(source, 62, "QUIT", new Rectangle(518, 111, 59, 41), 1);
        SaveGame = Button(source, 63, "SAVE GAME", new Rectangle(518, 152, 59, 41), 4);
        Options = Button(source, 64, "OPTIONS", new Rectangle(518, 193, 59, 41), 0);
        Allies = Button(source, 151, "ALLIES MENU", new Rectangle(518, 234, 59, 41), 117);
        Pause = Button(source, 196, "PAUSE", new Rectangle(518, 275, 59, 41), 131);
        Objectives = Button(source, 202, "OBJECTIVES", new Rectangle(518, 316, 59, 41), 118);
        LastMessage = Button(source, 147, "LAST MSG", new Rectangle(4, 460, 20, 19), 40);
        NextMessage = Button(source, 149, "NEXT MSG", new Rectangle(24, 460, 20, 19), 57);
        PetraCounter = Button(source, 75, "P7", new Rectangle(524, 456, 72, 17), 104);

        // `maine` declares control 79 as the 15-character identity strip at
        // the foot of the command panel. Original gameplay captures show the
        // selected unit name here (for example, "Trooper"), not a command
        // mode. Controls 204/203 are the two wide lower readouts; their exact
        // native writers remain unresolved, so the port reserves them for
        // opt-in diagnostics rather than presenting invented unit stats as
        // original UI.
        PanelIdentity = Readout(source, 79, new Point(520, 404), 15);
        LowerReadoutTop = Readout(source, 204, new Point(10, 425), 72);
        LowerReadoutBottom = Readout(source, 203, new Point(10, 440), 72);
        MessageStatus = Readout(source, 148, new Point(50, 462), 61);
        AuxiliaryReadout = Readout(source, 200, new Point(480, 463), 3);
        SetWaypointsMessage = Message(source, 180, "Set waypoints.");
        SelectTargetMessage = Message(source, 181, "Select target.");
        TooManyUnitsMessage = Message(source, 182, "Too many units");
        IssuingRefundMessage = Message(source, 183, "Issuing refund");
    }

    /// <summary>The screen the rectangles are placed on.</summary>
    public GameplayScreen Screen { get; }

    public GameplayHudButton Stop { get; }
    public GameplayHudButton BuildTab { get; }
    public GameplayHudButton ResearchTab { get; }
    public GameplayHudButton OptionsTab { get; }
    public Rectangle TabStrip { get; }
    public GameplayHudButton MoveOnly { get; }
    public GameplayHudButton MoveAndAttack { get; }
    public GameplayHudButton Waypoints { get; }
    public GameplayHudButton Contextual { get; }
    public GameplayHudButton Secondary { get; }
    public GameplayHudButton Quit { get; }
    public GameplayHudButton SaveGame { get; }
    public GameplayHudButton Options { get; }
    public GameplayHudButton Allies { get; }
    public GameplayHudButton Pause { get; }
    public GameplayHudButton Objectives { get; }
    public GameplayHudButton LastMessage { get; }
    public GameplayHudButton NextMessage { get; }
    public GameplayHudButton PetraCounter { get; }
    public GameplayHudReadout PanelIdentity { get; }
    public GameplayHudReadout LowerReadoutTop { get; }
    public GameplayHudReadout LowerReadoutBottom { get; }
    public GameplayHudReadout MessageStatus { get; }
    public GameplayHudReadout AuxiliaryReadout { get; }
    public string SetWaypointsMessage { get; }
    public string SelectTargetMessage { get; }
    public string TooManyUnitsMessage { get; }
    public string IssuingRefundMessage { get; }

    /// <summary>
    /// Resolves a data-authored production or research gadget by its native UI
    /// ID. The caller owns the explicit fallback for the no-data host path.
    /// </summary>
    public Rectangle CatalogBounds(int uiId, Rectangle fallback) => Screen.Anchor(
        source?.Controls.TryGetValue(uiId, out var control) == true
            ? ToRectangle(control.Bounds)
            : fallback);

    /// <summary>The <c>mainbut</c> frame a production or research gadget draws, if authored.</summary>
    public int? CatalogFrame(int uiId) =>
        source?.Controls.TryGetValue(uiId, out var control) == true ? control.Frame : null;

    /// <summary>
    /// The text a control shows in UI 79 while hovered: the <c>textmsg</c>
    /// with the control's own number (0x4337c8 → 0x4226a0), for example
    /// "Exploiter 1500".
    /// </summary>
    public string? ControlText(int uiId) => source?.LabelFor(uiId);

    public static GameplayHudLayout Load(GameInstallation? installation, GameplayScreen? screen = null)
    {
        screen ??= GameplayScreen.Classic;
        if (installation is null) return new GameplayHudLayout(null, screen);
        try
        {
            return new GameplayHudLayout(InterfaceDefinition.Load(installation.DataFile("intrface", "maine")), screen);
        }
        catch (IOException)
        {
            // The host still exposes its data-location error screen without
            // requiring UI source files to be present.
            return new GameplayHudLayout(null, screen);
        }
        catch (FormatException)
        {
            // Unknown modded interface syntax cannot silently become gameplay
            // behavior. Keep the explicit shipped-layout fallback instead.
            return new GameplayHudLayout(null, screen);
        }
    }

    private GameplayHudButton Button(InterfaceDefinition? source, int id, string fallbackLabel, Rectangle fallbackBounds, int fallbackFrame)
    {
        if (source?.Controls.TryGetValue(id, out var control) == true && control.Frame is { } frame)
            return new GameplayHudButton(id, Screen.Anchor(ToRectangle(control.Bounds)), frame, source.LabelFor(id) ?? fallbackLabel);
        return new GameplayHudButton(id, Screen.Anchor(fallbackBounds), fallbackFrame, fallbackLabel);
    }

    private GameplayHudButton Tab(InterfaceDefinition? source, int controlId, int pictureId, string fallbackLabel, Rectangle fallbackBounds, int fallbackFrame)
    {
        if (source?.Controls.TryGetValue(controlId, out var control) == true &&
            source.Controls.TryGetValue(pictureId, out var picture) && picture.Frame is { } frame)
            return new GameplayHudButton(controlId, Screen.Anchor(ToRectangle(control.Bounds)), frame, source.LabelFor(controlId) ?? fallbackLabel);
        return new GameplayHudButton(controlId, Screen.Anchor(fallbackBounds), fallbackFrame, fallbackLabel);
    }

    private GameplayHudReadout Readout(InterfaceDefinition? source, int id, Point fallbackOrigin, int fallbackCapacity)
    {
        if (source?.Controls.TryGetValue(id, out var control) == true)
            return new GameplayHudReadout(id, Screen.Anchor(new Point(control.Bounds.X, control.Bounds.Y)), control.Bounds.Width);
        return new GameplayHudReadout(id, Screen.Anchor(fallbackOrigin), fallbackCapacity);
    }

    private static string Message(InterfaceDefinition? source, int id, string fallback) =>
        source?.LabelFor(id) ?? fallback;

    private static Rectangle ToRectangle(InterfaceRectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
}
