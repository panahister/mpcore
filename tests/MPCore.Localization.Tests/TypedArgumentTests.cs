using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Google.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MPCore.Application.Results;
using MPCore.Localization.Tests.Protos;
using MPCore.Localization.Tests.Resources;
using MPCore.Transport.Grpc;
using MPCore.Transport.Http;

namespace MPCore.Localization.Tests;

/// <summary>
/// A typed argument is formatted at the edge for the negotiated culture, by the formatter a product supplies,
/// or rendered in its invariant form when there is none or it fails. The fake formatter writes the culture's
/// name, so a test sees which culture it was given. The fixture cultures stand for any language.
/// </summary>
public sealed class TypedArgumentTests
{
    private const string Key = "orders.ship_by";
    private const string Invariant = "Order A-17 ships by 2026-10-12, before 2026-10-12T09:30:00+04:00: 1234567 items for 1234.50.";

    private static readonly Dictionary<string, string> Strings = new() { ["order"] = "A-17" };

    private static readonly Dictionary<string, MessageArgument> Typed = new()
    {
        ["date"] = MessageArgument.Date(new DateOnly(2026, 10, 12)),
        ["deadline"] = MessageArgument.DateAndTime(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.FromHours(4))),
        ["count"] = MessageArgument.Integer(1234567),
        ["total"] = MessageArgument.Decimal(1234.50m),
    };

    private static readonly FailureDescriptor ShipBy = new(
        new ErrorIdentity("orders", "SHIP_BY_PASSED"),
        ErrorCategory.BusinessRule,
        new FailureMessageDescriptor(Key, Strings, Typed));

    public static TheoryData<string> Cultures() => new() { "en", "en-GB", "en-AU", "en-US-POSIX" };

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Without_a_formatter_a_typed_argument_renders_in_its_invariant_form_in_every_culture(string culture)
    {
        var (catalog, _) = Build(formatter: null);

        Assert.Equal(Invariant, catalog.Render(Key, Strings, Typed, CultureInfo.GetCultureInfo(culture)));
        Assert.Equal(Invariant, catalog.Localize(ShipBy.Message, CultureInfo.GetCultureInfo(culture)));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void The_products_formatter_formats_each_typed_argument_for_the_negotiated_culture(string culture)
    {
        var formatter = new CultureNameFormatter();
        var (catalog, _) = Build(formatter);

        var rendered = catalog.Render(Key, Strings, Typed, CultureInfo.GetCultureInfo(culture));

        Assert.Equal(
            $"Order A-17 ships by {culture}:2026-10-12, before {culture}:2026-10-12T09:30:00+04:00: {culture}:1234567 items for {culture}:1234.50.",
            rendered);
        // Once per typed argument, with its name, its typed value and the culture the caller negotiated, even
        // though the template came from the neutral file; never for the string argument.
        Assert.Equal(
            Typed.Keys.Order(StringComparer.Ordinal).Select(name => (name, Typed[name].Value, culture)),
            formatter.Calls.Select(static call => (call.Name, call.Value.Value, call.Culture)).OrderBy(static call => call.Name, StringComparer.Ordinal));
        Assert.IsType<DateOnly>(formatter.Calls.Single(static call => call.Name == "date").Value.Value);
        Assert.IsType<DateTimeOffset>(formatter.Calls.Single(static call => call.Name == "deadline").Value.Value);
        Assert.IsType<long>(formatter.Calls.Single(static call => call.Name == "count").Value.Value);
        Assert.IsType<decimal>(formatter.Calls.Single(static call => call.Name == "total").Value.Value);

        // The descriptor's path, which both transports take, formats the same way.
        Assert.Equal(rendered, catalog.Localize(ShipBy.Message, CultureInfo.GetCultureInfo(culture)));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void A_string_argument_renders_exactly_as_before_in_every_culture(string culture)
    {
        var (plain, _) = Build(formatter: null);
        var (formatted, _) = Build(new CultureNameFormatter());
        var arguments = new Dictionary<string, string> { ["limit"] = "1234.50" };
        var expected = culture == "en-GB" ? "en-GB fixture: the limit of 1234.50 has been passed." : "The limit of 1234.50 was exceeded.";

        Assert.Equal(expected, plain.Render("orders.limit_exceeded", arguments, CultureInfo.GetCultureInfo(culture)));
        Assert.Equal(expected, formatted.Render("orders.limit_exceeded", arguments, CultureInfo.GetCultureInfo(culture)));
        Assert.Equal(expected, formatted.Render("orders.limit_exceeded", arguments, typedArguments: null, CultureInfo.GetCultureInfo(culture)));
        Assert.Equal(expected, formatted.Localize(new FailureMessageDescriptor("orders.limit_exceeded", arguments), CultureInfo.GetCultureInfo(culture)));
    }

    public static TheoryData<string, string> Failures() => new()
    {
        { "throws", "threw" },
        { "null", "empty" },
        { "empty", "empty" },
        { "blank", "empty" },
        { "too-long", "too_long" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void A_formatter_that_fails_gives_the_invariant_form_is_counted_and_never_logs_the_value(string behaviour, string reason)
    {
        var (catalog, logs) = Build(new FailingFormatter(behaviour));
        var counted = new ConcurrentBag<(string? Argument, string? Reason)>();
        using var listener = Listen(counted);

        var rendered = catalog.Render(Key, Strings, Typed, CultureInfo.GetCultureInfo("en-GB"));

        Assert.Equal(Invariant, rendered);
        Assert.Equal(
            Typed.Keys.Order(StringComparer.Ordinal).Select(name => ((string?)name, (string?)reason)),
            counted.OrderBy(static entry => entry.Argument, StringComparer.Ordinal));
        Assert.NotEmpty(logs.Entries);
        Assert.All(logs.Entries, entry =>
        {
            Assert.All(Typed.Values, value => Assert.DoesNotContain(value.InvariantValue, entry.Message, StringComparison.Ordinal));
            Assert.DoesNotContain(FailingFormatter.Secret, entry.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_formatted_value_of_exactly_the_limit_is_used()
    {
        var (catalog, _) = Build(new FailingFormatter("at-limit"));
        var limit = new string('x', FailureMessageDescriptor.MaximumArgumentValueLength);

        Assert.Equal(
            Invariant.Replace("by 2026-10-12,", $"by {limit},", StringComparison.Ordinal),
            catalog.Render(Key, Strings, Typed, CultureInfo.GetCultureInfo("en")));
    }

    [Theory]
    [InlineData("en-GB", true)]
    [InlineData("en-AU", true)]
    [InlineData("en-GB", false)]
    public async Task Over_REST_the_detail_carries_the_formatted_arguments(string language, bool withFormatter)
    {
        await using var host = await StartAsync(withFormatter ? new CultureNameFormatter() : null);

        var (status, problem) = await host.GetAsync("/ship-by", language);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(withFormatter ? Formatted(language) : Invariant, problem.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("en-GB", true)]
    [InlineData("en-AU", true)]
    [InlineData("en-GB", false)]
    public async Task Over_gRPC_the_localized_message_carries_the_formatted_arguments(string language, bool withFormatter)
    {
        await using var host = await StartAsync(withFormatter ? new CultureNameFormatter() : null);

        var status = await host.FailAsync(language);

        Assert.Equal(language, status.GetDetail<LocalizedMessage>()?.Locale);
        Assert.Equal(withFormatter ? Formatted(language) : Invariant, status.GetDetail<LocalizedMessage>()?.Message);
    }

    [Fact]
    public async Task A_formatter_that_throws_fails_neither_transport()
    {
        await using var host = await StartAsync(new FailingFormatter("throws"));

        var (status, problem) = await host.GetAsync("/ship-by", "en-GB");
        var grpc = await host.FailAsync("en-GB");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(Invariant, problem.GetProperty("detail").GetString());
        Assert.Equal((int)StatusCode.FailedPrecondition, grpc.Code);
        Assert.Equal(Invariant, grpc.GetDetail<LocalizedMessage>()?.Message);
    }

    [Fact]
    public void A_catalog_of_its_own_receives_typed_arguments_in_their_invariant_form()
    {
        IMessageCatalog catalog = new EchoCatalog();

        Assert.Equal(
            "count=1234567;date=2026-10-12;deadline=2026-10-12T09:30:00+04:00;order=A-17;total=1234.50",
            catalog.Render(Key, Strings, Typed, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Throws<ArgumentException>("typedArguments", () => catalog.Render(
            Key, new Dictionary<string, string> { ["count"] = "3" }, Typed, CultureInfo.GetCultureInfo("en-GB")));
    }

    /// <summary>Names that are argument names, and names that are not: the last reaches a log and a metric tag.</summary>
    public static TheoryData<string, bool> ArgumentNames() => new()
    {
        { "deadline", true },
        { "ship_by", true },
        { "a1", true },
        { "Deadline", false },
        { "1st", false },
        { "_hidden", false },
        { "ship-by", false },
        { "ship by", false },
        { "a.b", false },
        { string.Empty, false },
        { "x\n", false },
        { "x\ny", false },
        { "d\u00e9lai", false },
    };

    [Theory]
    [MemberData(nameof(ArgumentNames))]
    public void A_direct_render_refuses_a_typed_argument_name_that_is_not_an_argument_name_as_the_descriptor_does(string name, bool valid)
    {
        var (catalog, logs) = Build(new CultureNameFormatter());
        var typed = new Dictionary<string, MessageArgument> { [name] = MessageArgument.Integer(7) };

        if (valid)
        {
            Assert.Null(Record.Exception(() => new FailureMessageDescriptor(Key, null, typed)));
            Assert.Null(Record.Exception(() => catalog.Render(Key, null, typed, CultureInfo.GetCultureInfo("en"))));
            return;
        }

        var refused = Assert.Throws<ArgumentException>("typedArguments", () => catalog.Render(Key, null, typed, CultureInfo.GetCultureInfo("en")));
        Assert.Throws<ArgumentException>("typedArguments", () => new FailureMessageDescriptor(Key, null, typed));
        // The refusal does not repeat the name, and nothing was logged for it.
        Assert.DoesNotContain(name.Length == 0 ? "\0" : name, refused.Message, StringComparison.Ordinal);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public void A_name_that_is_not_an_argument_name_never_reaches_the_formatter_or_the_metric()
    {
        var (catalog, _) = Build(new CultureNameFormatter());
        var counted = new ConcurrentBag<(string? Argument, string? Reason)>();
        using var listener = Listen(counted);

        Assert.Throws<ArgumentException>("typedArguments", () => catalog.Render(
            Key, null, new Dictionary<string, MessageArgument> { ["Bad Name"] = MessageArgument.Integer(1) }, CultureInfo.GetCultureInfo("en-GB")));

        Assert.Empty(counted);
    }

    private static string Formatted(string culture) =>
        $"Order A-17 ships by {culture}:2026-10-12, before {culture}:2026-10-12T09:30:00+04:00: {culture}:1234567 items for {culture}:1234.50.";

    private static (IMessageCatalog Catalog, MessageCatalogTests.CapturingLoggerProvider Logs) Build(IMessageArgumentFormatter? formatter)
    {
        var logs = new MessageCatalogTests.CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddMPCoreMessageCatalog(catalog =>
        {
            catalog.AddResources<TestMessages>().AddResources<TypedMessages>();
            if (formatter is not null)
            {
                catalog.UseArgumentFormatter(formatter);
            }
        });
        return (services.BuildServiceProvider().GetRequiredService<IMessageCatalog>(), logs);
    }

    private static MeterListener Listen(ConcurrentBag<(string? Argument, string? Reason)> counted)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == MessageCatalog.MeterName && instrument.Name == "mpcore.localization.argument_fallback")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? key = null, argument = null, reason = null;
            foreach (var tag in tags)
            {
                switch (tag.Key)
                {
                    case "key": key = tag.Value as string; break;
                    case "argument": argument = tag.Value as string; break;
                    case "reason": reason = tag.Value as string; break;
                }
            }

            if (key == Key)
            {
                for (var i = 0; i < value; i++)
                {
                    counted.Add((argument, reason));
                }
            }
        });
        listener.Start();
        return listener;
    }

    private static async Task<Host> StartAsync(IMessageArgumentFormatter? formatter)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMPCoreMessageCatalog(catalog =>
        {
            catalog.AddResources<TypedMessages>();
            if (formatter is not null)
            {
                catalog.UseArgumentFormatter(formatter);
            }
        });
        builder.Services.AddMPCoreHttpFailureHandling(options =>
        {
            options.SupportedCultures.Add("en-GB");
            options.SupportedCultures.Add("en-AU");
        });
        builder.Services.AddGrpc().AddMPCoreFailureHandling(options =>
        {
            options.SupportedCultures.Add("en-GB");
            options.SupportedCultures.Add("en-AU");
        });

        var application = builder.Build();
        application.UseMPCoreProblemDetails();
        application.UseMPCoreRequestContext();
        application.UseRouting();
        application.MapGet("/ship-by", static () => Result.FromFailure(ShipBy).ToHttpResult());
        application.MapGrpcService<TypedArgumentProbeService>();
        await application.StartAsync();
        return new Host(application);
    }

    /// <summary>The fake formatter: writes the culture's name before the invariant form, and records each call.</summary>
    private sealed class CultureNameFormatter : IMessageArgumentFormatter
    {
        public ConcurrentQueue<(string Name, MessageArgument Value, string Culture)> Calls { get; } = new();

        public string? Format(string name, MessageArgument value, CultureInfo culture)
        {
            Calls.Enqueue((name, value, culture.Name));
            return $"{culture.Name}:{value.InvariantValue}";
        }
    }

    /// <summary>A formatter that fails in one way, and puts the value and a marker in what it throws.</summary>
    private sealed class FailingFormatter(string behaviour) : IMessageArgumentFormatter
    {
        public const string Secret = "formatter-secret-marker";

        public string? Format(string name, MessageArgument value, CultureInfo culture) => behaviour switch
        {
            "throws" => throw new InvalidOperationException($"{Secret}: cannot format {value.InvariantValue}"),
            "null" => null,
            "empty" => string.Empty,
            "blank" => "   ",
            "too-long" => new string('x', FailureMessageDescriptor.MaximumArgumentValueLength + 1),
            "at-limit" => name == "date" ? new string('x', FailureMessageDescriptor.MaximumArgumentValueLength) : value.InvariantValue,
            _ => throw new ArgumentOutOfRangeException(nameof(behaviour)),
        };
    }

    /// <summary>A catalog a product wrote itself before typed arguments existed: it lists the arguments it is given.</summary>
    private sealed class EchoCatalog : IMessageCatalog
    {
        public string? Localize(FailureMessageDescriptor message, CultureInfo culture) => null;

        public string? Render(string key, IReadOnlyDictionary<string, string>? arguments, CultureInfo culture) =>
            string.Join(';', (arguments ?? new Dictionary<string, string>()).OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key}={pair.Value}"));

        public bool IsKnownKey(string key) => false;
    }

    /// <summary>Fails with the typed message, over gRPC.</summary>
    private sealed class TypedArgumentProbeService : CultureProbe.CultureProbeBase
    {
        public override Task<CultureProbeReply> Fail(CultureProbeRequest request, ServerCallContext context)
        {
            Result.FromFailure(ShipBy).ThrowIfFailure();
            return Task.FromResult(new CultureProbeReply());
        }
    }

    private sealed class Host(WebApplication application) : IAsyncDisposable
    {
        private readonly TestServer _server = application.GetTestServer();

        public async Task<(HttpStatusCode Status, JsonElement Problem)> GetAsync(string path, string acceptLanguage)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
            using var client = _server.CreateClient();
            var response = await client.SendAsync(request);
            return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
        }

        public async Task<Google.Rpc.Status> FailAsync(string acceptLanguage)
        {
            using var channel = GrpcChannel.ForAddress(_server.BaseAddress, new GrpcChannelOptions { HttpHandler = _server.CreateHandler() });
            var headers = new Metadata { { "accept-language", acceptLanguage } };
            var exception = await Assert.ThrowsAsync<RpcException>(async () =>
                await new CultureProbe.CultureProbeClient(channel).FailAsync(new CultureProbeRequest { Mode = "typed" }, headers));
            return exception.GetRpcStatus() ?? throw new InvalidOperationException("The failure carried no rich status.");
        }

        public async ValueTask DisposeAsync() => await application.DisposeAsync();
    }
}
