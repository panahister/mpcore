using System.Globalization;
using System.Text.RegularExpressions;
using MPCore.Application.Results;

namespace MPCore.Localization;

/// <summary>
/// Formats a typed message argument for a culture: how a date, a date and time or a number looks in each
/// language a product serves is the product's decision, never MP Core's. A product registers one with
/// <see cref="MessageCatalogBuilder.UseArgumentFormatter"/>.
/// </summary>
/// <remarks>
/// The catalog calls it once for each typed argument of a message it renders, before substitution, with the
/// argument's name, its typed value and the culture the caller negotiated, for REST <c>detail</c>, gRPC
/// <c>LocalizedMessage</c> and a direct <see cref="IMessageCatalog.Render(string, IReadOnlyDictionary{string, string}?, IReadOnlyDictionary{string, MessageArgument}?, CultureInfo)"/>
/// alike. The catalog is a singleton, so the formatter is called concurrently. When it throws, or returns
/// null, white space or a text longer than <see cref="FailureMessageDescriptor.MaximumArgumentValueLength"/>,
/// the argument renders in its invariant form, the request does not fail, the
/// <c>mpcore.localization.argument_fallback</c> metric counts it, and the value is never logged. With no
/// formatter registered, every typed argument renders in its invariant form in every culture.
/// </remarks>
public interface IMessageArgumentFormatter
{
    /// <summary>Formats one typed argument.</summary>
    /// <param name="name">The argument's name, as the template's placeholder names it.</param>
    /// <param name="value">The typed value; <see cref="MessageArgument.InvariantValue"/> is its invariant form.</param>
    /// <param name="culture">The culture the caller negotiated.</param>
    /// <returns>The text to substitute.</returns>
    string? Format(string name, MessageArgument value, CultureInfo culture);
}

/// <summary>Puts typed arguments beside the string arguments, each as the text it renders as.</summary>
internal static partial class MessageArgumentSubstitution
{
    /// <summary>
    /// Refuses a typed argument whose name is not an argument name, that has no value, or that repeats the name of
    /// a string argument, before anything is rendered. A name reaches the warning log and the metric tag
    /// <c>argument</c> when a formatter fails, so a name that is not one is refused here, as the descriptor refuses
    /// it, and the refusal does not repeat it.
    /// </summary>
    internal static void Validate(
        IReadOnlyDictionary<string, string>? arguments,
        IReadOnlyDictionary<string, MessageArgument>? typedArguments)
    {
        if (typedArguments is null)
        {
            return;
        }

        foreach (var (name, value) in typedArguments)
        {
            if (!ArgumentName().IsMatch(name))
            {
                throw new ArgumentException(
                    "A typed argument has a name that is not a lower-case letter followed by lower-case letters, digits or underscores.",
                    nameof(typedArguments));
            }

            if (value is null)
            {
                throw new ArgumentException($"The typed argument '{name}' has no value.", nameof(typedArguments));
            }

            if (arguments is not null && arguments.Keys.Contains(name, StringComparer.Ordinal))
            {
                throw new ArgumentException($"The typed argument '{name}' repeats the name of a string argument.", nameof(typedArguments));
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="arguments"/> itself when there is no typed argument, so a message with string
    /// arguments only renders exactly as it did before typed arguments existed. Otherwise each typed argument
    /// is added as <paramref name="render"/> renders it, once.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? Merge(
        IReadOnlyDictionary<string, string>? arguments,
        IReadOnlyDictionary<string, MessageArgument>? typedArguments,
        Func<string, MessageArgument, string> render)
    {
        Validate(arguments, typedArguments);
        if (typedArguments is null || typedArguments.Count == 0)
        {
            return arguments;
        }

        var merged = arguments is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(arguments, StringComparer.Ordinal);
        foreach (var (name, value) in typedArguments)
        {
            merged.Add(name, render(name, value));
        }

        return merged;
    }

    // The rule of FailureMessageDescriptor and of a placeholder in a template; TypedArgumentTests holds the two together.
    [GeneratedRegex("^[a-z][a-z0-9_]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentName();
}
