using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MPCore.Application.Results;

namespace MPCore.Transport.Grpc.Tests;

public sealed class GrpcFailureWireTests
{
    [Theory]
    [InlineData("LEGACY_FAILURE")]
    [InlineData("BUSINESS_RULE_VIOLATION")]
    [InlineData("DEADLINE_EXCEEDED")]
    [InlineData("REQUEST_CANCELLED")]
    [InlineData("RPC_RESOURCE_EXHAUSTED")]
    [InlineData("UNEXPECTED_FAILURE")]
    public void Built_in_error_reasons_fit_the_google_rpc_contract(string code)
    {
        Assert.True(ErrorIdentity.IsValidCode(code));
        Assert.InRange(code.Length, 1, ErrorIdentity.MaximumCodeLength);
    }

    [Fact]
    public async Task Unary_failure_has_native_status_allowlisted_details_and_request_identity()
    {
        await using var fixture = await GrpcFixture.CreateAsync();
        var headers = new Metadata
        {
            { "x-request-id", "request-12345678" },
            { "accept-language", "fa-IR, en;q=0.5" }
        };

        using var call = fixture.Client.FailAsync(new FailureRequest(), headers);
        var responseHeaders = await call.ResponseHeadersAsync;
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await call.ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.DoesNotContain("database", exception.Status.Detail, StringComparison.OrdinalIgnoreCase);
        var status = exception.GetRpcStatus();
        Assert.NotNull(status);
        var errorInfo = status.GetDetail<ErrorInfo>();
        Assert.Equal("catalog.customer", errorInfo?.Domain);
        Assert.Equal("CUSTOMER_INVALID", errorInfo?.Reason);
        var fieldViolation = Assert.Single(status.GetDetail<BadRequest>()!.FieldViolations);
        Assert.Equal("REQUIRED", fieldViolation.Reason);
        Assert.Equal("request-12345678", status.GetDetail<RequestInfo>()?.RequestId);
        Assert.Equal("request-12345678", responseHeaders.GetValue("x-request-id"));
        Assert.Equal("fa", status.GetDetail<LocalizedMessage>()?.Locale);
        Assert.Equal("ورودی نامعتبر است.", status.GetDetail<LocalizedMessage>()?.Message);
    }

