using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MPCore.Application.Results;
using MPCore.Application.Time;

namespace MPCore.Localization.EntityFrameworkCore;

/// <summary>
/// The EF Core store: changes are tracked on the product's own context and committed by the handler's
/// transaction. Every instance picks the change up at its next refresh.
/// </summary>
/// <typeparam name="TContext">The product's context, which applies <see cref="LocalizationModelBuilderExtensions.ApplyMPCoreLocalization"/>.</typeparam>
/// <param name="context">The context.</param>
/// <param name="clock">The clock.</param>
public sealed class EntityFrameworkMessageTranslationStore<TContext>(TContext context, IClock clock) : IMessageTranslationStore
    where TContext : DbContext
{
    /// <inheritdoc />
    public async Task SetAsync(string key, string culture, string text, CancellationToken cancellationToken)
    {
        var (normalizedKey, normalizedCulture) = Normalize(key, culture);
        if (string.IsNullOrWhiteSpace(text) || text.Length > LocalizationModelBuilderExtensions.MaximumTextLength)
        {
            throw new ArgumentException(
                $"A translation must be non-empty and at most {LocalizationModelBuilderExtensions.MaximumTextLength} characters.",
                nameof(text));
        }

        var set = context.Set<MessageTranslationRecord>();
        var record = await set.FindAsync([normalizedKey, normalizedCulture], cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            record = new MessageTranslationRecord { Key = normalizedKey, Culture = normalizedCulture };
            set.Add(record);
        }

        record.Text = text;
        record.ModifiedOnUtc = clock.UtcNow;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string key, string culture, CancellationToken cancellationToken)
    {
        var (normalizedKey, normalizedCulture) = Normalize(key, culture);
        var set = context.Set<MessageTranslationRecord>();
        var record = await set.FindAsync([normalizedKey, normalizedCulture], cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return false;
        }

        set.Remove(record);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageTranslationEntry>> ListAsync(string? culture, CancellationToken cancellationToken)
    {
        var query = context.Set<MessageTranslationRecord>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(culture))
        {
            var normalized = CultureInfo.GetCultureInfo(culture).Name;
            query = query.Where(record => record.Culture == normalized);
        }

        return await query
            .OrderBy(record => record.Culture).ThenBy(record => record.Key)
            .Select(record => new MessageTranslationEntry(record.Key, record.Culture, record.Text, record.ModifiedOnUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static (string Key, string Culture) Normalize(string key, string culture)
    {
        // The failure model's own validation: a key that FailureMessageDescriptor would refuse can never
        // be rendered, so it is refused here too.
        _ = new FailureMessageDescriptor(key);
        if (string.IsNullOrWhiteSpace(culture))
        {
            throw new ArgumentException("A culture is required.", nameof(culture));
        }

        CultureInfo parsed;
        try
        {
            parsed = CultureInfo.GetCultureInfo(culture, predefinedOnly: true);
        }
        catch (CultureNotFoundException exception)
        {
            throw new ArgumentException($"'{culture}' is not a known culture.", nameof(culture), exception);
        }

        if (parsed.Name.Length == 0)
        {
            throw new ArgumentException("The invariant culture holds the resource files' default text and cannot be overridden.", nameof(culture));
        }

        return (key, parsed.Name);
    }
}
