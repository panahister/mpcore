using System.Net;
using System.Text.Json;
using MPCore.Application.Results;

namespace MPCore.Transport.Http.Tests;

public sealed class ProblemDetailsContractTests
{
    [Theory]
    [InlineData(ErrorCategory.Validation, 400)]
    [InlineData(ErrorCategory.Unauthenticated, 401)]
    [InlineData(ErrorCategory.Forbidden, 403)]
    [InlineData(ErrorCategory.NotFound, 404)]
    [InlineData(ErrorCategory.AlreadyExists, 409)]
    [InlineData(ErrorCategory.Conflict, 409)]
    [InlineData(ErrorCategory.Concurrency, 409)]
    [InlineData(ErrorCategory.BusinessRule, 422)]
    [InlineData(ErrorCategory.Precondition, 422)]
    [InlineData(ErrorCategory.RateLimit, 429)]
    [InlineData(ErrorCategory.Quota, 429)]
    [InlineData(ErrorCategory.DependencyUnavailable, 503)]
    [InlineData(ErrorCategory.Deadline, 504)]
    [InlineData(ErrorCategory.Cancelled, 503)]
    [InlineData(ErrorCategory.Unknown, 500)]
    public async Task Every_error_category_maps_to_the_documented_status(
        ErrorCategory category,
        int expectedStatus)
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync();

        var thrown = await fixture.Client.GetAsync(new Uri($"/fail?category={category}", UriKind.Relative));
        var returned = await fixture.Client.GetAsync(new Uri($"/fail-result?category={category}", UriKind.Relative));
        var document = JsonDocument.Parse(await thrown.Content.ReadAsStringAsync());

        Assert.Equal(expectedStatus, (int)thrown.StatusCode);
        Assert.Equal(expectedStatus, (int)returned.StatusCode);
        Assert.Equal("application/problem+json", thrown.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedStatus, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(category.ToString(), document.RootElement.GetProperty("category").GetString());
    }

    [Fact]
    public async Task Concurrency_uses_412_only_for_a_conditional_request_and_echoes_the_entity_tag()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync();

        var plain = await fixture.Client.GetAsync(
            new Uri("/fail?category=Concurrency", UriKind.Relative));

        using var ifMatch = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/fail?category=Concurrency", UriKind.Relative));
        ifMatch.Headers.TryAddWithoutValidation("If-Match", "\"v7\"");
        var conditional = await fixture.Client.SendAsync(ifMatch);

        using var ifUnmodified = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/fail?category=Concurrency", UriKind.Relative));
        ifUnmodified.Headers.TryAddWithoutValidation("If-Unmodified-Since", "Tue, 01 Sep 2026 00:00:00 GMT");
        var timeConditional = await fixture.Client.SendAsync(ifUnmodified);

        Assert.Equal(HttpStatusCode.Conflict, plain.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, conditional.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, timeConditional.StatusCode);
        Assert.Equal("\"v7\"", conditional.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task The_problem_document_carries_the_documented_member_contract()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/fail?category=NotFound&secret=search-term", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("x-request-id", "request-12345678");
        var response = await fixture.Client.SendAsync(request);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("urn:mpcore:error:catalog.product:PRODUCT_NOT_FOUND", root.GetProperty("type").GetString());
        Assert.Equal("Requested resource was not found.", root.GetProperty("title").GetString());
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.Equal("/fail", root.GetProperty("instance").GetString());
        Assert.Equal("catalog.product", root.GetProperty("errorDomain").GetString());
        Assert.Equal("PRODUCT_NOT_FOUND", root.GetProperty("errorCode").GetString());
        Assert.Equal("NotFound", root.GetProperty("category").GetString());
        Assert.Equal("request-12345678", root.GetProperty("requestId").GetString());
        Assert.Equal("request-12345678", response.Headers.GetValues("x-request-id").Single());

        // detail is absent by default because the null localizer resolves nothing.
        Assert.False(root.TryGetProperty("detail", out _));

        // instance is the path only; the query string can carry identifiers and search terms.
        Assert.DoesNotContain("search-term", root.ToString(), StringComparison.Ordinal);

        var resource = root.GetProperty("resource");
        Assert.Equal("product", resource.GetProperty("type").GetString());
        Assert.Equal("products/2f1c", resource.GetProperty("name").GetString());
        Assert.Equal("tenants/42", resource.GetProperty("owner").GetString());
    }

    [Fact]
    public async Task Typed_details_preserve_every_machine_readable_rule_code()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync();

