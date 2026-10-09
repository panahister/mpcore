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
    public override SensitiveValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // A document that does not fit the type is the caller's mistake and a JsonException, which an ASP.NET Core
        // endpoint answers with 400. Any other type would be read as a fault of the host and answered with 500.
        // The message is fixed: it never carries the value.
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                try
                {
                    return new SensitiveValue(reader.GetString()!);
                }
                catch (InvalidOperationException)
                {
                    // Text that is not valid UTF-8.
                    throw new JsonException("A sensitive value must be a valid JSON string.");
                }

            default:
                throw new JsonException("A sensitive value must be a JSON string.");
        }
    }

    public override void Write(Utf8JsonWriter writer, SensitiveValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(SensitiveValue.Mask);
    }
}
