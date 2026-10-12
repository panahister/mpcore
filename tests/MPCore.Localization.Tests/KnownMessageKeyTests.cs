using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using MPCore.Application.Results;
using MPCore.Localization.EntityFrameworkCore;
using MPCore.Localization.Tests.Resources;

namespace MPCore.Localization.Tests;

/// <summary>
/// The catalog lists every key that has a default text, so that an administrator's form offers only keys the
/// code uses, with the text to translate and the placeholders a translation keeps. The expected keys and texts
/// are read from the resource files themselves, not from the catalog.
/// </summary>
public sealed class KnownMessageKeyTests
{
    private const int ListingFixturePrecedence = 10;

    private static IMessageCatalog Build(Action<IServiceCollection>? extra = null, bool withListingFixture = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        extra?.Invoke(services);
        services.AddMPCoreMessageCatalog(catalog =>
        {
            catalog.AddResources<TestMessages>();
            if (withListingFixture)
            {
                catalog.AddResources<ListingFixture>(ListingFixturePrecedence);
            }
        });
        return services.BuildServiceProvider().GetRequiredService<IMessageCatalog>();
    }

    [Fact]
    public void A_host_lists_MP_Cores_keys_and_its_own_once_each_ordered_by_key_with_the_texts_of_the_files()
    {
        var catalog = Build();
        var mpcore = NeutralTexts("src/MPCore.Localization/Resources/MPCoreMessages.resx");
        var product = NeutralTexts("tests/MPCore.Localization.Tests/Resources/TestMessages.resx");

        var listed = catalog.ListKnownKeys();

        Assert.NotEmpty(mpcore);
        Assert.Equal(mpcore.Keys.Concat(product.Keys).Order(StringComparer.Ordinal), listed.Select(static entry => entry.Key));
        Assert.All(listed.Where(entry => mpcore.ContainsKey(entry.Key)), entry =>
        {
            Assert.Equal(mpcore[entry.Key], entry.DefaultText);
            Assert.Equal(MessageTextOrigin.MPCore, entry.Origin);
        });
        Assert.All(listed.Where(entry => product.ContainsKey(entry.Key)), entry =>
        {
            Assert.Equal(product[entry.Key], entry.DefaultText);
            Assert.Equal(MessageTextOrigin.Product, entry.Origin);
        });

        // MP Core's texts are the ones it renders: listing changes none of them.
        Assert.Equal("You do not have permission to do this.", catalog.Render("mpcore.permission_denied", null, CultureInfo.GetCultureInfo("en")));
        Assert.Equal(["limit"], listed.Single(static entry => entry.Key == "orders.limit_exceeded").Placeholders);
        Assert.Equal(["precision", "scale"], listed.Single(static entry => entry.Key == "validation.scale_precision").Placeholders);
    }

