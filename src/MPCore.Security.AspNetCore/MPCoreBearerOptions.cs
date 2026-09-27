namespace MPCore.Security.AspNetCore;

/// <summary>
/// Bearer-only resource-server configuration. The options carry no realm name, no client id, no
/// issuer default, no secret and no product role. Every value is supplied by the host.
/// </summary>
public sealed class MPCoreBearerOptions
{
    /// <summary>The asymmetric signature algorithms MP Core accepts.</summary>
    /// <remarks>
    /// <c>none</c> and every symmetric <c>HS*</c> algorithm are deliberately absent so a token
    /// signed with the public JWKS material, or with no signature at all, can never be accepted.
    /// </remarks>
    public static readonly IReadOnlyList<string> AsymmetricAlgorithms =
    [
        "RS256", "RS384", "RS512",
        "PS256", "PS384", "PS512",
        "ES256", "ES384", "ES512"
    ];

    /// <summary>The Keycloak default audience that never identifies this resource server.</summary>
    public const string RejectedDefaultAudience = "account";

    /// <summary>Gets or sets the OIDC authority. Required, absolute, and <c>https</c>.</summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether identity-provider metadata must be retrieved over
    /// HTTPS. May be <see langword="false"/> only in the Development environment.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Gets or sets the expected issuer. Defaults to <see cref="Authority"/>.</summary>
    public string? ValidIssuer { get; set; }

    /// <summary>Gets the required, non-empty set of audiences that identify this resource server.</summary>
    public IList<string> ValidAudiences { get; } = [];

    /// <summary>Gets the accepted signature algorithms. Defaults to <see cref="AsymmetricAlgorithms"/>.</summary>
    public IList<string> ValidAlgorithms { get; } = [.. AsymmetricAlgorithms];

    /// <summary>Gets or sets the accepted clock skew. Defaults to thirty seconds.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the authentication scheme name.</summary>
    public string AuthenticationScheme { get; set; } = "Bearer";

    /// <summary>Gets or sets the metadata address override. Left unset, OIDC discovery is used.</summary>
    public string? MetadataAddress { get; set; }

    internal string ResolvedIssuer => string.IsNullOrWhiteSpace(ValidIssuer) ? Authority! : ValidIssuer;
}
