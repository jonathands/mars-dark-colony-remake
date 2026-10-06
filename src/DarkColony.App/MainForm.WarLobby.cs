using DarkColony.App.Diagnostics;
using DarkColony.App.Ui;
using DarkColony.App.Rendering;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Interface;
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

/// <summary>The Single Player War lobby: map list, player rows, and game options.</summary>
public sealed partial class MainForm
{
    private void StartSinglePlayerWar()
    {
        EnsureSinglePlayerMaps();
        var maps = _singlePlayerMaps;
        if (maps.Count == 0)
        {
            _status = $"No complete {(_grayRace ? "Gray" : "Human")} War maps were found in scenario\\mplayer.";
            return;
        }

        _singlePlayerMapIndex = Math.Clamp(_singlePlayerMapIndex, 0, maps.Count - 1);
        var selected = maps[_singlePlayerMapIndex];
        var localPlayer = _warLobbyPlayers.FirstOrDefault(player => player.Type == WarLobbyPlayerType.Human);
        if (localPlayer is null)
        {
            _status = "Single Player War needs one Human player slot.";
            return;
        }

        // The native session start (WarSession) gives the occupied rows the
        // map's team positions at random. Single Player War has one human,
        // the first Human row; any other Human row plays as a computer.
        var firstHuman = Array.IndexOf(_warLobbyPlayers, localPlayer);
        var rows = _warLobbyPlayers.Select((player, index) => new WarLobbyRow(
            player.Type switch
            {
                WarLobbyPlayerType.Human when index == firstHuman => WarSeatKind.Human,
                WarLobbyPlayerType.Human or WarLobbyPlayerType.Ai => WarSeatKind.Computer,
                WarLobbyPlayerType.AiPlus => WarSeatKind.ComputerPlus,
                _ => WarSeatKind.None,
            },
            player.Gray ? 1 : 0)).ToArray();
        var rules = _simulationRules ??= SimulationRules.Load(_installation!);
        if (!selected.TryCreateSession(rows, 0, CurrentWarSettings(), rules.RandomTable, out var launch))
        {
            _status = $"{selected.DisplayName} has only {WarSession.PlayerPositions(selected.Stem)} player positions for these rows.";
            return;
        }

        _selectedScenario = new ScenarioChoice("mplayer", launch.Stem, WarLaunch: launch);
        _localPlayerTeam = launch.LocalTeamId;
        _grayRace = localPlayer.Gray;
        _status = $"Single Player War: {launch.Stem.ToUpperInvariant()} as {(localPlayer.Gray ? "Gray" : "Human")} team {_localPlayerTeam + 1}; P7 { _warP7QuantityMultiplier}% / flow {_warP7FlowMultiplier}%.";
        RuntimeLog.Info($"{_status} Seats: {string.Join(", ", launch.Seats!.Where(seat => seat is not null).Select(seat => $"team {seat!.TeamId + 1} {seat.Kind}"))}.");
        ShowScreen(MenuScreenId.Gameplay);
        if (GrantP7 is { } grant && _scenarioSimulation?.EconomyForTeam(_localPlayerTeam) is { } economy)
        {
            economy.AddP7(grant);
            _grantedP7 = true;
            RuntimeLog.Info($"Diagnostic --grant-p7: team {_localPlayerTeam + 1} received {grant} P7; this game will not be saved.");
        }
    }

    private void EnsureSinglePlayerMaps()
    {
        if (_singlePlayerMaps.Count != 0 || _installation is null) return;
        _singlePlayerMaps = SinglePlayerWarCatalog.Load(_installation).Scenarios;
        _singlePlayerMapIndex = Math.Clamp(_singlePlayerMapIndex, 0, Math.Max(0, _singlePlayerMaps.Count - 1));
    }

    /// <summary>multie push buttons 28 and 29 (<c>list 27 -1</c>, <c>list 27 1</c>) move the rows shown, not the choice.</summary>
    private void ScrollSinglePlayerMapList(int step)
    {
        WarMapList()?.Step(step);
        _surface.Invalidate();
    }

