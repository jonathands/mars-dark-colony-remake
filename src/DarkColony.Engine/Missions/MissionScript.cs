using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Missions;

/// <summary>Action record types, numbered as the <c>.tro</c> parser <c>0x43E658</c> stores them.</summary>
public enum MissionActionType : byte
{
    Ai = 0x00,
    Die = 0x01,
    Reinforce = 0x02,
    Bail = 0x03,
    AiMessage = 0x04,
    NewRate = 0x05,
    SetArray = 0x06,
    SetLifes = 0x07,
    Ally = 0x08,
    DependFiddle = 0x09,
    Waypoint = 0x0a,
    Message = 0x0b,
    ExoMoney = 0x0c,
    SetMoney = 0x0d,
    NewRate2 = 0x0e,
    Reinforce2 = 0x0f,
    NewType = 0x10,
    Artifact = 0x11,
    NoUndeploy = 0x12,
    Abduct = 0x13,
    Vision = 0x14,
    NoPickup = 0x15,
}

/// <summary>One compiled action: its numeric fields as the parser stores them, and an optional expression.</summary>
public sealed record MissionAction(MissionActionType Type, IReadOnlyList<int> Values, byte[]? Expression);

/// <summary>
/// One trigger table entry (<c>0x4FBC48 + slot * 0x10</c>): mode (0 norm,
/// 1 trip), compiled condition, remaining lives, and the action list in the
/// order the executor walks it.
/// </summary>
public sealed record MissionTrigger(int Slot, bool Trip, byte Lives, byte[] Condition, IReadOnlyList<MissionAction> Actions);

/// <summary>
/// A scenario's compiled mission script: the <c>.tro</c> triggers (loader
/// <c>0x43FB90</c>, parser <c>0x43E658</c>) and the <c>.mtg</c> trip map.
/// </summary>
public sealed class MissionScript
{
    /// <summary>The trigger table has 128 entries; the runners never visit higher slots.</summary>
    public const int TriggerSlots = 0x80;

    private MissionScript(IReadOnlyList<MissionTrigger> triggers, MissionTripMap? tripMap)
    {
        Triggers = triggers;
        TripMap = tripMap;
    }

    public IReadOnlyList<MissionTrigger> Triggers { get; }
    public MissionTripMap? TripMap { get; }

    /// <summary>Loads the <c>.tro</c> and <c>.mtg</c> beside a scenario, or null when it has no script.</summary>
    public static MissionScript? LoadForScenario(string scenarioPath)
    {
        var script = Path.ChangeExtension(scenarioPath, ".tro");
        if (!File.Exists(script)) return null;
        var tripPath = Path.ChangeExtension(scenarioPath, ".mtg");
        return Compile(ScenarioTriggers.Load(script), File.Exists(tripPath) ? MissionTripMap.Load(tripPath) : null);
    }

    public static MissionScript Compile(IReadOnlyList<ScenarioTrigger> triggers, MissionTripMap? tripMap = null) =>
        new(triggers.Select(CompileTrigger).ToArray(), tripMap);

    private static MissionTrigger CompileTrigger(ScenarioTrigger trigger)
    {
        var trip = trigger.Mode.ToLowerInvariant() switch
        {
            "norm" => false,
            "trip" => true,
            _ => throw new InvalidDataException($"Trigger {trigger.Id} has unknown mode '{trigger.Mode}'."),
        };
        // The parser pushes each action at the head of the list, so the
        // executor runs a block's commands in reverse source order.
        var actions = trigger.Commands.Select(CompileAction).Where(action => action is not null).Cast<MissionAction>().Reverse().ToArray();
        return new MissionTrigger(trigger.Id, trip, unchecked((byte)trigger.RepeatCount), TriggerExpression.Compile(trigger.Condition), actions);
    }

