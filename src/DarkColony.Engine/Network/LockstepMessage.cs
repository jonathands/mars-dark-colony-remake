using System.Text.Json;
using System.Text.Json.Serialization;
using DarkColony.Engine.Commands;

namespace DarkColony.Engine.Network;

/// <summary>A message between lockstep peers.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LockstepTurn), "turn")]
[JsonDerivedType(typeof(LockstepDigest), "digest")]
public abstract record LockstepMessage(int Player);

/// <summary>Everything <paramref name="Player"/> orders for <paramref name="Tick"/> (possibly nothing).</summary>
public sealed record LockstepTurn(int Player, ulong Tick, IReadOnlyList<WorldCommand> Commands) : LockstepMessage(Player);

/// <summary>The sender's state digest right after it ran <paramref name="Tick"/>.</summary>
public sealed record LockstepDigest(int Player, ulong Tick, string Digest) : LockstepMessage(Player);

/// <summary>Length-free JSON lines for lockstep messages.</summary>
public static class LockstepCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Encode(LockstepMessage message) => JsonSerializer.Serialize(message, Options);

    public static LockstepMessage Decode(string line) =>
        JsonSerializer.Deserialize<LockstepMessage>(line, Options) ?? throw new InvalidDataException("Empty lockstep message.");
}