    private static void RemapWarControlPalette(byte[] rgba)
    {
        for (var pixel = 0; pixel < rgba.Length; pixel += 4)
        {
            if (rgba[pixel + 3] == 0) continue;
            if (rgba[pixel] == 12 && rgba[pixel + 1] == 36 && rgba[pixel + 2] == 0)
            {
                rgba[pixel] = 7;
                rgba[pixel + 1] = 7;
                rgba[pixel + 2] = 7;
            }
            else if (rgba[pixel] == 28 && rgba[pixel + 1] == 77 && rgba[pixel + 2] == 0)
            {
                rgba[pixel] = 65;
                rgba[pixel + 1] = 8;
                rgba[pixel + 2] = 0;
            }
            else if (rgba[pixel] == 48 && rgba[pixel + 1] == 117 && rgba[pixel + 2] == 0)
            {
                rgba[pixel] = 175;
                rgba[pixel + 1] = 11;
                rgba[pixel + 2] = 15;
            }
        }
    }

    // The free-war lobby is intrface/multie over the tcpwait picture.
    private const string WarLobbyScreen = "multie";
    private const string WarLobbyPalette = "tcpwait";
    private const int WarMapListId = 27;
    private const int WarMapScrollId = 30;
    // The captured lobby starts each map's description at x 373, cell 43 of the list.
    private const int WarMapDescriptionCell = 43;

    private NativeListView? _warMapList;

    /// <summary>The map list (multie list 27), its selection following the chosen map.</summary>
    private NativeListView? WarMapList()
    {
        EnsureSinglePlayerMaps();
        if (NativeScreen(WarLobbyScreen) is not { } screen || screen.Widget(WarMapListId) is not { } list ||
            NativeFont(screen, list.Font) is not { } font) return null;
        if (_warMapList is null)
        {
            _warMapList = new NativeListView(list.Bounds.Height / font.Sprite.Frames[0].Height);
            _warMapList.SetCount(_singlePlayerMaps.Count);
            // The port opens the lobby on the chosen map, so it starts in view.
            if (_singlePlayerMapIndex >= _warMapList.VisibleRows) _warMapList.SetTop(_singlePlayerMapIndex);
        }
        _warMapList.SetCount(_singlePlayerMaps.Count);
        _warMapList.Select(_singlePlayerMapIndex);
        return _warMapList;
    }

    private void DrawSinglePlayerMapSelection(Graphics graphics)
    {
        EnsureSinglePlayerMaps();
        if (NativeScreen(WarLobbyScreen) is not { } screen) return;
        var maps = _singlePlayerMaps;
        if (maps.Count == 0)
        {
            DrawMenuText(graphics, "NO WAR MAPS", new Rectangle(29, 200, 535, 18), center: false);
            return;
        }

        _singlePlayerMapIndex = Math.Clamp(_singlePlayerMapIndex, 0, maps.Count - 1);
        if (WarMapList() is { } view && screen.Widget(WarMapListId) is { } list)
        {
            DrawNativeList(graphics, screen, WarLobbyPalette, list, view,
                row => $"{maps[row].DisplayName.PadRight(WarMapDescriptionCell)}{WarMapDescription(maps[row].Stem)}");
            if (screen.Widget(WarMapScrollId) is { } scroll) DrawNativeScroll(graphics, screen, WarLobbyPalette, scroll, view.ScrollSpan);
        }
        DrawWarLobbyPlayers(graphics, screen);
        DrawWarLobbyOptions(graphics, screen);
    }

    private int? _warLobbyHover;

    /// <summary>The lobby's own push and check buttons (player rows and options) play HLIGHT.WAV too.</summary>
    private void SetWarLobbyHover(Point point)
    {
        int? hovered = null;
        if (NativeScreen(WarLobbyScreen) is { } screen)
        {
            foreach (var widget in screen.Widgets)
            {
                if (widget.Kind is not (NativeWidgetKind.PushButton or NativeWidgetKind.CheckButton) || !ToRectangle(widget.Bounds).Contains(point)) continue;
                if (widget.Id is >= 16 and <= 23 && _warLobbyPlayers[widget.Id - 16].Type != WarLobbyPlayerType.Human) continue;
                if (_buttons.Any(button => button.NativeId == widget.Id)) continue;
                hovered = widget.Id;
            }
        }
        if (hovered == _warLobbyHover) return;
        _warLobbyHover = hovered;
        if (hovered is not null) PlayInterfaceSound(ButtonHighlightSound);
    }