    private static MissionAction? CompileAction(ScenarioTriggerCommand command)
    {
        var reader = new ArgumentReader(command.Raw[command.Verb.Length..]);
        switch (command.Verb.ToLowerInvariant())
        {
            case "ai": return Fixed(MissionActionType.Ai, reader.Short(), reader.Short());
            case "die": return Fixed(MissionActionType.Die);
            case "aimsg":
            {
                var who = reader.Byte();
                var count = reader.Byte();
                var values = new List<int> { who, count };
                for (var index = 0; index < count && index < 5; index++) values.Add(reader.Short());
                return new MissionAction(MissionActionType.AiMessage, values, null);
            }
            case "reinforce": return Reinforcement(MissionActionType.Reinforce, reader);
            case "reinforce2": return Reinforcement(MissionActionType.Reinforce2, reader);
            case "noundeploy": return Fixed(MissionActionType.NoUndeploy);
            case "artifact": return Fixed(MissionActionType.Artifact, reader.Byte(), reader.Byte());
            case "abduct": return Fixed(MissionActionType.Abduct, reader.Byte(), reader.Byte());
            case "newrate": return Fixed(MissionActionType.NewRate, reader.Byte(), reader.Byte(), reader.Byte());
            case "newtype": return Fixed(MissionActionType.NewType, reader.Byte(), reader.Byte(), reader.Byte());
            case "newrate2": return WithExpression(MissionActionType.NewRate2, reader, reader.Byte(), reader.Byte());
            case "setmoney": return WithExpression(MissionActionType.SetMoney, reader, reader.Byte(), reader.Byte());
            case "setarray": return WithExpression(MissionActionType.SetArray, reader, reader.Short());
            case "setlifes": return WithExpression(MissionActionType.SetLifes, reader, reader.Short());
            case "waypoint":
            {
                var x = reader.Byte();
                var z = reader.Byte();
                var count = reader.Byte();
                if (count is 0 or > 8) throw new InvalidDataException($"waypoint has {count} points (1-8 allowed).");
                var values = new List<int> { x, z, count };
                for (var index = 0; index < count; index++)
                {
                    values.Add(reader.Byte());
                    values.Add(reader.Byte());
                }
                return new MissionAction(MissionActionType.Waypoint, values, null);
            }
            case "exomoney": return Fixed(MissionActionType.ExoMoney, reader.Byte(), reader.Byte());
            case "vision": return Fixed(MissionActionType.Vision, reader.Byte(), reader.Byte(), reader.Byte());
            case "nopickup": return Fixed(MissionActionType.NoPickup, reader.Byte());
            case "ally": return Fixed(MissionActionType.Ally, reader.Byte(), reader.Byte(), reader.Byte());
            case "dfiddle": return Fixed(MissionActionType.DependFiddle, reader.Byte(), reader.Byte(), reader.Byte());
            case "bail": return Fixed(MissionActionType.Bail, reader.Byte(), reader.Byte());
            case "msg": return Fixed(MissionActionType.Message, reader.Byte(), reader.Byte(), reader.Byte(), reader.Byte(), reader.Byte());
            default: throw new InvalidDataException($"Unknown trigger command '{command.Verb}'.");
        }
    }

    private static MissionAction Fixed(MissionActionType type, params int[] values) => new(type, values, null);

    private static MissionAction WithExpression(MissionActionType type, ArgumentReader reader, params int[] values) =>
        new(type, values, TriggerExpression.Compile(reader.Rest()));

    /// <summary>Team, x, z, then five (entity type, count) pairs, read alternately.</summary>
    private static MissionAction Reinforcement(MissionActionType type, ArgumentReader reader)
    {
        var values = new List<int> { reader.Byte(), reader.Byte(), reader.Byte() };
        for (var pair = 0; pair < 5; pair++)
        {
            values.Add(reader.Byte());
            values.Add(reader.Byte());
        }
        return new MissionAction(type, values, null);
    }

    /// <summary><c>strtol</c>-style reader: a missing number reads as 0; stored bytes and words truncate.</summary>
    private sealed class ArgumentReader(string text)
    {
        private int position;

        public int Byte() => unchecked((byte)Number());
        public int Short() => unchecked((short)Number());

        private int Number()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            var start = position;
            if (position < text.Length && text[position] is '-' or '+') position++;
            var digitsStart = position;
            while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
            if (position == digitsStart)
            {
                position = start;
                return 0;
            }
            return int.Parse(text.AsSpan(start, position - start), System.Globalization.CultureInfo.InvariantCulture);
        }

        public string Rest() => text[position..].Trim();
    }
}

/// <summary>
/// The <c>.mtg</c> trip map: width and height bytes, then one trigger ID per
/// cell in file row order. The MAP loader (<c>0x453320</c>) stores
/// <c>id &lt;&lt; 10</c> in the alternate grid at row <c>ysize - 1 - row</c>, so
/// file rows run opposite to world Z like MAP tiles.
/// </summary>
public sealed class MissionTripMap
{
    private readonly byte[] ids;

    private MissionTripMap(int width, int height, byte[] ids)
    {
        Width = width;
        Height = height;
        this.ids = ids;
    }

    public int Width { get; }
    public int Height { get; }

    public static MissionTripMap Load(string path) => Parse(File.ReadAllBytes(path));

    public static MissionTripMap Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) throw new InvalidDataException("MTG is shorter than its header.");
        int width = data[0], height = data[1];
        if (data.Length != 2 + width * height) throw new InvalidDataException($"MTG length is {data.Length}; expected {2 + width * height}.");
        return new MissionTripMap(width, height, data[2..].ToArray());
    }

    /// <summary>The trip trigger at a world cell (bits 10-15 of the alternate grid word), or 0.</summary>
    public int TriggerAt(CellCoordinate cell) =>
        (uint)cell.X < (uint)Width && (uint)cell.Z < (uint)Height ? ids[(Height - 1 - cell.Z) * Width + cell.X] & 0x3f : 0;
}
