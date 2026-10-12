using System.Globalization;
using System.Reflection;
using MPCore.Application.Results;

namespace MPCore.Application.Tests;

/// <summary>
/// A message argument may carry a typed value, held in an invariant form, beside the string arguments; the
/// descriptor's limits hold for both together.
/// </summary>
public sealed class MessageArgumentTests
{
    private static readonly DateOnly Day = new(2026, 10, 12);
    private static readonly DateTimeOffset Instant = new(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(4));

    [Fact]
    public void Each_kind_holds_its_value_and_an_invariant_form_whatever_the_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        // en-DE stands for any culture whose own forms differ: a comma before the decimals, the day first.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-DE");
        try
        {
            Assert.NotEqual("1234.50", 1234.50m.ToString(CultureInfo.CurrentCulture));

            var date = MessageArgument.Date(Day);
            var dateAndTime = MessageArgument.DateAndTime(Instant);
            var fraction = MessageArgument.DateAndTime(new DateTimeOffset(2026, 10, 12, 9, 30, 0, 250, TimeSpan.Zero));
            var integer = MessageArgument.Integer(-1234567);
            var number = MessageArgument.Decimal(1234.50m);

            Assert.Equal((MessageArgumentKind.Date, Day, "2026-10-12"), (date.Kind, (DateOnly)date.Value, date.InvariantValue));
            Assert.Equal((MessageArgumentKind.DateAndTime, Instant, "2026-10-12T09:30:00+04:00"), (dateAndTime.Kind, (DateTimeOffset)dateAndTime.Value, dateAndTime.InvariantValue));
            Assert.Equal("2026-10-12T09:30:00.25+00:00", fraction.InvariantValue);
            Assert.Equal((MessageArgumentKind.Integer, -1234567L, "-1234567"), (integer.Kind, (long)integer.Value, integer.InvariantValue));
            Assert.Equal((MessageArgumentKind.Decimal, 1234.50m, "1234.50"), (number.Kind, (decimal)number.Value, number.InvariantValue));
            Assert.Equal("2026-10-12", date.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_date_and_time_without_an_offset_is_refused()
    {
        // The overload exists only to stop DateTime's implicit conversion to DateTimeOffset, which would take
        // the machine's offset: the compiler refuses a call to it, and a call made past the compiler throws.
        var refusal = typeof(MessageArgument).GetMethod(nameof(MessageArgument.DateAndTime), [typeof(DateTime)]);

        Assert.NotNull(refusal);
        Assert.True(refusal.GetCustomAttribute<ObsoleteAttribute>()?.IsError);
        foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Local, DateTimeKind.Utc })
        {
            var thrown = Assert.Throws<TargetInvocationException>(() => refusal.Invoke(null, [new DateTime(2026, 10, 12, 9, 30, 0, kind)]));
            Assert.IsType<ArgumentException>(thrown.InnerException);
        }
    }

    [Fact]
    public void A_descriptor_keeps_typed_arguments_beside_string_arguments()
    {
        var message = new FailureMessageDescriptor(
            "orders.ship_by",
            new Dictionary<string, string> { ["order"] = "A-17" },
            new Dictionary<string, MessageArgument> { ["date"] = MessageArgument.Date(Day), ["count"] = MessageArgument.Integer(3) });

        Assert.Equal("A-17", Assert.Single(message.Arguments).Value);
        Assert.Equal(["count", "date"], message.TypedArguments.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("2026-10-12", message.TypedArguments["date"].InvariantValue);
        Assert.Empty(new FailureMessageDescriptor("orders.ship_by", message.Arguments).TypedArguments);
        Assert.Empty(new FailureMessageDescriptor("orders.ship_by").TypedArguments);
    }

    [Fact]
    public void The_descriptors_limits_hold_for_typed_arguments_too()
    {
        var sixteenStrings = Enumerable.Range(0, FailureMessageDescriptor.MaximumArgumentCount)
            .ToDictionary(static index => $"text_{index}", static _ => "x");
        var fifteenStrings = sixteenStrings.Take(FailureMessageDescriptor.MaximumArgumentCount - 1).ToDictionary();
        var one = new Dictionary<string, MessageArgument> { ["count"] = MessageArgument.Integer(1) };

        // The count is of both kinds together.
        Assert.Equal(FailureMessageDescriptor.MaximumArgumentCount, new FailureMessageDescriptor("orders.ship_by", fifteenStrings, one).Arguments.Count + 1);
        Assert.Throws<ArgumentException>("typedArguments", () => new FailureMessageDescriptor("orders.ship_by", sixteenStrings, one));
        Assert.Throws<ArgumentException>("typedArguments", () => new FailureMessageDescriptor(
            "orders.ship_by",
            null,
            Enumerable.Range(0, FailureMessageDescriptor.MaximumArgumentCount + 1).ToDictionary(static index => $"number_{index}", static index => MessageArgument.Integer(index))));

        // The names follow the rule of string arguments, never repeat one, and every argument has a value.
        Assert.Throws<ArgumentException>("typedArguments", () => new FailureMessageDescriptor("orders.ship_by", null, new Dictionary<string, MessageArgument> { ["Count"] = MessageArgument.Integer(1) }));
        Assert.Throws<ArgumentException>("typedArguments", () => new FailureMessageDescriptor("orders.ship_by", new Dictionary<string, string> { ["count"] = "1" }, one));
        Assert.Throws<ArgumentException>("typedArguments", () => new FailureMessageDescriptor("orders.ship_by", null, new Dictionary<string, MessageArgument> { ["count"] = null! }));

        // The string arguments' own limits are unchanged.
        Assert.Throws<ArgumentException>("arguments", () => new FailureMessageDescriptor(
            "orders.ship_by", new Dictionary<string, string> { ["note"] = new string('x', FailureMessageDescriptor.MaximumArgumentValueLength + 1) }, one));

        // Every invariant form is far inside the value limit, the largest decimal and the widest instant included.
        Assert.All(
            new[] { MessageArgument.Decimal(decimal.MinValue), MessageArgument.Integer(long.MinValue), MessageArgument.DateAndTime(DateTimeOffset.MaxValue.ToOffset(TimeSpan.FromHours(-14))) },
            argument => Assert.InRange(argument.InvariantValue.Length, 1, FailureMessageDescriptor.MaximumArgumentValueLength));
    }
}
