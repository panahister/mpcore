using MPCore.Application.Results;

namespace MPCore.Application.Querying;

/// <summary>
/// The sort fields a query publishes. A caller-supplied field is either one of these or a governed
/// failure; it never becomes part of a database expression on trust. This is what lets a read port stay
/// typed while a client still chooses the order.
/// </summary>
public sealed class SortAllowlist
{
    /// <summary>The failure domain used when a caller asks for a field a query does not publish.</summary>
    public const string FailureDomain = "mpcore.querying";

    /// <summary>The failure code used when a caller asks for a field a query does not publish.</summary>
    public const string FailureCode = "SORT_FIELD_NOT_ALLOWED";

    private readonly HashSet<string> _fields;

    /// <summary>Creates an allowlist. Field names are compared without regard to case.</summary>
    /// <param name="fields">Every field this query can be sorted by. At least one is required.</param>
    public SortAllowlist(params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Length == 0 || Array.Exists(fields, field => string.IsNullOrWhiteSpace(field)))
        {
            throw new ArgumentException("A sort allowlist names at least one field, and no field is blank.", nameof(fields));
        }

        _fields = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The published field names.</summary>
    public IReadOnlyCollection<string> Fields => _fields;

    /// <summary>Whether the query can be sorted by this field.</summary>
    /// <param name="field">The caller-supplied field name.</param>
    public bool Allows(string? field) => field is not null && _fields.Contains(field);

    /// <summary>
    /// Resolves a requested sort. A null request yields the query own default; an allowed field comes
    /// back spelled as the allowlist spells it, so an adapter can switch on it safely.
    /// </summary>
    /// <param name="requested">What the caller asked for, or null.</param>
    /// <param name="fallback">The order the query uses when nothing was asked for.</param>
    public Result<SortSpec> Resolve(SortSpec? requested, SortSpec fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (requested is null)
        {
            return Result<SortSpec>.Success(fallback);
        }

        if (!Allows(requested.Field))
        {
            return Result<SortSpec>.FromFailure(Rejected());
        }

        var canonical = _fields.First(field => string.Equals(field, requested.Field, StringComparison.OrdinalIgnoreCase));
        return Result<SortSpec>.Success(requested with { Field = canonical });
    }

    /// <summary>
    /// The failure a rejected field produces. It names the input that was wrong, not the value: echoing
    /// caller text into a governed failure is how unsanitised input reaches a log or a screen.
    /// </summary>
    public FailureDescriptor Rejected() => new(
        new ErrorIdentity(FailureDomain, FailureCode),
        ErrorCategory.Validation,
        new FailureMessageDescriptor("mpcore.querying.sort_field_not_allowed"),
        details: [new ValidationFailureDetail([new FieldViolation("sort", FailureCode, new FailureMessageDescriptor("mpcore.querying.sort_field_not_allowed"))])]);
}