    /// <summary>A multie push or check button, highlighted under the pointer.</summary>
    private void DrawWarLobbyButton(Graphics graphics, NativeScreenDefinition screen, int id, bool selected = false, string? label = null)
    {
        if (screen.Widget(id) is not { } button) return;
        var down = button.Kind == NativeWidgetKind.CheckButton ? selected : PressedOver(button.Bounds);
        DrawNativeButton(graphics, screen, WarLobbyPalette, button, down, PointerOver(button.Bounds), label);
    }

    private void DrawWarLobbyLabel(Graphics graphics, NativeScreenDefinition screen, int id)
    {
        if (screen.Widget(id) is not { Message: not null } label || screen.Message(label.Message) is not { } text) return;
        DrawNativeText(graphics, screen, WarLobbyPalette, label.Bounds, label.Align, text, label.Font, label.Remap, label.Intensity);
    }

    private void DrawWarLobbyPlayers(Graphics graphics, NativeScreenDefinition screen)
    {
        // Column headings: labels 136-141.
        for (var id = 136; id <= 141; id++) DrawWarLobbyLabel(graphics, screen, id);
        var warGreen = Color.FromArgb(91, 203, 0);
        for (var index = 0; index < _warLobbyPlayers.Length; index++)
        {
            var player = _warLobbyPlayers[index];
            var y = 21 + index * 19;
            var typeColor = player.Type == WarLobbyPlayerType.Human ? Color.FromArgb(79, 7, 7) : warGreen;
            DrawMenuText(graphics, WarLobbyTypeLabel(player.Type), new Rectangle(45, y, 50, 16), remap: typeColor);
            DrawMenuText(graphics, player.Gray ? "Gray" : "Human", new Rectangle(150, y, 50, 16), remap: warGreen);
            var name = !InNetworkLobby && index == 0 && !string.IsNullOrWhiteSpace(_leaderName) ? _leaderName : player.Name;
            DrawMenuText(graphics, name, new Rectangle(246, y, 160, 16), center: false, remap: warGreen);
            var type = Animation("knobe.fin", "PLAYERTYPE");
            if (type is not null) DrawAnimationFrame(graphics, "knobe.fin", type.FirstFrame + (int)player.Type, 99, y);
            var opacity = player.Type == WarLobbyPlayerType.None ? 0.32f : 1f;
            var race = Animation("knobe.fin", "RACEFACE");
            if (race is not null) DrawAnimationFrame(graphics, "knobe.fin", race.FirstFrame + (player.Gray ? 1 : 0), 204, y, opacity);
            var colors = Animation("knobe.fin", "CUBE");
            if (colors is not null) DrawAnimationFrame(graphics, "knobe.fin", colors.FirstFrame + player.Color, 425, y + 2, opacity);
            var teams = Animation("knobe.fin", "TEAMS");
            if (teams is not null) DrawAnimationFrame(graphics, "knobe.fin", teams.FirstFrame + player.Team, 512, y + 2, opacity);
            // Colour and team wheels: push buttons 32-47 and 150-165.
            DrawWarLobbyButton(graphics, screen, 32 + index);
            DrawWarLobbyButton(graphics, screen, 40 + index);
            DrawWarLobbyButton(graphics, screen, 150 + index);
            DrawWarLobbyButton(graphics, screen, 158 + index);
            // Ready: check buttons 16-23. The captured lobby shows the box on
            // the human row only.
            if (player.Type == WarLobbyPlayerType.Human) DrawWarLobbyButton(graphics, screen, 16 + index, player.Ready);
        }
    }

