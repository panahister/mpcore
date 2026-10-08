using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MPCore.Application.Sensitive;

/// <summary>
/// A value that must never print: a one-time code, a token, a secret a command carries. Every way of turning
/// it into text shows <see cref="Mask"/>: <see cref="ToString"/>, string interpolation and formatting, a
/// record's printed members, an exception message, System.Text.Json, and the debugger. The value leaves only
/// through <see cref="Reveal"/>, a single method to search for in a review.
/// </summary>
/// <remarks>
/// Equality compares in constant time (<see cref="CryptographicOperations.FixedTimeEquals"/>), so comparing a
/// submitted code with a stored one does not disclose how much of it matched. Values of different lengths are
/// unequal without their content being compared. JSON reads a string into the value, so a request can carry
/// one, and writes the mask.
/// </remarks>
[DebuggerDisplay(Mask)]
[JsonConverter(typeof(SensitiveValueJsonConverter))]
public sealed class SensitiveValue : IEquatable<SensitiveValue>
{
    /// <summary>What every rendering of the value shows.</summary>
    public const string Mask = "***";

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string _value;

    /// <summary>Wraps a value.</summary>
    /// <param name="value">The value; never null.</param>
    public SensitiveValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    /// <summary>
    /// Returns the value itself. The only way out: use it where the value is consumed, such as sending a
    /// code or presenting a token, and never to log, trace or describe it.
    /// </summary>
    public string Reveal() => _value;

    /// <summary>Compares the value with a plain candidate in constant time.</summary>
    /// <param name="candidate">The candidate, for example a code a user submitted.</param>
    public bool FixedTimeEquals(string? candidate) => candidate is not null && Same(_value, candidate);

    /// <inheritdoc />
    public bool Equals(SensitiveValue? other) => other is not null && Same(_value, other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SensitiveValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode(StringComparison.Ordinal);

    /// <summary>Returns <see cref="Mask"/>, never the value.</summary>
    public override string ToString() => Mask;

    /// <summary>Compares two values in constant time.</summary>
    public static bool operator ==(SensitiveValue? left, SensitiveValue? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Compares two values in constant time.</summary>
    public static bool operator !=(SensitiveValue? left, SensitiveValue? right) => !(left == right);

    private static bool Same(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(left.AsSpan()), MemoryMarshal.AsBytes(right.AsSpan()));
}

/// <summary>Reads a JSON string into a <see cref="SensitiveValue"/>, and writes the mask.</summary>
internal sealed class SensitiveValueJsonConverter : JsonConverter<SensitiveValue>
{
    public override SensitiveValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : new SensitiveValue(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, SensitiveValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(SensitiveValue.Mask);
    }
}

/// <summary>
/// Types whose objects must never be logged whole, such as the request and response messages of a gRPC service
/// that carries codes or tokens. MP Core's log and trace processors mask an attribute or tag that holds one.
/// </summary>
/// <remarks>
/// Google.Protobuf prints every field of a message, even one marked <c>debug_redact</c>, so a message cannot
/// keep its own secrets out of a log. A type is added by the gRPC interceptor for a named service
/// (<c>AddMPCoreSensitiveMessages</c>), or by the host for any other type. A type is never removed.
/// </remarks>
public static class SensitiveMessageTypes
{
    private static readonly ConcurrentDictionary<Type, byte> Types = new();

    /// <summary>Gets a value indicating whether no type was added; the processors skip the lookup then.</summary>
    public static bool IsEmpty => Types.IsEmpty;

    /// <summary>Adds a type whose objects are masked whole wherever MP Core's processors see them.</summary>
    /// <param name="type">The type.</param>
    public static void Add(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Types.TryAdd(type, 0);
    }

    /// <summary>Determines whether objects of a type are masked whole.</summary>
    /// <param name="type">The type.</param>
    public static bool Contains(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return !Types.IsEmpty && Types.ContainsKey(type);
    }
}
