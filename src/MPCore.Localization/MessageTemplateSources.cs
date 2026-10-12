using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Resources;

namespace MPCore.Localization;

/// <summary>
/// One place message templates come from: resource files shipped with the code, or a store an
/// administrator edits. The catalog asks every source, highest <see cref="Precedence"/> first.
/// </summary>
public interface IMessageTemplateSource
{
    /// <summary>
    /// Gets the precedence. A higher value wins: an administrator's override (100) beats the product's
    /// resource files (0), which beat MP Core's own defaults (-100).
    /// </summary>
    int Precedence { get; }

    /// <summary>
    /// Finds the template for a key in exactly this culture. Parent cultures are the catalog's job, so a
    /// source never falls back on its own.
    /// </summary>
    /// <param name="key">The message key.</param>
    /// <param name="culture">The exact culture; <see cref="CultureInfo.InvariantCulture"/> means the default text.</param>
    /// <param name="template">The template, with named placeholders such as <c>{limit}</c>.</param>
    bool TryGetTemplate(string key, CultureInfo culture, [NotNullWhen(true)] out string? template);
}

/// <summary>
/// A template source that can list the keys it has a default text for, so that
/// <see cref="IMessageCatalog.ListKnownKeys"/> can offer them. Optional: a source that does not implement
/// it still renders, and adds nothing to the list. The resource-file source implements it; a source of
/// translations, such as the stored one, never does, so a translation never defines a key.
/// </summary>
public interface IListableMessageTemplateSource : IMessageTemplateSource
{
    /// <summary>
    /// Lists every key this source has a default text for, with that text: exactly the keys for which
    /// <see cref="IMessageTemplateSource.TryGetTemplate"/> returns a template in
    /// <see cref="CultureInfo.InvariantCulture"/>. It reads nothing but what the source already holds: no
    /// database and no network.
    /// </summary>
    IReadOnlyDictionary<string, string> ListDefaultTemplates();
}

/// <summary>
/// Reads templates from a .resx resource file, the .NET standard for localized text. The neutral file
/// (for example <c>Messages.resx</c>) holds the default language, and each culture file
/// (<c>Messages.&lt;culture&gt;.resx</c>) holds one translation.
/// </summary>
/// <param name="resources">The resource manager for the file.</param>
/// <param name="precedence">The precedence; resource files default to 0.</param>
public sealed class ResourceMessageTemplateSource(ResourceManager resources, int precedence = 0) : IListableMessageTemplateSource
{
    private readonly ResourceManager _resources = resources ?? throw new ArgumentNullException(nameof(resources));

    /// <inheritdoc />
    public int Precedence { get; } = precedence;

    /// <summary>Gets a value indicating whether this source holds MP Core's own texts.</summary>
    internal bool IsMPCoreDefaults { get; init; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ListDefaultTemplates()
    {
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        var set = Set(CultureInfo.InvariantCulture);
        if (set is null)
        {
            return templates;
        }

        // The neutral file, read the way TryGetTemplate reads it for the invariant culture, so that a key is
        // listed exactly when TryGetTemplate finds it. The set belongs to the resource manager's cache and is
        // not disposed here.
        var entries = set.GetEnumerator();
        while (entries.MoveNext())
        {
            if (entries.Key is string key && entries.Value is string text)
            {
                templates[key] = text;
            }
        }

        return templates;
    }

    /// <inheritdoc />
    public bool TryGetTemplate(string key, CultureInfo culture, [NotNullWhen(true)] out string? template)
    {
        ArgumentNullException.ThrowIfNull(culture);
        template = Set(culture)?.GetString(key);
        return template is not null;
    }

    private ResourceSet? Set(CultureInfo culture)
    {
        try
        {
            // tryParents: false. The catalog walks the culture chain itself, so that an override for a
            // culture is found before the resource file's default text.
            return _resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        }
        catch (MissingManifestResourceException)
        {
            return null;
        }
    }
}

/// <summary>One stored translation, as an administrator sees it.</summary>
/// <param name="Key">The message key.</param>
/// <param name="Culture">The culture name, for example <c>en-GB</c>.</param>
/// <param name="Text">The template, with named placeholders.</param>
/// <param name="ModifiedOnUtc">When it was last written.</param>
public sealed record MessageTranslationEntry(string Key, string Culture, string Text, DateTimeOffset ModifiedOnUtc);

/// <summary>
/// The port an administrator's commands use to change translations. It never saves: the handler's
/// transaction commits the change together with everything else the handler did (ADR-011).
/// </summary>
public interface IMessageTranslationStore
{
    /// <summary>Adds or replaces the translation of a key in a culture.</summary>
    /// <param name="key">The message key.</param>
    /// <param name="culture">The culture name.</param>
    /// <param name="text">The template.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SetAsync(string key, string culture, string text, CancellationToken cancellationToken);

    /// <summary>Removes the translation, so the resource file's text applies again.</summary>
    /// <param name="key">The message key.</param>
    /// <param name="culture">The culture name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when a translation existed.</returns>
    Task<bool> RemoveAsync(string key, string culture, CancellationToken cancellationToken);

    /// <summary>Lists stored translations, optionally of one culture, ordered by culture and key.</summary>
    /// <param name="culture">The culture name, or null for all.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<IReadOnlyList<MessageTranslationEntry>> ListAsync(string? culture, CancellationToken cancellationToken);
}
