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

    private void SelectSinglePlayerMap(int delta)
    {
        EnsureSinglePlayerMaps();
        var maps = _singlePlayerMaps;
        if (maps.Count == 0) return;
        _singlePlayerMapIndex = (_singlePlayerMapIndex + delta + maps.Count) % maps.Count;
            _status = $"Single Player War map: {maps[_singlePlayerMapIndex].Stem.ToUpperInvariant()}.";
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

    private void DrawSinglePlayerMapSelection(Graphics graphics)
    {
        EnsureSinglePlayerMaps();
        var maps = _singlePlayerMaps;
        if (maps.Count == 0)
        {
            DrawMenuText(graphics, "NO WAR MAPS", new Rectangle(29, 200, 535, 18), center: false);
            return;
        }

        const int rowHeight = 14;
        const int visibleRows = 8;
        const int listTop = 200;
        const int listHeight = 114;
        _singlePlayerMapIndex = Math.Clamp(_singlePlayerMapIndex, 0, maps.Count - 1);
        var first = Math.Clamp(_singlePlayerMapIndex - visibleRows / 2, 0, Math.Max(0, maps.Count - visibleRows));
        var listBounds = new Rectangle(29, listTop, 535, listHeight);
        var state = graphics.Save();
        graphics.SetClip(listBounds);
        for (var row = 0; row < visibleRows && first + row < maps.Count; row++)
        {
            var index = first + row;
            var bounds = new Rectangle(29, listTop + row * rowHeight, 535, rowHeight);
            if (index == _singlePlayerMapIndex)
            {
                using var highlight = new SolidBrush(Color.FromArgb(85, 0, 255, 255));
                graphics.FillRectangle(highlight, bounds);
            }
            DrawMenuText(graphics, maps[index].DisplayName, new Rectangle(29, bounds.Y, 280, rowHeight), center: false);
            DrawMenuText(graphics, WarMapDescription(maps[index].Stem), new Rectangle(373, bounds.Y, 190, rowHeight), center: false);
        }
        graphics.Restore(state);

        DrawSinglePlayerScrollThumb(graphics, maps.Count, first, visibleRows);
        DrawWarLobbyPlayers(graphics);
        DrawWarLobbyOptions(graphics);
    }

    private static void DrawSinglePlayerScrollThumb(Graphics graphics, int mapCount, int first, int visibleRows)
    {
        var track = new Rectangle(596, 227, 10, 61); // multie scroll 30
        var maximumFirst = Math.Max(0, mapCount - visibleRows);
        var thumbHeight = Math.Clamp(track.Height * visibleRows / Math.Max(visibleRows, mapCount), 10, track.Height);
        var travel = track.Height - thumbHeight;
        var top = track.Y + (maximumFirst == 0 ? 0 : travel * first / maximumFirst);
        using var fill = new SolidBrush(Color.FromArgb(190, 235, 25, 25));
        using var edge = new Pen(Color.FromArgb(230, 245, 80, 80));
        graphics.FillRectangle(fill, track.X + 1, top, track.Width - 2, thumbHeight);
        graphics.DrawRectangle(edge, track.X, top, track.Width - 1, thumbHeight - 1);
    }

    private void DrawWarLobbyPlayers(Graphics graphics)
    {
        var headings = new[] { ("Type", 36, 102), ("Race", 141, 102), ("Name", 246, 160), ("Color", 409, 84), ("Team", 496, 84), ("Ready", 577, 60) };
        foreach (var (label, x, width) in headings) DrawMenuText(graphics, label, new Rectangle(x, 3, width, 12), center: false);
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
            DrawWarReadyCheckbox(graphics, new Rectangle(610, y - 3, 27, 17), player.Ready, opacity);
        }
    }

    private void DrawWarReadyCheckbox(Graphics graphics, Rectangle bounds, bool selected, float opacity)
    {
        // `multie` declares checkb 16-23 at these bounds. It is a distinct
        // widget from the player-row CHAB masks, so model the checkbox itself
        // rather than treating readiness as a floating text glyph.
        using var fill = new SolidBrush(Color.FromArgb((int)(opacity * 255), 0, 0, 0));
        using var edge = new Pen(Color.FromArgb((int)(opacity * 255), 65, 65, 65));
        using var inset = new Pen(Color.FromArgb((int)(opacity * 255), selected ? 175 : 35, selected ? 11 : 35, selected ? 15 : 35));
        graphics.FillRectangle(fill, bounds);
        graphics.DrawRectangle(edge, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        var marker = new Rectangle(bounds.X + 8, bounds.Y + 3, 11, 11);
        graphics.DrawRectangle(inset, marker.X, marker.Y, marker.Width - 1, marker.Height - 1);
        if (selected) DrawMenuText(graphics, "✓", marker, remap: Color.FromArgb(255, 31, 31));
    }

    private void DrawWarLobbyOptions(Graphics graphics)
    {
        DrawOptionRow(graphics, "Storage Cells", 326, _warStorageCells, ["OFF", "LOW", "MED", "HIGH"]);
        DrawOptionRow(graphics, "Artifacts", 344, _warArtifacts, ["OFF", "LOW", "MED", "HIGH"]);
        DrawOptionRow(graphics, "Erupting Vents", 362, _warEruptingVents ? 1 : 0, ["OFF", "ON"], 538);
        DrawOptionRow(graphics, "Renewable Vents", 380, _warRenewableVents ? 1 : 0, ["OFF", "ON"], 538);
        DrawMultiplierRow(graphics, "P7 Quantity Multiplier", 400, _warP7QuantityMultiplier);
        DrawMultiplierRow(graphics, "P7 Flow Multiplier", 418, _warP7FlowMultiplier);
        var rank = _warLobbyPlayers.FirstOrDefault(player => player.Type == WarLobbyPlayerType.Human)?.Gray == true
            ? new[] { "XIMAL.", "IDRAC.", "SITRUC.", "REGLIA." }[_warCommanderRank]
            : new[] { "LEUT.", "CAPT.", "MAJ.", "COL." }[_warCommanderRank];
        DrawMenuText(graphics, "Commander Rank", new Rectangle(332, 435, 207, 18), center: false, remap: Color.FromArgb(91, 203, 0));
        DrawMenuText(graphics, rank, new Rectangle(539, 436, 64, 16));
        DrawLobbyArrow(graphics, "LEFT", 521, 435);
        DrawLobbyArrow(graphics, "RIGHT", 603, 435);
    }

    private void DrawOptionRow(Graphics graphics, string label, int y, int selected, string[] values, int start = 456)
    {
        var labelColor = Color.FromArgb(91, 203, 0);
        var valueColor = Color.FromArgb(255, 31, 31);
        DrawMenuText(graphics, label, new Rectangle(332, y, start - 332, 18), center: false, remap: labelColor);
        for (var index = 0; index < values.Length; index++)
        {
            var bounds = new Rectangle(start + index * 41, y, 41, 18);
            var button = Animation("knobe.fin", "BUTTON");
            if (button is not null)
                DrawAnimationFrame(
                    graphics,
                    "knobe.fin",
                    index == selected ? LastVisibleFrame(button) : button.FirstFrame,
                    bounds.X,
                    bounds.Y,
                    remapWarControlPalette: true);
            DrawMenuText(graphics, values[index], bounds, remap: valueColor);
        }
    }

    private void DrawMultiplierRow(Graphics graphics, string label, int y, int value)
    {
        DrawMenuText(graphics, label, new Rectangle(332, y, 207, 18), center: false, remap: Color.FromArgb(91, 203, 0));
        DrawMenuText(graphics, $"{value}%", new Rectangle(539, y, 64, 18));
        DrawLobbyArrow(graphics, "LEFT", 521, y);
        DrawLobbyArrow(graphics, "RIGHT", 603, y);
    }

    private void DrawLobbyArrow(Graphics graphics, string animationName, int x, int y)
    {
        var animation = Animation("knobe.fin", animationName);
        if (animation is not null && DrawAnimationFrame(
            graphics, "knobe.fin", animation.FirstFrame, x, y, remapWarControlPalette: true)) return;
        DrawMenuText(graphics, animationName == "LEFT" ? "<" : ">", new Rectangle(x, y, 41, 18));
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
        const int rowHeight = 14;
        const int visibleRows = 8;
        const int listTop = 200;
        const int listHeight = 114;
        var maps = _singlePlayerMaps;
        if (point.X is < 29 or >= 564 || point.Y is < listTop or >= listTop + listHeight || maps.Count == 0) return;
        _singlePlayerMapIndex = Math.Clamp(_singlePlayerMapIndex, 0, maps.Count - 1);
        var first = Math.Clamp(_singlePlayerMapIndex - visibleRows / 2, 0, Math.Max(0, maps.Count - visibleRows));
        var index = first + (point.Y - listTop) / rowHeight;
        if (index >= maps.Count) return;
        _singlePlayerMapIndex = index;
        _status = $"Single Player War map: {maps[index].DisplayName}.";
    }

    private void SelectSinglePlayerMapFromScroll(Point point)
    {
        var maps = _singlePlayerMaps;
        if (maps.Count == 0) return;
        const int top = 227;
        const int height = 61;
        var fraction = Math.Clamp(point.Y - top, 0, height - 1) / (double)(height - 1);
        _singlePlayerMapIndex = (int)Math.Round(fraction * (maps.Count - 1));
        _status = $"Single Player War map: {maps[_singlePlayerMapIndex].DisplayName}.";
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
            if (new Rectangle(610, y, 27, 17).Contains(point))
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