        var validation = await ReadAsync(fixture, "Validation");
        var precondition = await ReadAsync(fixture, "Precondition");
        var quota = await ReadAsync(fixture, "Quota");

        var violation = validation.GetProperty("violations").EnumerateArray().Single();
        Assert.Equal("customer.display_name", violation.GetProperty("field").GetString());
        Assert.Equal("REQUIRED", violation.GetProperty("rule").GetString());

        var precondViolation = precondition.GetProperty("preconditions").EnumerateArray().Single();
        Assert.Equal("customer", precondViolation.GetProperty("type").GetString());
        Assert.Equal("customers/42", precondViolation.GetProperty("subject").GetString());
        Assert.Equal("VERSION_MISMATCH", precondViolation.GetProperty("rule").GetString());

        var quotaViolation = quota.GetProperty("quota").EnumerateArray().Single();
        Assert.Equal("tenants/42", quotaViolation.GetProperty("subject").GetString());
        Assert.Equal("REQUEST_LIMIT", quotaViolation.GetProperty("rule").GetString());
    }

    [Fact]
    public async Task Culture_negotiation_and_localized_detail_mirror_the_grpc_adapter()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync(localize: true);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/fail?category=Validation", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("accept-language", "fa-IR, en;q=0.5");
        var response = await fixture.Client.SendAsync(request);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("ورودی نامعتبر است.", root.GetProperty("detail").GetString());
        Assert.Equal(
            "این مقدار الزامی است.",
            root.GetProperty("violations").EnumerateArray().Single().GetProperty("message").GetString());
        Assert.Equal("Request validation failed.", root.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Localized_detail_is_truncated_to_512_characters()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync(localize: true);

        var root = await ReadAsync(fixture, "Validation");

        Assert.Equal(512, root.GetProperty("detail").GetString()!.Length);
    }

    [Fact]
    public async Task An_unusable_request_id_is_replaced_by_a_generated_identifier()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/fail?category=Conflict", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("x-request-id", "bad id!");
        var response = await fixture.Client.SendAsync(request);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var requestId = root.GetProperty("requestId").GetString()!;
        Assert.Equal(32, requestId.Length);
        Assert.NotEqual("bad id!", requestId);
        Assert.All(requestId, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
    }

    [Fact]
    public async Task Retry_after_requires_both_a_retry_directive_and_policy_approval()
    {
        await using var denied = await ProblemDetailsFixture.CreateAsync();
        await using var allowed = await ProblemDetailsFixture.CreateAsync(allowRetry: true);

        var deniedResponse = await denied.Client.GetAsync(new Uri("/fail?category=RateLimit", UriKind.Relative));
        var allowedResponse = await allowed.Client.GetAsync(new Uri("/fail?category=RateLimit", UriKind.Relative));
        var allowedRoot = JsonDocument.Parse(await allowedResponse.Content.ReadAsStringAsync()).RootElement;
        var deniedRoot = JsonDocument.Parse(await deniedResponse.Content.ReadAsStringAsync()).RootElement;

        Assert.Null(deniedResponse.Headers.RetryAfter);
        Assert.False(deniedRoot.TryGetProperty("retryAfterSeconds", out _));
        Assert.Equal(TimeSpan.FromSeconds(3), allowedResponse.Headers.RetryAfter?.Delta);
        Assert.Equal(3, allowedRoot.GetProperty("retryAfterSeconds").GetInt32());
    }

    [Fact]
    public async Task Enrichers_extend_the_document_but_never_overwrite_the_base_contract()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync(enrich: true);

        var root = await ReadAsync(fixture, "Conflict");

        Assert.Equal("tenants/42", root.GetProperty("tenant").GetString());
        Assert.Equal(409, root.GetProperty("status").GetInt32());
        Assert.Equal("CUSTOMER_INVALID", root.GetProperty("errorCode").GetString());
    }

    [Fact]
    public void A_reserved_member_throws_in_development_and_is_dropped_elsewhere()
    {
        var failure = FailureCatalog.For(ErrorCategory.Conflict);
        var development = new ProblemDetailsEnrichmentContext(new DefaultHttpContext(), failure, 409, true);
        var production = new ProblemDetailsEnrichmentContext(new DefaultHttpContext(), failure, 409, false);

        Assert.Throws<InvalidOperationException>(() => development.TryAdd("status", 200));
        Assert.False(production.TryAdd("errorCode", "SPOOFED"));
        Assert.True(production.TryAdd("tenant", "tenants/42"));
        Assert.Contains("errorCode", production.RejectedMembers);
        Assert.DoesNotContain("errorCode", production.Extensions.Keys);
    }

    [Fact]
    public void Every_base_contract_member_is_reserved()
    {
        Assert.Equal(
            new[]
            {
                "category", "detail", "errorCode", "errorDomain", "instance", "preconditions", "quota",
                "requestId", "resource", "retryAfterSeconds", "status", "title", "traceId", "type",
                "violations"
            },
            ProblemDetailsEnrichmentContext.ReservedMembers.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Documents_stay_within_the_cap_and_drop_in_the_documented_order()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync(
            options => options.ProblemDocumentByteLimit = HttpFailureOptions.MinimumProblemDocumentBytes,
            localize: true,
            enrich: true);

        var response = await fixture.Client.GetAsync(new Uri("/fail-oversized", UriKind.Relative));
        var payload = await response.Content.ReadAsByteArrayAsync();
        var root = JsonDocument.Parse(payload).RootElement;

        Assert.True(
            payload.Length <= HttpFailureOptions.MinimumProblemDocumentBytes,
            $"Problem document was {payload.Length} bytes.");

        // Enricher extensions, then localized text, then typed detail elements.
        Assert.False(root.TryGetProperty("tenant", out _));
        Assert.False(root.TryGetProperty("detail", out _));
        Assert.False(root.TryGetProperty("violations", out _));

        // Identity and correlation are never dropped.
        Assert.Equal("catalog.customer", root.GetProperty("errorDomain").GetString());
        Assert.Equal("CUSTOMER_INVALID", root.GetProperty("errorCode").GetString());
        Assert.Equal("Validation", root.GetProperty("category").GetString());
        Assert.Equal(32, root.GetProperty("requestId").GetString()!.Length);
        Assert.Equal("urn:mpcore:error:catalog.customer:CUSTOMER_INVALID", root.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_full_size_document_keeps_its_typed_details()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync();

        var response = await fixture.Client.GetAsync(new Uri("/fail-oversized", UriKind.Relative));
        var payload = await response.Content.ReadAsByteArrayAsync();
        var root = JsonDocument.Parse(payload).RootElement;

        Assert.True(payload.Length <= HttpFailureOptions.MaximumProblemDocumentBytes);
        Assert.Equal(32, root.GetProperty("violations").GetArrayLength());
    }

    [Fact]
    public async Task Exception_text_credentials_and_stack_traces_never_reach_the_client()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync();

        var response = await fixture.Client.GetAsync(new Uri("/boom", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Internal service error.", root.GetProperty("title").GetString());
        Assert.Equal("mpcore.http", root.GetProperty("errorDomain").GetString());
        Assert.Equal("UNEXPECTED_FAILURE", root.GetProperty("errorCode").GetString());
        Assert.False(root.TryGetProperty("detail", out _));
        Assert.DoesNotContain("database-password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("internal-db", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_failure_carries_no_detail_and_no_typed_extension()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync(localize: true, enrich: true);

        var root = await ReadAsync(fixture, "Unknown");

        Assert.False(root.TryGetProperty("detail", out _));
        Assert.False(root.TryGetProperty("tenant", out _));
        Assert.False(root.TryGetProperty("violations", out _));
        Assert.Equal("CUSTOMER_INVALID", root.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Successful_responses_carry_the_representation_with_no_envelope()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync();

        var ok = await fixture.Client.GetAsync(new Uri("/ok", UriKind.Relative));
        var okBody = await ok.Content.ReadAsStringAsync();
        var unit = await fixture.Client.GetAsync(new Uri("/unit", UriKind.Relative));
        var value = await fixture.Client.GetAsync(new Uri("/value", UriKind.Relative));
        var valueBody = await value.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("""{"id":"products/2f1c","name":"Widget"}""", okBody);
        Assert.DoesNotContain("success", okBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"data\"", okBody, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, unit.StatusCode);
        Assert.Equal("""{"id":"products/2f1c"}""", valueBody);
    }

    [Fact]
    public async Task The_request_identifier_is_echoed_on_a_successful_response()
    {
        await using var fixture = await ProblemDetailsFixture.CreateAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/ok", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("x-request-id", "request-12345678");
        var response = await fixture.Client.SendAsync(request);

        Assert.Equal("request-12345678", response.Headers.GetValues("x-request-id").Single());
    }

    private static async Task<JsonElement> ReadAsync(ProblemDetailsFixture fixture, string category)
    {
        var response = await fixture.Client.GetAsync(new Uri($"/fail?category={category}", UriKind.Relative));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}
