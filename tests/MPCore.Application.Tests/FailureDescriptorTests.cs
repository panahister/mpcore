using MPCore.Application.Results;

namespace MPCore.Application.Tests;

public sealed class FailureDescriptorTests
{
    [Theory]
    [InlineData("Catalog.Customer", "CUSTOMER_NOT_FOUND")]
    [InlineData("catalog.customer", "customer-not-found")]
    [InlineData("catalog.customer", "1_CUSTOMER")]
    [InlineData("catalog.customer", "A")]
    [InlineData("catalog.customer", "AB")]
    [InlineData("catalog.customer", "CUSTOMER_")]
    public void Identity_rejects_unstable_values(string domain, string code) =>
        Assert.Throws<ArgumentException>(() => new ErrorIdentity(domain, code));

    [Fact]
    public void Identity_accepts_standard_upper_snake_reason()
    {
        var identity = new ErrorIdentity("catalog.customer", "A_B");

        Assert.Equal("A_B", identity.Code);
    }

    [Fact]
    public void Error_reason_accepts_63_characters_and_rejects_64()
    {
        var maximum = $"A{new string('B', ErrorIdentity.MaximumCodeLength - 1)}";
        var tooLong = $"A{new string('B', ErrorIdentity.MaximumCodeLength)}";

        Assert.Equal(ErrorIdentity.MaximumCodeLength, new ErrorIdentity("mpcore.test", maximum).Code.Length);
        Assert.Throws<ArgumentException>(() => new ErrorIdentity("mpcore.test", tooLong));
    }

    [Theory]
    [InlineData("items.0.name")]
    [InlineData("Items[0].name")]
    [InlineData("items[-1].name")]
    public void Validation_paths_require_the_governed_protobuf_shape(string path) =>
        Assert.Throws<ArgumentException>(() => new FieldViolation(
            path,
            "REQUIRED",
            new FailureMessageDescriptor("validation.required")));

    [Fact]
    public void Validation_detail_copies_the_input_collection()
    {
        var source = new List<FieldViolation>
        {
            new("items[0].name", "REQUIRED", new FailureMessageDescriptor("validation.required"))
        };
        var detail = new ValidationFailureDetail(source);

        source.Clear();

        Assert.Single(detail.Violations);
    }

    [Fact]
    public void Retry_requires_an_explicit_bounded_delay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryDirective(true));
        Assert.Throws<ArgumentException>(() => new RetryDirective(false, TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(2), RetryDirective.After(TimeSpan.FromSeconds(2)).RetryAfter);
    }
}
