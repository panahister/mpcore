using System.Globalization;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Results;
using MPCore.Localization;
using MPCore.Validation.FluentValidation;

namespace MPCore.Validation.Tests;

public sealed record AddressInput(string? PostalCode, string? Phone);

public sealed record LineInput(string? Sku, int Quantity);

public sealed record PlaceOrderInput(AddressInput? ShippingAddress, IReadOnlyList<LineInput> Lines, string? Note, string? Secret);

public sealed class PlaceOrderInputValidator : AbstractValidator<PlaceOrderInput>
{
    public PlaceOrderInputValidator()
    {
        RuleFor(x => x.ShippingAddress).NotNull();
        RuleFor(x => x.ShippingAddress!.PostalCode).NotEmpty().Length(10).When(x => x.ShippingAddress is not null);
        RuleFor(x => x.ShippingAddress!.Phone)
            .Matches("^09[0-9]{9}$").WithErrorCode("PHONE_FORMAT").WithMessage("ordering.phone_format")
            .When(x => x.ShippingAddress is not null);
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.Sku).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, 10);
        });
        RuleFor(x => x.Note).MaximumLength(5);
        RuleFor(x => x.Secret).MaximumLength(3);
    }
}

public sealed class ValidationFailureConversionTests
{
    private static IReadOnlyList<FieldViolation> Violations(PlaceOrderInput input)
    {
        var result = new PlaceOrderInputValidator().Validate(input);
        var failure = ValidationFailures.ToFailure(result.Errors);
        Assert.Equal("mpcore.validation", failure.Identity.Domain);
        Assert.Equal("VALIDATION_FAILED", failure.Identity.Code);
        Assert.Equal(ErrorCategory.Validation, failure.Category);
        return Assert.IsType<ValidationFailureDetail>(Assert.Single(failure.Details)).Violations;
    }

    [Fact]
    public void Paths_codes_keys_and_limits_follow_MP_Core_conventions()
    {
        var violations = Violations(new PlaceOrderInput(
            new AddressInput("123", "0912"), [new LineInput("", 0)], "too long note", "abcd"));

        var postal = Assert.Single(violations, v => v.FieldPath == "shipping_address.postal_code");
        Assert.Equal("EXACT_LENGTH", postal.RuleCode);
        Assert.Equal("validation.exact_length", postal.Message.Key);
        Assert.Equal("10", postal.Message.Arguments["max_length"]);

        var phone = Assert.Single(violations, v => v.FieldPath == "shipping_address.phone");
        Assert.Equal("PHONE_FORMAT", phone.RuleCode);
        Assert.Equal("ordering.phone_format", phone.Message.Key);

        var sku = Assert.Single(violations, v => v.FieldPath == "lines[0].sku");
        Assert.Equal("NOT_EMPTY", sku.RuleCode);
        Assert.Equal("validation.not_empty", sku.Message.Key);

        var quantity = Assert.Single(violations, v => v.FieldPath == "lines[0].quantity");
        Assert.Equal("INCLUSIVE_BETWEEN", quantity.RuleCode);
        Assert.Equal(("1", "10"), (quantity.Message.Arguments["from"], quantity.Message.Arguments["to"]));

        var note = Assert.Single(violations, v => v.FieldPath == "note");
        Assert.Equal("MAXIMUM_LENGTH", note.RuleCode);
        Assert.Equal("5", note.Message.Arguments["max_length"]);
        Assert.Equal("note", note.Message.Arguments["field"]);
    }