    [Fact]
    public void A_key_in_two_sources_is_listed_once_with_the_default_text_rendering_uses()
    {
        var catalog = Build(withListingFixture: true);
        var listed = catalog.ListKnownKeys();
        // en-AU: no source has a text in en-AU or en, so rendering falls back to the neutral default text.
        var untranslated = CultureInfo.GetCultureInfo("en-AU");

        var limit = Assert.Single(listed, static entry => entry.Key == "orders.limit_exceeded");
        Assert.Equal("Fixture: at most {limit} per {period}.", limit.DefaultText);
        Assert.Equal(catalog.Render("orders.limit_exceeded", null, untranslated), limit.DefaultText);
        Assert.Equal(["limit", "period"], limit.Placeholders);
        Assert.Equal(MessageTextOrigin.Product, limit.Origin);

        // A key MP Core defines and the product defines again: the product's text wins, so the text is the product's.
        var denied = Assert.Single(listed, static entry => entry.Key == "mpcore.permission_denied");
        Assert.Equal("Fixture: {name} may not do this.", denied.DefaultText);
        Assert.Equal(catalog.Render("mpcore.permission_denied", null, untranslated), denied.DefaultText);
        Assert.Equal(MessageTextOrigin.Product, denied.Origin);

        Assert.Equal(listed.Count, listed.Select(static entry => entry.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_text_without_placeholders_lists_none()
    {
        var listed = Build().ListKnownKeys();

        Assert.Empty(listed.Single(static entry => entry.Key == "orders.only_default").Placeholders);
        Assert.Empty(listed.Single(static entry => entry.Key == "mpcore.permission_denied").Placeholders);
    }

    [Fact]
    public void Placeholders_are_parsed_by_the_rule_the_catalog_renders_with()
    {
        var catalog = Build(withListingFixture: true);
        var listed = catalog.ListKnownKeys();

        var mixed = Assert.Single(listed, static entry => entry.Key == "orders.mixed_braces");
        Assert.Equal(["name", "first_name"], mixed.Placeholders);

        // The oracle is rendering itself: every listed placeholder is substituted, and a brace that is not
        // listed stays as written even when an argument of that name is given.
        var culture = CultureInfo.GetCultureInfo("en-AU");
        foreach (var entry in listed)
        {
            var arguments = entry.Placeholders.ToDictionary(static name => name, static name => $"<{name}>", StringComparer.Ordinal);
            var rendered = catalog.Render(entry.Key, arguments, culture)!;
            Assert.All(entry.Placeholders, name => Assert.DoesNotContain("{" + name + "}", rendered, StringComparison.Ordinal));
        }

        var unlisted = new Dictionary<string, string>(StringComparer.Ordinal) { ["Name"] = "x", ["0"] = "x", ["first-name"] = "x" };
        Assert.Equal(mixed.DefaultText, catalog.Render("orders.mixed_braces", unlisted, culture));
    }

    [Fact]
    public void A_stored_translation_of_an_unknown_key_is_served_in_its_culture_and_never_listed()
    {
        var stored = new DatabaseMessageTemplateSource();
        stored.Replace(
        [
            new MessageTranslationRecord { Key = "orders.invented", Culture = "en-GB", Text = "en-GB fixture, stored: invented." },
            new MessageTranslationRecord { Key = "orders.limit_exceeded", Culture = "en-GB", Text = "en-GB fixture, stored: at most {limit}." },
        ]);
        var catalog = Build(services => services.AddSingleton<IMessageTemplateSource>(stored));

        var listed = catalog.ListKnownKeys();

        Assert.Equal("en-GB fixture, stored: invented.", catalog.Render("orders.invented", null, CultureInfo.GetCultureInfo("en-GB")));
        Assert.DoesNotContain(listed, static entry => entry.Key == "orders.invented");
        Assert.False(catalog.IsKnownKey("orders.invented"));

        // A stored translation of a known key does not change its default text either.
        Assert.Equal("The limit of {limit} was exceeded.", Assert.Single(listed, static entry => entry.Key == "orders.limit_exceeded").DefaultText);
    }

    [Fact]
    public void The_list_agrees_with_IsKnownKey()
    {
        var stored = new DatabaseMessageTemplateSource();
        stored.Replace([new MessageTranslationRecord { Key = "orders.invented", Culture = "en-GB", Text = "en-GB fixture, stored: invented." }]);
        var catalog = Build(
            services =>
            {
                services.AddSingleton<IMessageTemplateSource>(stored);
                services.AddSingleton<IMessageTemplateSource>(new ResourceMessageTemplateSource(new System.Resources.ResourceManager(typeof(ProductPlatform))));
            },
            withListingFixture: true);
        var listed = catalog.ListKnownKeys().Select(static entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        // Every key any file of this repository's catalog fixtures names, in any culture, MP Core's file included,
        // and the keys that exist only as a stored translation or nowhere.
        var candidates = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "tests/MPCore.Localization.Tests/Resources"), "*.resx")
            .Append(Path.Combine(RepositoryRoot(), "src/MPCore.Localization/Resources/MPCoreMessages.resx"))
            .SelectMany(static path => XDocument.Load(path).Root!.Elements("data").Select(static data => (string)data.Attribute("name")!))
            .Concat(["orders.invented", "orders.only_translated", "orders.nowhere"])
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("orders.only_translated", candidates);
        Assert.All(candidates, key => Assert.True(
            catalog.IsKnownKey(key) == listed.Contains(key),
            $"{key}: IsKnownKey says {catalog.IsKnownKey(key)}, the list says {listed.Contains(key)}"));
        Assert.All(listed, key => Assert.True(catalog.IsKnownKey(key), key));
    }

    [Fact]
    public async Task The_list_is_computed_once_and_asks_no_source_that_does_not_list()
    {
        var counting = new CountingListingSource();
        var catalog = Build(services =>
        {
            services.AddSingleton<IMessageTemplateSource>(counting);
            services.AddSingleton<IMessageTemplateSource>(new UnreachableSource());
        });

        var lists = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(catalog.ListKnownKeys)));

        Assert.Equal(1, counting.Calls);
        Assert.All(lists, list => Assert.Same(lists[0], list));
        Assert.Contains(lists[0], static entry => entry.Key == "orders.counted" && entry.Origin == MessageTextOrigin.Product);
        Assert.Same(lists[0], catalog.ListKnownKeys());
        Assert.Equal(1, counting.Calls);
    }

    [Fact]
    public void A_catalog_of_its_own_that_does_not_list_says_so()
    {
        IMessageCatalog catalog = new SilentCatalog();

        Assert.Throws<NotSupportedException>(catalog.ListKnownKeys);
    }

    private static Dictionary<string, string> NeutralTexts(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), relativePath)).Root!.Elements("data")
            .ToDictionary(static data => (string)data.Attribute("name")!, static data => (string)data.Element("value")!, StringComparer.Ordinal);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MPCore.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("MPCore.sln not found above the test output.");
    }

    /// <summary>A listing source that counts how often it is asked for its keys.</summary>
    private sealed class CountingListingSource : IListableMessageTemplateSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int Precedence => 0;

        public IReadOnlyDictionary<string, string> ListDefaultTemplates()
        {
            Interlocked.Increment(ref _calls);
            Thread.Sleep(50);
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["orders.counted"] = "Counted {times} times." };
        }

        public bool TryGetTemplate(string key, CultureInfo culture, [NotNullWhen(true)] out string? template)
        {
            template = key == "orders.counted" && culture.Name.Length == 0 ? "Counted {times} times." : null;
            return template is not null;
        }
    }

    /// <summary>Stands for a source that reads a database or the network: listing must never ask it.</summary>
    private sealed class UnreachableSource : IMessageTemplateSource
    {
        public int Precedence => 100;

        public bool TryGetTemplate(string key, CultureInfo culture, [NotNullWhen(true)] out string? template) =>
            throw new InvalidOperationException("Listing asked a source that does not list its keys.");
    }

    /// <summary>A catalog a product wrote itself, before the list existed.</summary>
    private sealed class SilentCatalog : IMessageCatalog
    {
        public string? Localize(FailureMessageDescriptor message, CultureInfo culture) => null;

        public string? Render(string key, IReadOnlyDictionary<string, string>? arguments, CultureInfo culture) => null;

        public bool IsKnownKey(string key) => false;
    }
}
