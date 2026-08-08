using System.Drawing;
using DarkColony.Engine.Data;

namespace DarkColony.App.Ui;

/// <summary>
/// Source-layout identity for the common unit controls in
/// <c>intrface/maine</c> group 40.  Keep screen coordinates, source gadget
/// IDs, and mainbut frames together so the gameplay adapter cannot drift into
/// an unrelated port-only HUD layout.
/// </summary>
internal sealed record GameplayHudButton(int UiId, Rectangle Bounds, int Frame, string Label);
internal sealed record GameplayHudReadout(int UiId, Point Origin);

internal sealed class GameplayHudLayout
{
    private readonly InterfaceDefinition? source;

    private GameplayHudLayout(InterfaceDefinition? source)
    {
        this.source = source;
        BuildTab = Tab(source, 0, 3, "BUILD", new Rectangle(518, 92, 40, 20), 77);
        ResearchTab = Tab(source, 1, 4, "RESEARCH", new Rectangle(557, 92, 41, 20), 78);
        OptionsTab = Tab(source, 2, 5, "OPTIONS", new Rectangle(598, 92, 40, 20), 79);
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

        // `maine` in_text controls: these are source origins, while the wider
        // App bounds remain a port diagnostic area for decoded live state.
        CommandStatus = Readout(source, 79, new Point(520, 404));
        SelectedName = Readout(source, 204, new Point(10, 425));
        SelectedStats = Readout(source, 203, new Point(10, 440));
        ResourceStatus = Readout(source, 148, new Point(50, 462));
    }

    public GameplayHudButton Stop { get; }
    public GameplayHudButton BuildTab { get; }
    public GameplayHudButton ResearchTab { get; }
    public GameplayHudButton OptionsTab { get; }
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
    public GameplayHudReadout CommandStatus { get; }
    public GameplayHudReadout SelectedName { get; }
    public GameplayHudReadout SelectedStats { get; }
    public GameplayHudReadout ResourceStatus { get; }

    /// <summary>
    /// Resolves a data-authored production or research gadget by its native UI
    /// ID. The caller owns the explicit fallback for the no-data host path.
    /// </summary>
    public Rectangle CatalogBounds(int uiId, Rectangle fallback) =>
        source?.Controls.TryGetValue(uiId, out var control) == true
            ? ToRectangle(control.Bounds)
            : fallback;

    public static GameplayHudLayout Load(GameInstallation? installation)
    {
        if (installation is null) return new GameplayHudLayout(null);
        try
        {
            return new GameplayHudLayout(InterfaceDefinition.Load(installation.DataFile("intrface", "maine")));
        }
        catch (IOException)
        {
            // The host still exposes its data-location error screen without
            // requiring UI source files to be present.
            return new GameplayHudLayout(null);
        }
        catch (FormatException)
        {
            // Unknown modded interface syntax cannot silently become gameplay
            // behavior. Keep the explicit shipped-layout fallback instead.
            return new GameplayHudLayout(null);
        }
    }

    private static GameplayHudButton Button(InterfaceDefinition? source, int id, string fallbackLabel, Rectangle fallbackBounds, int fallbackFrame)
    {
        if (source?.Controls.TryGetValue(id, out var control) == true && control.Frame is { } frame)
            return new GameplayHudButton(id, ToRectangle(control.Bounds), frame, source.LabelFor(id) ?? fallbackLabel);
        return new GameplayHudButton(id, fallbackBounds, fallbackFrame, fallbackLabel);
    }

    private static GameplayHudButton Tab(InterfaceDefinition? source, int controlId, int pictureId, string fallbackLabel, Rectangle fallbackBounds, int fallbackFrame)
    {
        if (source?.Controls.TryGetValue(controlId, out var control) == true &&
            source.Controls.TryGetValue(pictureId, out var picture) && picture.Frame is { } frame)
            return new GameplayHudButton(controlId, ToRectangle(control.Bounds), frame, source.LabelFor(controlId) ?? fallbackLabel);
        return new GameplayHudButton(controlId, fallbackBounds, fallbackFrame, fallbackLabel);
    }

    private static GameplayHudReadout Readout(InterfaceDefinition? source, int id, Point fallbackOrigin)
    {
        if (source?.Controls.TryGetValue(id, out var control) == true)
            return new GameplayHudReadout(id, new Point(control.Bounds.X, control.Bounds.Y));
        return new GameplayHudReadout(id, fallbackOrigin);
    }

    private static Rectangle ToRectangle(InterfaceRectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
}
