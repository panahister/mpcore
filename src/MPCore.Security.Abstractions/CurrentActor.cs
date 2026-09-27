using System.Collections.ObjectModel;

namespace MPCore.Security;

/// <summary>
/// An immutable, transport-free description of the caller behind the current operation.
/// </summary>
/// <remarks>
/// <para>
/// Instances are produced only by <see cref="CurrentActorBuilder"/>, which validates and freezes
/// every member. The model deliberately exposes no raw token, no claim bag, no principal and no
/// identity-provider type.
/// </para>
/// <para>
/// <see cref="SubjectId"/> is the only stable subject identifier. <see cref="UserName"/>,
/// <see cref="Email"/> and <see cref="PhoneNumber"/> are mutable in the identity provider and must
/// never be persisted as a key or used for an authorization decision.
/// </para>
/// </remarks>
public sealed record CurrentActor
{
    /// <summary>Maximum length of every single-valued string member.</summary>
    public const int MaximumMemberLength = 256;

    /// <summary>Maximum number of scopes carried by one actor.</summary>
    public const int MaximumScopeCount = 128;

    /// <summary>Maximum number of roles carried by one actor.</summary>
    public const int MaximumRoleCount = 256;

    /// <summary>Maximum length of a single scope entry.</summary>
    public const int MaximumScopeLength = 128;

    /// <summary>Maximum length of a single role entry.</summary>
    public const int MaximumRoleLength = 128;

    private readonly HashSet<string> _scopeIndex;
    private readonly HashSet<string> _roleIndex;

    internal CurrentActor(
        ActorKind kind,
        string? subjectId,
        string? userName,
        string? displayName,
        string? email,
        bool emailVerified,
        string? phoneNumber,
        bool phoneNumberVerified,
        string? sessionId,
        string? clientId,
        string? issuer,
        DateTimeOffset? authenticatedAt,
        DateTimeOffset? expiresAt,
        string[] scopes,
        string[] roles)
    {
        Kind = kind;
        SubjectId = subjectId;
        UserName = userName;
        DisplayName = displayName;
        Email = email;
        EmailVerified = emailVerified;
        PhoneNumber = phoneNumber;
        PhoneNumberVerified = phoneNumberVerified;
        SessionId = sessionId;
        ClientId = clientId;
        Issuer = issuer;
        AuthenticatedAt = authenticatedAt;
        ExpiresAt = expiresAt;
        Scopes = new ReadOnlyCollection<string>(scopes);
        Roles = new ReadOnlyCollection<string>(roles);
        _scopeIndex = new HashSet<string>(scopes, StringComparer.Ordinal);
        _roleIndex = new HashSet<string>(roles, StringComparer.Ordinal);
    }

    /// <summary>Gets the shared anonymous actor.</summary>
    public static CurrentActor Anonymous { get; } = new CurrentActorBuilder(ActorKind.Anonymous).Build();

    /// <summary>The system acting on its own behalf, named after the job or process. Not a token and not a user.</summary>
    public static CurrentActor ForSystem(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        return new CurrentActorBuilder(ActorKind.System) { SubjectId = "system:" + processName, UserName = processName, DisplayName = processName }.Build();
    }

    /// <summary>Gets the kind of caller.</summary>
    public ActorKind Kind { get; }

    /// <summary>Gets a value indicating whether a validated credential produced this actor.</summary>
    public bool IsAuthenticated => Kind != ActorKind.Anonymous;

    /// <summary>Gets the stable subject identifier. Always present when authenticated.</summary>
    public string? SubjectId { get; }

    /// <summary>Gets the mutable preferred user name. Never a persistence key.</summary>
    public string? UserName { get; }

    /// <summary>Gets the display name.</summary>
    public string? DisplayName { get; }

    /// <summary>Gets the mutable email address. Never a persistence key.</summary>
    public string? Email { get; }

    /// <summary>Gets a value indicating whether the provider asserted a verified email.</summary>
    public bool EmailVerified { get; }

    /// <summary>Gets the mutable phone number. Never a persistence key.</summary>
    public string? PhoneNumber { get; }

    /// <summary>Gets a value indicating whether the provider asserted a verified phone number.</summary>
    public bool PhoneNumberVerified { get; }

    /// <summary>Gets the provider session identifier.</summary>
    public string? SessionId { get; }

    /// <summary>Gets the authorized party (client) identifier.</summary>
    public string? ClientId { get; }

    /// <summary>Gets the token issuer.</summary>
    public string? Issuer { get; }

    /// <summary>Gets the authentication instant asserted by the provider.</summary>
    public DateTimeOffset? AuthenticatedAt { get; }

    /// <summary>Gets the credential expiry asserted by the provider.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Gets the ordinal, de-duplicated OAuth scopes.</summary>
    public IReadOnlyCollection<string> Scopes { get; }

    /// <summary>Gets the ordinal, de-duplicated normalized roles.</summary>
    public IReadOnlyCollection<string> Roles { get; }

    /// <summary>Determines whether the actor carries the supplied scope, compared ordinally.</summary>
    public bool HasScope(string scope) => !string.IsNullOrEmpty(scope) && _scopeIndex.Contains(scope);

    /// <summary>Determines whether the actor carries the supplied role, compared ordinally.</summary>
    public bool HasRole(string role) => !string.IsNullOrEmpty(role) && _roleIndex.Contains(role);

    /// <inheritdoc />
    public bool Equals(CurrentActor? other) =>
        other is not null &&
        Kind == other.Kind &&
        string.Equals(SubjectId, other.SubjectId, StringComparison.Ordinal) &&
        string.Equals(UserName, other.UserName, StringComparison.Ordinal) &&
        string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) &&
        string.Equals(Email, other.Email, StringComparison.Ordinal) &&
        EmailVerified == other.EmailVerified &&
        string.Equals(PhoneNumber, other.PhoneNumber, StringComparison.Ordinal) &&
        PhoneNumberVerified == other.PhoneNumberVerified &&
        string.Equals(SessionId, other.SessionId, StringComparison.Ordinal) &&
        string.Equals(ClientId, other.ClientId, StringComparison.Ordinal) &&
        string.Equals(Issuer, other.Issuer, StringComparison.Ordinal) &&
        AuthenticatedAt == other.AuthenticatedAt &&
        ExpiresAt == other.ExpiresAt &&
        Scopes.SequenceEqual(other.Scopes, StringComparer.Ordinal) &&
        Roles.SequenceEqual(other.Roles, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(SubjectId);
        hash.Add(UserName);
        hash.Add(DisplayName);
        hash.Add(Email);
        hash.Add(EmailVerified);
        hash.Add(PhoneNumber);
        hash.Add(PhoneNumberVerified);
        hash.Add(SessionId);
        hash.Add(ClientId);
        hash.Add(Issuer);
        hash.Add(AuthenticatedAt);
        hash.Add(ExpiresAt);
        hash.Add(Scopes.Count);
        hash.Add(Roles.Count);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Returns a bounded, non-identifying description. Never emits email, phone number, session id
    /// or any other member that could disclose personal data through a log sink.
    /// </summary>
    public override string ToString() => IsAuthenticated
        ? $"CurrentActor {{ Kind = {Kind}, SubjectId = {SubjectId}, Scopes = {Scopes.Count}, Roles = {Roles.Count} }}"
        : "CurrentActor { Kind = Anonymous }";
}