    [Fact]
    public void The_value_the_caller_sent_is_never_carried()
    {
        var violations = Violations(new PlaceOrderInput(null, [], null, "top-secret-value"));

        var secret = Assert.Single(violations, v => v.FieldPath == "secret");
        Assert.DoesNotContain(secret.Message.Arguments.Values, value => value.Contains("top-secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ShippingAddress.PostalCode", "shipping_address.postal_code")]
    [InlineData("Lines[12].Sku", "lines[12].sku")]
    [InlineData("SKU", "sku")]
    [InlineData("HTTPRequestId", "http_request_id")]
    [InlineData("Line1", "line1")]
    [InlineData("", "request")]
    [InlineData("Weird Name", "request")]
    public void Property_paths_become_snake_case_field_paths(string propertyName, string expected) =>
        Assert.Equal(expected, ValidationFailures.ToFieldPath(propertyName));

    [Fact]
    public void At_most_32_violations_are_carried()
    {
        var failures = Enumerable.Range(0, 40)
            .Select(i => new global::FluentValidation.Results.ValidationFailure($"Field{i}", "x") { ErrorCode = "NotEmptyValidator" })
            .ToList();

        var detail = Assert.IsType<ValidationFailureDetail>(Assert.Single(ValidationFailures.ToFailure(failures).Details));

        Assert.Equal(ValidationFailures.MaximumViolations, detail.Violations.Count);
    }

    public enum Color
    {
        Red = 1,
    }

    public sealed record Everything(
        string? NotNullText, string? NotEmptyText, string? NullText, string? EmptyText, string? LengthText, string? MinText,
        string? MaxText, string? ExactText, int Less, int LessOrEqual, int Greater, int GreaterOrEqual, int EqualTo,
        int NotEqualTo, int Between, int ExclusiveBetween, string? Pattern, string? Email, string? Card, Color Enum,
        string? EnumName, decimal Amount, string? Predicate);

    public sealed class EverythingValidator : AbstractValidator<Everything>
    {
        public EverythingValidator()
        {
            RuleFor(x => x.NotNullText).NotNull();
            RuleFor(x => x.NotEmptyText).NotEmpty();
            RuleFor(x => x.NullText).Null();
            RuleFor(x => x.EmptyText).Empty();
            RuleFor(x => x.LengthText).Length(2, 3);
            RuleFor(x => x.MinText).MinimumLength(3);
            RuleFor(x => x.MaxText).MaximumLength(1);
            RuleFor(x => x.ExactText).Length(4);
            RuleFor(x => x.Less).LessThan(0);
            RuleFor(x => x.LessOrEqual).LessThanOrEqualTo(0);
            RuleFor(x => x.Greater).GreaterThan(10);
            RuleFor(x => x.GreaterOrEqual).GreaterThanOrEqualTo(10);
            RuleFor(x => x.EqualTo).Equal(7);
            RuleFor(x => x.NotEqualTo).NotEqual(1);
            RuleFor(x => x.Between).InclusiveBetween(5, 6);
            RuleFor(x => x.ExclusiveBetween).ExclusiveBetween(5, 6);
            RuleFor(x => x.Pattern).Matches("^[0-9]+$");
            RuleFor(x => x.Email).EmailAddress();
            RuleFor(x => x.Card).CreditCard();
            RuleFor(x => x.Enum).IsInEnum();
            RuleFor(x => x.EnumName).IsEnumName(typeof(Color));
            RuleFor(x => x.Amount).PrecisionScale(4, 2, ignoreTrailingZeros: true);
            RuleFor(x => x.Predicate).Must(static _ => false);
        }
    }

    [Fact]
    public void Every_built_in_validator_has_a_default_text_in_English_and_Persian()
    {
        var input = new Everything(
            null, "", "x", "x", "abcd", "a", "ab", "abc", 1, 1, 1, 1, 1, 1, 1, 5, "abc", "not-an-email", "1234",
            (Color)9, "Blue", 123.456m, "x");
        var failures = new EverythingValidator().Validate(input).Errors;
        var keys = failures.Select(ValidationFailures.ToViolation).Select(static v => v.Message.Key).ToHashSet();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMPCoreMessageCatalog();
        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IMessageCatalog>();

        Assert.Equal(23, failures.Count);
        foreach (var key in keys)
        {
            Assert.True(catalog.IsKnownKey(key), $"no default text for {key}");
            Assert.NotNull(catalog.Render(key, null, CultureInfo.GetCultureInfo("fa")));
            Assert.NotEqual(
                catalog.Render(key, null, CultureInfo.GetCultureInfo("en")),
                catalog.Render(key, null, CultureInfo.GetCultureInfo("fa")));
        }
    }
}
