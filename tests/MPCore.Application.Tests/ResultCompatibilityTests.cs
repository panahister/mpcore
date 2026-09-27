using MPCore.Application.Results;

namespace MPCore.Application.Tests;

public sealed class ResultCompatibilityTests
{
    [Fact]
    public void Legacy_error_and_result_surface_remains_usable()
    {
        var error = new Error("LEGACY_CODE", "Legacy description");
        var (code, description) = error;
        var result = Result.Failure(error);

        Assert.Equal("LEGACY_CODE", code);
        Assert.Equal("Legacy description", description);
        Assert.Same(error, result.Error);
        Assert.True(result.IsFailure);
        Assert.Equal("LEGACY_CODE", result.FailureDescriptor?.Identity.Code);
    }

    [Fact]
    public void New_failure_factory_preserves_legacy_error_property_without_copying_display_text()
    {
        var failure = CreateFailure();

        var result = Result.FromFailure(failure);

        Assert.Same(failure, result.FailureDescriptor);
        Assert.Equal("CUSTOMER_NOT_FOUND", result.Error.Code);
        Assert.Empty(result.Error.Description);
    }

    [Fact]
    public void Value_or_throw_raises_a_transport_neutral_failure_exception()
    {
        var result = Result<string>.FromFailure(CreateFailure());

        var exception = Assert.Throws<ResultFailureException>(() => result.ValueOrThrow());

        Assert.Equal("catalog.customer", exception.Failure.Identity.Domain);
        Assert.DoesNotContain("Legacy description", exception.Message, StringComparison.Ordinal);
    }

    private static FailureDescriptor CreateFailure() => new(
        new ErrorIdentity("catalog.customer", "CUSTOMER_NOT_FOUND"),
        ErrorCategory.NotFound,
        new FailureMessageDescriptor("catalog.customer_not_found"));
}
