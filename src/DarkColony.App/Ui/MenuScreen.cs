namespace DarkColony.App.Ui;

public enum MenuScreenId
{
    Main,
    NewGame,
    LoadGame,
    SinglePlayer,
    Encyclopedia,
    NetworkOptions,
    NetworkConnect,
    Story,
    Gameplay,
}

public sealed record MenuButton(
    int Id,
    Rectangle Bounds,
    string Label,
    Action Action,
    bool Selected = false,
    string? ArtName = null);
