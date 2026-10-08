using System.Collections.ObjectModel;

namespace MPCore.Security;

/// <summary>
/// The only supported way to construct a <see cref="CurrentActor"/>. The builder validates every
/// documented bound and freezes the result, so an actor can never escape construction in an
/// unbounded or internally inconsistent state.
/// </summary>
public sealed class CurrentActorBuilder
{
    private readonly List<string> _scopes = [];
    private readonly List<string> _roles = [];

    /// <summary>Creates a builder for the supplied actor kind.</summary>
    /// <param name="kind">The kind of caller being described.</param>
    public CurrentActorBuilder(ActorKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Unknown actor kind.");
        }

        Kind = kind;
    }

    /// <summary>Gets the kind of caller being described.</summary>
    public ActorKind Kind { get; }

    /// <summary>Gets or sets the stable subject identifier. Required when authenticated.</summary>
    public string? SubjectId { get; set; }

    /// <summary>Gets or sets the preferred user name.</summary>
    public string? UserName { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the email address.</summary>
    public string? Email { get; set; }

    /// <summary>Gets or sets a value indicating whether the email is provider-verified.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Gets or sets the phone number.</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>Gets or sets a value indicating whether the phone number is provider-verified.</summary>
    public bool PhoneNumberVerified { get; set; }

    /// <summary>Gets or sets the provider session identifier.</summary>
    public string? SessionId { get; set; }

    /// <summary>Gets or sets the authorized party (client) identifier.</summary>
    public string? ClientId { get; set; }

    /// <summary>Gets or sets the token issuer.</summary>
    public string? Issuer { get; set; }

    /// <summary>Gets or sets when the person authenticated (<c>auth_time</c>); null when the provider did not say.</summary>
    public DateTimeOffset? AuthenticatedAt { get; set; }

    /// <summary>Gets or sets the credential expiry.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Gets or sets when the credential was issued (<c>iat</c>).</summary>
    public DateTimeOffset? IssuedAt { get; set; }

    /// <summary>
    /// Gets the mutable map of additional claims. Bounds are enforced by <see cref="Build"/>: at most
    /// <see cref="CurrentActor.MaximumAdditionalClaimCount"/> entries, each key and value non-empty and at most
    /// <see cref="CurrentActor.MaximumMemberLength"/> characters.
    /// </summary>
    public IDictionary<string, string> AdditionalClaims { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the mutable scope list. Bounds are enforced by <see cref="Build"/>.</summary>
    public IList<string> Scopes => _scopes;

    /// <summary>Gets the mutable role list. Bounds are enforced by <see cref="Build"/>.</summary>
    public IList<string> Roles => _roles;

    /// <summary>Adds a scope, ignoring null, empty and whitespace-only values.</summary>
    /// <param name="scope">The scope to add.</param>
    /// <returns>The same builder.</returns>
    public CurrentActorBuilder AddScope(string? scope)
    {
        if (!string.IsNullOrWhiteSpace(scope))
        {
            _scopes.Add(scope);
        }

        return this;
    }

    /// <summary>Adds a normalized role, ignoring null, empty and whitespace-only values.</summary>
    /// <param name="role">The role to add.</param>
    /// <returns>The same builder.</returns>
    public CurrentActorBuilder AddRole(string? role)
    {
        if (!string.IsNullOrWhiteSpace(role))
        {
            _roles.Add(role);
        }

        return this;
    }

    /// <summary>
    /// Validates every bound and produces the immutable actor.
    /// </summary>
    /// <exception cref="ArgumentException">A member exceeds its documented bound.</exception>
    /// <exception cref="InvalidOperationException">
    /// An authenticated actor carries no non-empty <see cref="SubjectId"/>.
    /// </exception>
    public CurrentActor Build()
    {
        if (Kind != ActorKind.Anonymous && string.IsNullOrWhiteSpace(SubjectId))
        {
            throw new InvalidOperationException(
                "An authenticated actor requires a non-empty SubjectId taken from the token subject.");
        }

        var scopes = Normalize(
            _scopes,
            CurrentActor.MaximumScopeCount,
            CurrentActor.MaximumScopeLength,
            nameof(Scopes));
        var roles = Normalize(
            _roles,
            CurrentActor.MaximumRoleCount,
            CurrentActor.MaximumRoleLength,
            nameof(Roles));

        return new CurrentActor(
            Kind,
            Bounded(SubjectId, nameof(SubjectId)),
            Bounded(UserName, nameof(UserName)),
            Bounded(DisplayName, nameof(DisplayName)),
            Bounded(Email, nameof(Email)),
            EmailVerified,
            Bounded(PhoneNumber, nameof(PhoneNumber)),
            PhoneNumberVerified,
            Bounded(SessionId, nameof(SessionId)),
            Bounded(ClientId, nameof(ClientId)),
            Bounded(Issuer, nameof(Issuer)),
            AuthenticatedAt,
            ExpiresAt,
            scopes,
            roles,
            IssuedAt,
            Claims(AdditionalClaims));
    }

    private static ReadOnlyDictionary<string, string> Claims(IDictionary<string, string> claims)
    {
        if (claims.Count > CurrentActor.MaximumAdditionalClaimCount)
        {
            throw new ArgumentException(
                $"{nameof(AdditionalClaims)} cannot contain more than {CurrentActor.MaximumAdditionalClaimCount} entries.",
                nameof(AdditionalClaims));
        }

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (type, value) in claims)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{nameof(AdditionalClaims)} entries need a type and a value.", nameof(AdditionalClaims));
            }

            copy[Bounded(type, nameof(AdditionalClaims))!] = Bounded(value, nameof(AdditionalClaims))!;
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static string? Bounded(string? value, string memberName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length > CurrentActor.MaximumMemberLength)
        {
            throw new ArgumentException(
                $"{memberName} cannot exceed {CurrentActor.MaximumMemberLength} characters.",
                memberName);
        }

        return value;
    }

    private static string[] Normalize(
        List<string> values,
        int maximumCount,
        int maximumLength,
        string memberName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(Math.Min(values.Count, maximumCount));
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.Length > maximumLength)
            {
                throw new ArgumentException(
                    $"{memberName} entries cannot exceed {maximumLength} characters.",
                    memberName);
            }

            if (!seen.Add(value))
            {
                continue;
            }

            if (result.Count == maximumCount)
            {
                throw new ArgumentException(
                    $"{memberName} cannot contain more than {maximumCount} distinct entries.",
                    memberName);
            }

            result.Add(value);
        }

        return [.. result];
    }
}