    private void DrawWarLobbyOptions(Graphics graphics, NativeScreenDefinition screen)
    {
        // Labels 104-128 and the check buttons after each: storage cells
        // 105-108, artifacts 110-113, erupting 115/116, renewable 118/119.
        foreach (var id in (int[])[104, 109, 114, 117, 120, 124, 128]) DrawWarLobbyLabel(graphics, screen, id);
        for (var value = 0; value < 4; value++)
        {
            DrawWarLobbyButton(graphics, screen, 105 + value, _warStorageCells == value);
            DrawWarLobbyButton(graphics, screen, 110 + value, _warArtifacts == value);
        }
        DrawWarLobbyButton(graphics, screen, 115, !_warEruptingVents);
        DrawWarLobbyButton(graphics, screen, 116, _warEruptingVents);
        DrawWarLobbyButton(graphics, screen, 118, !_warRenewableVents);
        DrawWarLobbyButton(graphics, screen, 119, _warRenewableVents);
        // P7 quantity, P7 flow and commander rank: in_text 121/125/129
        // between push buttons 122/123, 126/127 and 130/131.
        DrawMenuText(graphics, $"{_warP7QuantityMultiplier}%", new Rectangle(539, 400, 64, 18));
        DrawMenuText(graphics, $"{_warP7FlowMultiplier}%", new Rectangle(539, 418, 64, 18));
        var rank = _warLobbyPlayers.FirstOrDefault(player => player.Type == WarLobbyPlayerType.Human)?.Gray == true
            ? new[] { "XIMAL.", "IDRAC.", "SITRUC.", "REGLIA." }[_warCommanderRank]
            : new[] { "LEUT.", "CAPT.", "MAJ.", "COL." }[_warCommanderRank];
        DrawMenuText(graphics, rank, new Rectangle(539, 436, 64, 16));
        foreach (var id in (int[])[122, 123, 126, 127, 130, 131]) DrawWarLobbyButton(graphics, screen, id);
    }

    private static string WarMapDescription(string stem)
    {
        var players = stem.Length > 1 && char.IsDigit(stem[1]) ? stem[1] : '?';
        var terrain = stem.Length == 0 ? "Unknown" : char.ToLowerInvariant(stem[0]) switch
        {
            'a' => "Atlantis",
            'd' => "Desert",
            'j' => "Jungle",
            _ => "Unknown",
        };
        // The native lobby leaves a space before the closing parenthesis.
        return $"({players} Player {terrain} Map )";
    }

    private void SelectSinglePlayerMapAt(Point point)
    {
        if (NativeScreen(WarLobbyScreen) is not { } screen || screen.Widget(WarMapListId) is not { } list ||
            !ToRectangle(list.Bounds).Contains(point) || WarMapList() is not { } view || NativeFont(screen, list.Font) is not { } font) return;
        var row = view.RowAt(point.Y - list.Bounds.Y, font.Sprite.Frames[0].Height);
        if (row < 0) return;
        _singlePlayerMapIndex = row;
        view.Select(row);
        _status = $"Single Player War map: {_singlePlayerMaps[row].DisplayName}.";
    }

    /// <summary>A drag on the map list's scroll bar (multie scroll 30) moves the rows shown.</summary>
    private void ScrollSinglePlayerMaps(Point point)
    {
        if (NativeScreen(WarLobbyScreen)?.Widget(WarMapScrollId) is not { } scroll || WarMapList() is not { } view) return;
        view.Drag(point.Y - scroll.Bounds.Y, scroll.Bounds.Height);
    }

    private static string WarLobbyTypeLabel(WarLobbyPlayerType type) => type switch
    {
        WarLobbyPlayerType.Ai => "AI",
        WarLobbyPlayerType.AiPlus => "AI+",
        WarLobbyPlayerType.Human => "Human",
        _ => "None",
    };

