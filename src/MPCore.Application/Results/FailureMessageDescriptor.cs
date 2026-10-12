using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace MPCore.Application.Results;

/// <summary>
/// A localizable message reference. It carries a key and bounded arguments, never a rendered
/// sentence, so no transport adapter is forced to choose a language for the application layer.
/// </summary>
public sealed partial record FailureMessageDescriptor
{
    /// <summary>The largest permitted message-key length.</summary>
    public const int MaximumKeyLength = 160;

    /// <summary>The largest permitted number of message arguments.</summary>
    public const int MaximumArgumentCount = 16;

    /// <summary>The largest permitted length of a single argument value.</summary>
    public const int MaximumArgumentValueLength = 256;

    /// <summary>Creates a message descriptor.</summary>
    /// <param name="key">The lower-case dot-separated message key.</param>
    /// <param name="arguments">The bounded substitution arguments.</param>
    /// <exception cref="ArgumentException">The key or the arguments exceed the approved limits.</exception>
    public FailureMessageDescriptor(
        string key,
        IReadOnlyDictionary<string, string>? arguments = null)
        : this(key, arguments, typedArguments: null)
    {
    }

    /// <summary>
    /// Creates a message descriptor with typed arguments beside the string arguments: a date, a date and
    /// time with its offset, an integer or a decimal, which the edge formats for the caller's culture. The
    /// template names a typed argument as it names any other, <c>{deadline}</c>.
    /// </summary>
    /// <param name="key">The lower-case dot-separated message key.</param>
    /// <param name="arguments">The bounded substitution arguments, rendered as they are.</param>
    /// <param name="typedArguments">
    /// The typed arguments. Their names follow the rule of <paramref name="arguments"/> and never repeat one of
    /// them, and the two together hold at most <see cref="MaximumArgumentCount"/> arguments.
    /// </param>
    /// <exception cref="ArgumentException">The key or the arguments exceed the approved limits.</exception>
    public FailureMessageDescriptor(
        string key,
        IReadOnlyDictionary<string, string>? arguments,
        IReadOnlyDictionary<string, MessageArgument>? typedArguments)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            key.Length > MaximumKeyLength ||
            !MessageKeyPattern().IsMatch(key))
        {
            throw new ArgumentException(
                "Message keys must be lower-case dot-separated identifiers.",
                nameof(key));
        }

        var copy = arguments is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(arguments, StringComparer.Ordinal);

        if (copy.Count > MaximumArgumentCount ||
            copy.Any(pair =>
                !ArgumentKeyPattern().IsMatch(pair.Key) ||
                pair.Value is null ||
                pair.Value.Length > MaximumArgumentValueLength))
        {
            throw new ArgumentException("Message arguments exceed the approved key, count, or length limits.", nameof(arguments));
        }

        var typed = typedArguments is null
            ? new Dictionary<string, MessageArgument>(StringComparer.Ordinal)
            : new Dictionary<string, MessageArgument>(typedArguments, StringComparer.Ordinal);

        if (copy.Count + typed.Count > MaximumArgumentCount ||
            typed.Any(pair =>
                !ArgumentKeyPattern().IsMatch(pair.Key) ||
                copy.ContainsKey(pair.Key) ||
                pair.Value is null ||
                pair.Value.InvariantValue.Length > MaximumArgumentValueLength))
        {
            throw new ArgumentException(
                "Typed message arguments exceed the approved key, count, or length limits, or repeat the name of a string argument.",
                nameof(typedArguments));
        }

        Key = key;
        Arguments = new ReadOnlyDictionary<string, string>(copy);
        TypedArguments = new ReadOnlyDictionary<string, MessageArgument>(typed);
    }

    /// <summary>Gets the message key.</summary>
    public string Key { get; }

    /// <summary>Gets the substitution arguments.</summary>
    public IReadOnlyDictionary<string, string> Arguments { get; }

    /// <summary>
    /// Gets the typed arguments, which the edge formats for the caller's culture. Empty when the message has
    /// none.
    /// </summary>
    public IReadOnlyDictionary<string, MessageArgument> TypedArguments { get; }

    /// <summary>The fallback descriptor used when no more specific message exists.</summary>
    public static FailureMessageDescriptor Generic { get; } = new("mpcore.failure");

    [GeneratedRegex(
        "^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MessageKeyPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentKeyPattern();
}
