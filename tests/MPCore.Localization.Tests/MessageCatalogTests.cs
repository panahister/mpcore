using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MPCore.Application.Results;
using MPCore.Localization.Tests.Resources;

namespace MPCore.Localization.Tests;

/// <summary>
/// The catalog's rules, shown with fixture cultures: <c>en-GB</c> and <c>en-US</c> stand for any language a
/// product adds. Their texts are English and marked as fixtures, because MP Core carries no other language.
/// </summary>
public sealed class MessageCatalogTests
{
    private sealed class OverrideSource(string culture, string key, string text, int precedence = 100) : IMessageTemplateSource
    {
        public int Precedence => precedence;

        public bool TryGetTemplate(string requestedKey, CultureInfo requestedCulture, [NotNullWhen(true)] out string? template)
        {
            template = requestedKey == key && requestedCulture.Name == culture ? text : null;
            return template is not null;
        }
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                {
                    owner.Entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }

    private static (IMessageCatalog Catalog, CapturingLoggerProvider Logs) Build(params IMessageTemplateSource[] extra)
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        foreach (var source in extra)
        {
            services.AddSingleton(source);
        }

        services.AddMPCoreMessageCatalog(catalog => catalog.AddResources<TestMessages>());
        return (services.BuildServiceProvider().GetRequiredService<IMessageCatalog>(), logs);
    }

    private static readonly Dictionary<string, string> Limit = new() { ["limit"] = "5" };

    [Theory]
    [InlineData("en", "The limit of 5 was exceeded.")]
    [InlineData("en-GB", "en-GB fixture: the limit of 5 has been passed.")]
    [InlineData("en-US", "en-US fixture: no more than 5.")]
    [InlineData("en-US-POSIX", "en-US fixture: no more than 5.")]
    [InlineData("en-AU", "The limit of 5 was exceeded.")]
    public void A_key_is_rendered_through_the_culture_chain(string culture, string expected)
    {
        // en-GB from a culture resource file; en-US from another source; en-US-POSIX through its parent en-US;
        // en-AU, which nothing translates, from the neutral default text.
        var (catalog, _) = Build(new OverrideSource("en-US", "orders.limit_exceeded", "en-US fixture: no more than {limit}."));

        Assert.Equal(expected, catalog.Render("orders.limit_exceeded", Limit, CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void A_key_translated_nowhere_falls_back_to_the_default_text()
    {
        var (catalog, _) = Build();

        Assert.Equal("Only in the default language.", catalog.Render("orders.only_default", null, CultureInfo.GetCultureInfo("en-GB")));
    }

    [Fact]
    public void Named_placeholders_are_substituted_and_unknown_ones_stay()
    {
        var (catalog, _) = Build();

        Assert.Equal(
            "Hello Sara, {other} stays.",
            catalog.Render("orders.placeholders", new Dictionary<string, string> { ["name"] = "Sara" }, CultureInfo.GetCultureInfo("en")));
    }

    [Fact]
    public void An_override_source_beats_the_resource_file_only_in_its_culture()
    {
        var (catalog, _) = Build(new OverrideSource("en-GB", "orders.limit_exceeded", "en-GB fixture, overridden: at most {limit}."));

        Assert.Equal("en-GB fixture, overridden: at most 5.", catalog.Render("orders.limit_exceeded", Limit, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("The limit of 5 was exceeded.", catalog.Render("orders.limit_exceeded", Limit, CultureInfo.GetCultureInfo("en")));
    }

    [Fact]
    public void A_missing_key_renders_nothing_is_counted_and_is_logged_once()
    {
        var (catalog, logs) = Build();
        long counted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == MessageCatalog.MeterName && instrument.Name == "mpcore.localization.missing")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "key" && (string?)tag.Value == "orders.nowhere")
                {
                    Interlocked.Add(ref counted, value);
                }
            }
        });
        listener.Start();

        Assert.Null(catalog.Render("orders.nowhere", null, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Null(catalog.Render("orders.nowhere", null, CultureInfo.GetCultureInfo("en-GB")));

        Assert.Equal(2, Interlocked.Read(ref counted));
        Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("orders.nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public void Known_keys_are_those_with_a_default_text()
    {
        var (catalog, _) = Build(new OverrideSource("en-GB", "orders.invented", "en-GB fixture: invented"));

        Assert.True(catalog.IsKnownKey("orders.limit_exceeded"));
        Assert.True(catalog.IsKnownKey("mpcore.permission_denied"));
        Assert.False(catalog.IsKnownKey("orders.invented"));
        Assert.False(catalog.IsKnownKey("orders.nowhere"));
    }

    [Fact]
    public void MP_Core_messages_ship_in_English_below_the_product()
    {
        var (catalog, _) = Build();
        var (translated, _) = Build(new OverrideSource("en-GB", "mpcore.permission_denied", "en-GB fixture: not allowed.", precedence: 0));

        // MP Core's own text is English, and it is what every language falls back to until a product
        // translates the key; a product's text, at the product's precedence, wins in its culture.
        Assert.Equal("You do not have permission to do this.", catalog.Render("mpcore.permission_denied", null, CultureInfo.GetCultureInfo("en")));
        Assert.Equal("You do not have permission to do this.", catalog.Render("mpcore.permission_denied", null, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("en-GB fixture: not allowed.", translated.Render("mpcore.permission_denied", null, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("You do not have permission to do this.", translated.Render("mpcore.permission_denied", null, CultureInfo.GetCultureInfo("en")));
    }

    [Fact]
    public void The_catalog_is_the_transport_neutral_localizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMPCoreMessageCatalog(catalog => catalog.AddResources<TestMessages>());
        using var provider = services.BuildServiceProvider();

        var localizer = provider.GetRequiredService<IFailureMessageLocalizer>();

        Assert.Same(provider.GetRequiredService<IMessageCatalog>(), localizer);
        Assert.Equal("en-GB fixture: the limit of 5 has been passed.", localizer.Localize(new FailureMessageDescriptor("orders.limit_exceeded", Limit), CultureInfo.GetCultureInfo("en-GB")));
    }
}
