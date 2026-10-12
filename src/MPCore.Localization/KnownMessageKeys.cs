namespace MPCore.Localization;

/// <summary>Where the default text of a known message key comes from.</summary>
public enum MessageTextOrigin
{
    /// <summary>
    /// A text of the product: one of its resource files, or another source it registered that lists its
    /// keys (<see cref="IListableMessageTemplateSource"/>).
    /// </summary>
    Product = 0,

    /// <summary>MP Core's own English text, shipped in <c>MPCore.Localization</c>.</summary>
    MPCore = 1,
}

/// <summary>
/// A message key the code uses: its neutral default text, the names of the placeholders in that text, and
/// where the text comes from. An administrator's form offers these keys for translation, and a translation
/// keeps every placeholder.
/// </summary>
/// <param name="Key">The message key.</param>
/// <param name="DefaultText">
/// The neutral default text that rendering falls back to: the text of the listing source with the highest
/// precedence.
/// </param>
/// <param name="Placeholders">
/// The names of the placeholders in <paramref name="DefaultText"/>, each once, in the order they first
/// appear, parsed by the rule the catalog renders with (<c>{name}</c>, lower-case).
/// </param>
/// <param name="Origin">Whether <paramref name="DefaultText"/> is MP Core's own or the product's.</param>
public sealed record KnownMessageKey(
    string Key,
    string DefaultText,
    IReadOnlyList<string> Placeholders,
    MessageTextOrigin Origin);
