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
/// Reads templates from a .resx resource file, the .NET standard for localized text. The neutral file
/// (for example <c>Messages.resx</c>) holds the default language, and each culture file (for example
/// <c>Messages.fa.resx</c>) holds one translation.
/// </summary>
/// <param name="resources">The resource manager for the file.</param>
/// <param name="precedence">The precedence; resource files default to 0.</param>
public sealed class ResourceMessageTemplateSource(ResourceManager resources, int precedence = 0) : IMessageTemplateSource
{
    private readonly ResourceManager _resources = resources ?? throw new ArgumentNullException(nameof(resources));

    /// <inheritdoc />
    public int Precedence { get; } = precedence;

    /// <inheritdoc />
    public bool TryGetTemplate(string key, CultureInfo culture, [NotNullWhen(true)] out string? template)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ResourceSet? set;
        try
        {
            // tryParents: false. The catalog walks the culture chain itself, so that an override for
            // "fa" is found before the resource file's default text.
            set = _resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        }
        catch (MissingManifestResourceException)
        {
            set = null;
        }

        template = set?.GetString(key);
        return template is not null;
    }
}

/// <summary>One stored translation, as an administrator sees it.</summary>
/// <param name="Key">The message key.</param>
/// <param name="Culture">The culture name, for example <c>fa</c>.</param>
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