    [Fact]
    public async Task A_broken_business_rule_keeps_its_identity_and_localized_message_on_the_wire()
    {
        await using var fixture = await GrpcFixture.CreateAsync();
        var headers = new Metadata { { "accept-language", "fa" } };

        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.Client.FailAsync(new FailureRequest { Mode = "rule" }, headers));

        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
        var status = exception.GetRpcStatus();
        Assert.NotNull(status);
        var errorInfo = status.GetDetail<ErrorInfo>();
        Assert.Equal("orders", errorInfo?.Domain);
        Assert.Equal("LIMIT_EXCEEDED", errorInfo?.Reason);
        Assert.Equal("سقف 5 عدد رد شد.", status.GetDetail<LocalizedMessage>()?.Message);
        var violation = Assert.Single(status.GetDetail<PreconditionFailure>()!.Violations);
        Assert.Equal("LIMIT_EXCEEDED", violation.Type);
        Assert.Equal("BUSINESS_RULE:orders", violation.Subject);
        Assert.Equal("سقف 5 عدد رد شد.", violation.Description);
    }

    [Fact]
    public async Task Unknown_exception_is_sanitized()
    {
        await using var fixture = await GrpcFixture.CreateAsync();

        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.Client.FailAsync(new FailureRequest { Mode = "unknown" }));

        Assert.Equal(StatusCode.Internal, exception.StatusCode);
        Assert.DoesNotContain("database-password", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("UNEXPECTED_FAILURE", exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason);
        var interceptorLogs = fixture.Logs.Records
            .Where(record => record.Category.Contains("GrpcFailureInterceptor", StringComparison.Ordinal))
            .ToArray();
        var logEvidence = string.Join(
            Environment.NewLine,
            interceptorLogs.Select(record => $"{record.State}|{record.Formatted}|{record.Exception}"));
        Assert.NotEmpty(interceptorLogs);
        Assert.All(interceptorLogs, record => Assert.Null(record.Exception));
        Assert.DoesNotContain("database-password=must-not-leak", logEvidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retry_info_requires_both_logical_and_method_safety_approval()
    {
        await using var deniedFixture = await GrpcFixture.CreateAsync(allowRetry: false);
        var denied = await Assert.ThrowsAsync<RpcException>(async () =>
            await deniedFixture.Client.FailAsync(new FailureRequest { Mode = "retry" }));
        Assert.Null(denied.GetRpcStatus()?.GetDetail<RetryInfo>());

        await using var allowedFixture = await GrpcFixture.CreateAsync(allowRetry: true);
        var allowed = await Assert.ThrowsAsync<RpcException>(async () =>
            await allowedFixture.Client.FailAsync(new FailureRequest { Mode = "retry" }));
        Assert.Equal(TimeSpan.FromSeconds(3), allowed.GetRpcStatus()?.GetDetail<RetryInfo>()?.RetryDelay.ToTimeSpan());
    }

    [Fact]
    public async Task Precondition_and_quota_rule_codes_remain_machine_readable_on_the_wire()
    {
        await using var fixture = await GrpcFixture.CreateAsync();

        var preconditionException = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.Client.FailAsync(new FailureRequest { Mode = "precondition" }));
        var precondition = Assert.Single(
            preconditionException.GetRpcStatus()!.GetDetail<PreconditionFailure>()!.Violations);
        Assert.Equal("VERSION_MISMATCH", precondition.Type);
        Assert.Equal("customer:customers/42", precondition.Subject);

        var quotaException = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.Client.FailAsync(new FailureRequest { Mode = "quota" }));
        var quota = Assert.Single(
            quotaException.GetRpcStatus()!.GetDetail<QuotaFailure>()!.Violations);
        Assert.Equal("REQUEST_LIMIT", quota.QuotaId);
        Assert.Equal("tenants/42", quota.Subject);
    }

    [Fact]
    public async Task Streaming_termination_uses_the_same_failure_contract()
    {
        await using var fixture = await GrpcFixture.CreateAsync();
        using var call = fixture.Client.StreamThenFail(new FailureRequest());

        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal("first", call.ResponseStream.Current.Value);
        var exception = await Assert.ThrowsAsync<RpcException>(async () =>
            await call.ResponseStream.MoveNext());

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
        Assert.Equal("CUSTOMER_INVALID", exception.GetRpcStatus()?.GetDetail<ErrorInfo>()?.Reason);
    }

    private sealed class GrpcFixture : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly GrpcChannel _channel;

        private GrpcFixture(
            WebApplication application,
            GrpcChannel channel,
            CapturingLoggerProvider logs)
        {
            _application = application;
            _channel = channel;
            Logs = logs;
            Client = new FailureProbe.FailureProbeClient(channel);
        }

        public FailureProbe.FailureProbeClient Client { get; }

        public CapturingLoggerProvider Logs { get; }

        public static async Task<GrpcFixture> CreateAsync(bool allowRetry = false)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var logs = new CapturingLoggerProvider();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton<IGrpcFailureLocalizer, TestLocalizer>();
            if (allowRetry)
            {
                builder.Services.AddSingleton<IGrpcRetrySafetyPolicy, AllowGrpcRetryPolicy>();
            }

            builder.Services.AddGrpc().AddMPCoreFailureHandling(options =>
            {
                options.SupportedCultures.Clear();
                options.SupportedCultures.Add("en");
                options.SupportedCultures.Add("fa");
                options.DefaultCulture = "en";
            });
            builder.Services.AddSingleton<FailureProbeService>();
            var application = builder.Build();
            application.MapGrpcService<FailureProbeService>();
            await application.StartAsync();

            var channel = GrpcChannel.ForAddress(
                "http://localhost",
                new GrpcChannelOptions { HttpHandler = application.GetTestServer().CreateHandler() });
            return new GrpcFixture(application, channel, logs);
        }

        public async ValueTask DisposeAsync()
        {
            _channel.Dispose();
            await _application.DisposeAsync();
        }
    }

    private sealed class TestLocalizer : IGrpcFailureLocalizer
    {
        public string? Localize(FailureMessageDescriptor message, System.Globalization.CultureInfo culture) =>
            culture.TwoLetterISOLanguageName == "fa" && message.Key == "orders.limit_exceeded"
                ? $"سقف {message.Arguments["limit"]} عدد رد شد."
                : culture.TwoLetterISOLanguageName == "fa" && message.Key == "catalog.customer_invalid"
                ? "ورودی نامعتبر است."
                : culture.TwoLetterISOLanguageName == "fa" && message.Key == "validation.required"
                    ? "این مقدار الزامی است."
                    : null;
    }

    private sealed class AllowGrpcRetryPolicy : IGrpcRetrySafetyPolicy
    {
        public bool AllowsRetry(FailureDescriptor failure, ServerCallContext context) =>
            context.Method.EndsWith("/Fail", StringComparison.Ordinal);
    }

    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<LogRecord> _records = new();

        public IReadOnlyCollection<LogRecord> Records => _records.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _records);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string category,
            System.Collections.Concurrent.ConcurrentQueue<LogRecord> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                records.Enqueue(new LogRecord(
                    category,
                    state?.ToString() ?? string.Empty,
                    formatter(state, exception),
                    exception?.ToString()));
            }
        }
    }

    public sealed record LogRecord(
        string Category,
        string State,
        string Formatted,
        string? Exception);
}
