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

        Key = key;
        Arguments = new ReadOnlyDictionary<string, string>(copy);
    }

    /// <summary>Gets the message key.</summary>
    public string Key { get; }

    /// <summary>Gets the substitution arguments.</summary>
    public IReadOnlyDictionary<string, string> Arguments { get; }

    /// <summary>The fallback descriptor used when no more specific message exists.</summary>
    public static FailureMessageDescriptor Generic { get; } = new("mpcore.failure");

    [GeneratedRegex(
        "^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MessageKeyPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentKeyPattern();
}
