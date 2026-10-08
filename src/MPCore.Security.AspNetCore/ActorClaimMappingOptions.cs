namespace MPCore.Security.AspNetCore;

/// <summary>
/// Determines how provider claims become a <see cref="CurrentActor"/>. Every default is a generic
/// OIDC or JWT claim name; no realm, client id, URL or secret appears here.
/// </summary>
public sealed class ActorClaimMappingOptions
{
    /// <summary>The largest number of role sources a host may configure.</summary>
    public const int MaximumRoleSources = 8;

    /// <summary>Gets or sets the stable subject claim. Defaults to <c>sub</c>.</summary>
    public string SubjectClaim { get; set; } = "sub";

    /// <summary>Gets or sets the user-name claim. Defaults to <c>preferred_username</c>.</summary>
    public string? UserNameClaim { get; set; } = "preferred_username";

    /// <summary>Gets or sets the display-name claim. Defaults to <c>name</c>.</summary>
    public string? DisplayNameClaim { get; set; } = "name";

    /// <summary>Gets or sets the email claim. Defaults to <c>email</c>.</summary>
    public string? EmailClaim { get; set; } = "email";

    /// <summary>Gets or sets the email-verified claim. Defaults to <c>email_verified</c>.</summary>
    public string? EmailVerifiedClaim { get; set; } = "email_verified";

    /// <summary>Gets or sets the phone-number claim. Defaults to <c>phone_number</c>.</summary>
    public string? PhoneNumberClaim { get; set; } = "phone_number";

    /// <summary>Gets or sets the phone-verified claim. Defaults to <c>phone_number_verified</c>.</summary>
    public string? PhoneNumberVerifiedClaim { get; set; } = "phone_number_verified";

    /// <summary>Gets or sets the session claim. Defaults to <c>sid</c>.</summary>
    public string? SessionIdClaim { get; set; } = "sid";

    /// <summary>Gets or sets the authorized-party claim. Defaults to <c>azp</c>.</summary>
    public string? ClientIdClaim { get; set; } = "azp";

    /// <summary>Gets or sets the space-delimited scope claim per RFC 8693. Defaults to <c>scope</c>.</summary>
    public string? ScopeClaim { get; set; } = "scope";

    /// <summary>
    /// Gets the allowlist of further claim types exposed on <see cref="CurrentActor.AdditionalClaims"/>, for a
    /// claim MP Core does not map, such as <c>acr</c>. At most <see cref="CurrentActor.MaximumAdditionalClaimCount"/>.
    /// A claim is exposed only when the token carries it exactly once, with a value of at most
    /// <see cref="CurrentActor.MaximumMemberLength"/> characters; anything else is left out, never truncated.
    /// </summary>
    public ISet<string> AdditionalClaims { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Gets or sets the claim type used for synthesized normalized roles.</summary>
    public string RoleClaimType { get; set; } = "role";

    /// <summary>
    /// Gets or sets the claim identifying a machine caller. The actor is a <see cref="ActorKind.Service"/>
    /// when this claim is present and either the token has no user name or its user name starts with
    /// <see cref="ServiceAccountUserNamePrefix"/>. Otherwise it is a <see cref="ActorKind.User"/>.
    /// </summary>
    /// <remarks>
    /// Keycloak 25 and later write <c>client_id</c> only for a client that has the <c>service_account</c>
    /// client scope. A client created in the console has it; a client imported from a realm file has it only
    /// when the file lists it in <c>defaultClientScopes</c>. Without it a service's token maps to a user.
    /// </remarks>
    public string? ServiceAccountClientIdClaim { get; set; } = "client_id";

    /// <summary>
    /// User-name prefix that marks a service account even though a user name is present. Keycloak
    /// issues <c>service-account-&lt;client&gt;</c>; null disables the convention.
    /// </summary>
    public string? ServiceAccountUserNamePrefix { get; set; } = "service-account-";

    /// <summary>
    /// Keycloak-shaped mapping: realm roles, client roles prefixed by client, <c>preferred_username</c>,
    /// <c>azp</c>, and the service-account user-name convention. These are also the defaults.
    /// </summary>
    public ActorClaimMappingOptions UseKeycloakDefaults()
    {
        UserNameClaim = "preferred_username";
        ClientIdClaim = "azp";
        ServiceAccountClientIdClaim = "client_id";
        ServiceAccountUserNamePrefix = "service-account-";
        RoleClaimType = "role";
        RoleSources.Clear();
        RoleSources.Add(new RoleClaimSource { Path = "realm_access.roles" });
        RoleSources.Add(new RoleClaimSource { Path = "resource_access.*.roles", Prefix = RolePrefixMode.WildcardSegment });
        return this;
    }

    /// <summary>
    /// Plain OIDC mapping for providers without Keycloak's nested claims: roles from a flat
    /// <c>roles</c> claim, no service-account naming convention.
    /// </summary>
    public ActorClaimMappingOptions UseGenericOidc()
    {
        UserNameClaim = "preferred_username";
        ClientIdClaim = "azp";
        ServiceAccountClientIdClaim = "client_id";
        ServiceAccountUserNamePrefix = null;
        RoleClaimType = "roles";
        RoleSources.Clear();
        RoleSources.Add(new RoleClaimSource { Path = "roles" });
        return this;
    }

    /// <summary>
    /// Gets the role sources. Defaults cover realm roles and per-client roles for any OIDC provider
    /// that nests roles in JSON, including Keycloak, without naming a realm or a client.
    /// </summary>
    public IList<RoleClaimSource> RoleSources { get; } =
    [
        new RoleClaimSource { Path = "realm_access.roles" },
        new RoleClaimSource { Path = "resource_access.*.roles", Prefix = RolePrefixMode.WildcardSegment }
    ];
}

/// <summary>Selects how an extracted role is prefixed before it is normalized.</summary>
public enum RolePrefixMode
{
    /// <summary>The role is used verbatim.</summary>
    None = 0,

    /// <summary>The matched wildcard segment is prefixed, producing <c>&lt;segment&gt;:&lt;role&gt;</c>.</summary>
    WildcardSegment = 1,

    /// <summary>A fixed <see cref="RoleClaimSource.LiteralPrefix"/> is prefixed.</summary>
    Literal = 2
}

/// <summary>
/// One bounded, dotted JSON path into a provider claim, with an optional single <c>*</c> segment.
/// </summary>
public sealed class RoleClaimSource
{
    /// <summary>The largest number of dotted segments a role path may contain.</summary>
    public const int MaximumSegments = 8;

    /// <summary>Gets or sets the dotted path. The first segment is the claim type.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the prefix strategy.</summary>
    public RolePrefixMode Prefix { get; set; } = RolePrefixMode.None;

    /// <summary>Gets or sets the literal prefix used when <see cref="Prefix"/> is
    /// <see cref="RolePrefixMode.Literal"/>.</summary>
    public string? LiteralPrefix { get; set; }

    /// <summary>Gets or sets the separator placed between a prefix and a role.</summary>
    public string PrefixSeparator { get; set; } = ":";
}
