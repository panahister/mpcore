using System.ComponentModel;
using System.Globalization;

namespace MPCore.Application.Results;

/// <summary>The type of value a <see cref="MessageArgument"/> holds.</summary>
public enum MessageArgumentKind
{
    /// <summary>A calendar date, a <see cref="DateOnly"/>.</summary>
    Date = 1,

    /// <summary>A date and time with its offset from UTC, a <see cref="DateTimeOffset"/>.</summary>
    DateAndTime = 2,

    /// <summary>A whole number, a <see cref="long"/>.</summary>
    Integer = 3,

    /// <summary>A decimal number, a <see cref="decimal"/>.</summary>
    Decimal = 4,
}

/// <summary>
/// A typed value of a message argument: a date, a date and time with its offset, an integer or a decimal.
/// The application layer builds a message before the caller's language is known, so it passes the value,
/// not its text; the edge renders it in the negotiated culture.
/// </summary>
/// <remarks>
/// The value is held in an invariant form: <see cref="InvariantValue"/> is ISO 8601 for a date
/// (<c>2026-10-12</c>) and for a date and time (<c>2026-10-12T09:30:00+04:00</c>), and invariant digits for a
/// number (<c>-1234</c>, <c>1234.50</c>), the same in every culture. It is what a message shows when no
/// product formatter formats it. A date and time without an offset is refused: a <see cref="DateTime"/> does
/// not say which instant it names.
/// </remarks>
public sealed record MessageArgument
{
    private MessageArgument(MessageArgumentKind kind, object value, string invariantValue)
    {
        Kind = kind;
        Value = value;
        InvariantValue = invariantValue;
    }

    /// <summary>Gets the type of the value.</summary>
    public MessageArgumentKind Kind { get; }

    /// <summary>
    /// Gets the value: a <see cref="DateOnly"/>, a <see cref="DateTimeOffset"/>, a <see cref="long"/> or a
    /// <see cref="decimal"/>, as <see cref="Kind"/> says.
    /// </summary>
    public object Value { get; }

    /// <summary>Gets the invariant form of the value: ISO 8601 for dates and times, invariant digits for numbers.</summary>
    public string InvariantValue { get; }

    /// <summary>A calendar date.</summary>
    /// <param name="value">The date.</param>
    public static MessageArgument Date(DateOnly value) =>
        new(MessageArgumentKind.Date, value, value.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture));

    /// <summary>A date and time with its offset from UTC.</summary>
    /// <param name="value">The date and time.</param>
    public static MessageArgument DateAndTime(DateTimeOffset value) =>
        new(MessageArgumentKind.DateAndTime, value, value.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFzzz", CultureInfo.InvariantCulture));

    /// <summary>
    /// Refused: a <see cref="DateTime"/> has no offset, and its implicit conversion to a
    /// <see cref="DateTimeOffset"/> would take the offset of the machine. Pass a <see cref="DateTimeOffset"/>,
    /// or a <see cref="DateOnly"/> for a date.
    /// </summary>
    /// <param name="value">The date and time without an offset.</param>
    /// <exception cref="ArgumentException">Always.</exception>
    [Obsolete(DateTimeRefusal, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static MessageArgument DateAndTime(DateTime value) =>
        throw new ArgumentException(DateTimeRefusal, nameof(value));

    /// <summary>A whole number.</summary>
    /// <param name="value">The number.</param>
    public static MessageArgument Integer(long value) =>
        new(MessageArgumentKind.Integer, value, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>A decimal number. Its scale is kept: <c>12.50</c> stays <c>12.50</c>.</summary>
    /// <param name="value">The number.</param>
    public static MessageArgument Decimal(decimal value) =>
        new(MessageArgumentKind.Decimal, value, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Returns <see cref="InvariantValue"/>.</summary>
    public override string ToString() => InvariantValue;

    private const string DateTimeRefusal =
        "A date and time without an offset is refused as a message argument: pass a DateTimeOffset, or a DateOnly for a date.";
}
