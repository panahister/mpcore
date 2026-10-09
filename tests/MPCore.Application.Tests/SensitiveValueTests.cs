using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MPCore.Application.Sensitive;

namespace MPCore.Application.Tests;

/// <summary>
/// A one-time code or a token wrapped in <see cref="SensitiveValue"/> prints as a fixed mask wherever it is
/// turned into text, and leaves only through <see cref="SensitiveValue.Reveal"/>.
/// </summary>
public sealed class SensitiveValueTests
{
    private const string Known = "418-205-OTP";

    private sealed record VerifyCode(string Phone, SensitiveValue Code);

    [Fact]
    public void Every_way_of_turning_it_into_text_shows_the_mask()
    {
        var value = new SensitiveValue(Known);
        var builder = new StringBuilder().Append(value).Append(' ').AppendFormat(CultureInfo.InvariantCulture, "{0}", value);

        var texts = new[]
        {
            value.ToString(),
            $"interpolated {value}",
            string.Format(CultureInfo.InvariantCulture, "formatted {0:X}", value),
            string.Concat("concatenated ", value),
            builder.ToString(),
            new VerifyCode("+1-555", value).ToString(),
            new InvalidOperationException($"the code {value} was refused").Message,
            Convert.ToString(value, CultureInfo.InvariantCulture)!
        };

        foreach (var text in texts)
        {
            Assert.DoesNotContain(Known, text, StringComparison.Ordinal);
            Assert.Contains(SensitiveValue.Mask, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Json_writes_the_mask_and_reads_a_value_it_then_keeps_hidden()
    {
        var json = JsonSerializer.Serialize(new VerifyCode("+1-555", new SensitiveValue(Known)));
        var read = JsonSerializer.Deserialize<VerifyCode>($$"""{"Phone":"+1-555","Code":"{{Known}}"}""")!;

        Assert.DoesNotContain(Known, json, StringComparison.Ordinal);
        Assert.Contains("\"Code\":\"***\"", json, StringComparison.Ordinal);
        Assert.Equal(Known, read.Code.Reveal());
        Assert.DoesNotContain(Known, JsonSerializer.Serialize(read), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[\"a\"]")]
    public void Json_refuses_a_value_that_is_not_a_string_as_a_malformed_document(string code)
    {
        // A JsonException is what a serializer's caller expects of a document that does not fit the type. Any other
        // exception type is a fault of the host, and an ASP.NET Core endpoint answers it with 500.
        var failure = Record.Exception(() => JsonSerializer.Deserialize<VerifyCode>($$"""{"Phone":"+1-555","Code":{{code}}}"""));

        Assert.IsType<JsonException>(failure);
        Assert.DoesNotContain(code, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[\"a\"]")]
    public void The_converter_itself_throws_a_JsonException_and_does_not_rely_on_the_serializer_to_convert_it(string json)
    {
        // JsonSerializer turns the reader's own InvalidOperationException into a JsonException, which is why the
        // test above passes without any check. The converter is public through its attribute, and a caller that
        // reads with it directly must get the same exception type.
        var failure = Record.Exception(() => ReadWithTheConverter(json));

        Assert.IsType<JsonException>(failure);
    }

    private static void ReadWithTheConverter(string json)
    {
        var converterType = typeof(SensitiveValue).GetCustomAttribute<JsonConverterAttribute>()!.ConverterType!;
        var converter = (JsonConverter<SensitiveValue>)Activator.CreateInstance(converterType)!;
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();
        converter.Read(ref reader, typeof(SensitiveValue), JsonSerializerOptions.Default);
    }

    [Fact]
    public void Json_reads_null_as_no_value()
    {
        var read = JsonSerializer.Deserialize<VerifyCode>("""{"Phone":"+1-555","Code":null}""")!;

        Assert.Null(read.Code);
    }

    [Fact]
    public void The_debugger_shows_the_mask_and_never_the_field()
    {
        var display = typeof(SensitiveValue).GetCustomAttribute<DebuggerDisplayAttribute>();
        var fields = typeof(SensitiveValue).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(display);
        Assert.Equal(SensitiveValue.Mask, display.Value);
        Assert.All(fields, field => Assert.Equal(
            DebuggerBrowsableState.Never,
            field.GetCustomAttribute<DebuggerBrowsableAttribute>()?.State));
    }

    [Fact]
    public void The_value_leaves_only_through_reveal()
    {
        var value = new SensitiveValue(Known);
        var publicMembers = typeof(SensitiveValue)
            .GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(member => member is MethodInfo { IsSpecialName: false } or PropertyInfo)
            .Select(member => member.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Known, value.Reveal());
        Assert.DoesNotContain(publicMembers, name => name.Contains("Value", StringComparison.Ordinal) && name != nameof(SensitiveValue.Reveal));
        Assert.DoesNotContain(
            typeof(SensitiveValue).GetMethods(BindingFlags.Static | BindingFlags.Public),
            method => method.Name is "op_Implicit" or "op_Explicit" && method.ReturnType == typeof(string));
    }

    [Fact]
    public void Equality_is_by_value_and_compares_in_constant_time()
    {
        var one = new SensitiveValue(Known);
        var same = new SensitiveValue(new string(Known.ToCharArray()));
        var other = new SensitiveValue("418-205-OTQ");
        var shorter = new SensitiveValue("418");

        Assert.True(one.Equals(same));
        Assert.True(one == same);
        Assert.Equal(one.GetHashCode(), same.GetHashCode());
        Assert.False(one.Equals(other));
        Assert.True(one != other);
        Assert.False(one.Equals(shorter));
        Assert.False(one.Equals(null));
        Assert.True(one.FixedTimeEquals(Known));
        Assert.False(one.FixedTimeEquals("418-205-OTQ"));
    }

    [Fact]
    public void A_null_value_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new SensitiveValue(null!));
    }
}