    private bool HandleWarLobbyClick(Point point)
    {
        // Control geometry and value ranges are recovered from intrface/multie
        // and dc.exe's 0x4112dd-0x4116f6 event dispatcher.
        for (var index = 0; index < _warLobbyPlayers.Length; index++)
        {
            var y = 21 + index * 19;
            var player = _warLobbyPlayers[index];
            if (new Rectangle(99, y, 36, 16).Contains(point))
            {
                player.Type = (WarLobbyPlayerType)(((int)player.Type + 1) % 4);
                return true;
            }
            // The source face gadget is 204..230, but treating the adjacent
            // read-only Race label as the same target makes the selected
            // faction discoverable without changing the native visual layout.
            if (new Rectangle(141, y, 89, 22).Contains(point))
            {
                ToggleWarLobbyRace(index);
                return true;
            }
            if (new Rectangle(409, y + 1, 14, 14).Contains(point))
            {
                player.Color = (player.Color + 15) % 16;
                return true;
            }
            if (new Rectangle(473, y + 1, 14, 14).Contains(point))
            {
                player.Color = (player.Color + 1) % 16;
                return true;
            }
            if (new Rectangle(496, y + 1, 14, 14).Contains(point))
            {
                player.Team = (player.Team + 15) % 16;
                return true;
            }
            if (new Rectangle(560, y + 1, 14, 14).Contains(point))
            {
                player.Team = (player.Team + 1) % 16;
                return true;
            }
            if (player.Type == WarLobbyPlayerType.Human && new Rectangle(610, y, 27, 17).Contains(point))
            {
                player.Ready = !player.Ready;
                return true;
            }
        }

        if (TrySelectWarOption(point, 326, ref _warStorageCells, 4, 456) ||
            TrySelectWarOption(point, 344, ref _warArtifacts, 4, 456)) return true;
        if (TrySelectWarBinaryOption(point, 362, ref _warEruptingVents) ||
            TrySelectWarBinaryOption(point, 380, ref _warRenewableVents)) return true;
        if (TryAdjustWarMultiplier(point, 400, ref _warP7QuantityMultiplier) ||
            TryAdjustWarMultiplier(point, 418, ref _warP7FlowMultiplier)) return true;
        if (point.Y is >= 435 and < 453)
        {
            if (point.X is >= 521 and < 562)
            {
                _warCommanderRank = Math.Max(0, _warCommanderRank - 1);
                return true;
            }
            if (point.X is >= 603 and < 644)
            {
                _warCommanderRank = Math.Min(3, _warCommanderRank + 1);
                return true;
            }
        }
        return false;
    }

    private void ToggleWarLobbyRace(int playerIndex)
    {
        var player = _warLobbyPlayers[playerIndex];
        player.Gray = !player.Gray;
        if (player.Type != WarLobbyPlayerType.Human) return;

        // The session start gives the row's race to whichever team it lands on.
        _grayRace = player.Gray;
        _status = $"Player {playerIndex + 1}: {(player.Gray ? "Gray" : "Human")}.";
    }

    private static bool TrySelectWarOption(Point point, int y, ref int selected, int count, int start)
    {
        if (point.Y < y || point.Y >= y + 18 || point.X < start || point.X >= start + count * 41) return false;
        selected = (point.X - start) / 41;
        return true;
    }

    private static bool TrySelectWarBinaryOption(Point point, int y, ref bool selected)
    {
        if (point.Y < y || point.Y >= y + 18 || point.X < 538 || point.X >= 620) return false;
        selected = point.X >= 579;
        return true;
    }

    private static bool TryAdjustWarMultiplier(Point point, int y, ref int percent)
    {
        if (point.Y < y || point.Y >= y + 18) return false;
        if (point.X is >= 521 and < 562)
        {
            percent = Math.Max(25, percent - 25);
            return true;
        }
        if (point.X is >= 603 and < 644)
        {
            percent = Math.Min(500, percent + 25);
            return true;
        }
        return false;
    }

    private enum WarLobbyPlayerType { Ai, AiPlus, Human, None }

    private sealed class WarLobbyPlayer
    {
        public WarLobbyPlayerType Type { get; set; }
        public bool Gray { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Color { get; set; }
        public int Team { get; set; }
        public bool Ready { get; set; }

        public static WarLobbyPlayer[] CreateDefault() =>
        [
            new() { Type = WarLobbyPlayerType.Human, Name = "Player", Color = 0, Team = 0 },
            new() { Type = WarLobbyPlayerType.Ai, Gray = true, Color = 1, Team = 1 },
            new() { Type = WarLobbyPlayerType.None, Color = 2, Team = 2 },
            new() { Type = WarLobbyPlayerType.None, Gray = true, Color = 3, Team = 3 },
            new() { Type = WarLobbyPlayerType.None, Color = 4, Team = 4 },
            new() { Type = WarLobbyPlayerType.None, Gray = true, Color = 5, Team = 5 },
            new() { Type = WarLobbyPlayerType.None, Color = 6, Team = 6 },
            new() { Type = WarLobbyPlayerType.None, Gray = true, Color = 7, Team = 7 },
        ];
    }

    private readonly record struct ScenarioChoice(
        string Directory,
        string Name,
        string? DisplayName = null,
        IReadOnlyList<ScenarioTeam>? Teams = null,
        SinglePlayerWarLaunch? WarLaunch = null);
}
