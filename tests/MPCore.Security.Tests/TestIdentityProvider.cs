using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MPCore.Security.Tests;

/// <summary>
/// A bounded, fully in-process identity provider. It mints tokens with a locally generated key and
/// exposes the matching signing material directly, so no test ever reaches a real Keycloak, a real
/// realm, a real client id or a real network endpoint.
/// </summary>
internal sealed class TestIdentityProvider : IDisposable
{
    public const string Issuer = "https://identity.invalid/realms/mpcore-tests";
    public const string Audience = "mpcore-tests";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly JsonWebTokenHandler _handler = new();

    public TestIdentityProvider()
        : this(Issuer, "mpcore-local-test-key")
    {
    }

    /// <summary>A provider of its own issuer and key, for a host that accepts several issuers.</summary>
    public TestIdentityProvider(string issuer, string keyId)
    {
        IssuerName = issuer;
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = keyId };
        Configuration = new OpenIdConnectConfiguration { Issuer = issuer };
        Configuration.SigningKeys.Add(SigningKey);
    }

    /// <summary>The issuer this provider writes into its tokens.</summary>
    public string IssuerName { get; }

    public RsaSecurityKey SigningKey { get; }

    public OpenIdConnectConfiguration Configuration { get; }

    public string CreateToken(
        string subject = "8f2b3c1d-0000-4000-8000-000000000001",
        string? audience = null,
        IDictionary<string, object>? claims = null,
        DateTime? notBefore = null,
        DateTime? expires = null,
        string algorithm = SecurityAlgorithms.RsaSha256,
        string? issuer = null,
        string[]? audiences = null)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? IssuerName,
            Audience = audiences is null ? audience ?? Audience : null,
            NotBefore = notBefore ?? now.AddMinutes(-1),
            IssuedAt = now.AddMinutes(-1),
            Expires = expires ?? now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, algorithm),
            Claims = BuildClaims(subject, claims)
        };

        foreach (var each in audiences ?? [])
        {
            descriptor.Audiences.Add(each);
        }

        return _handler.CreateToken(descriptor);
    }

    /// <summary>
    /// Mints an <c>HS256</c> token keyed on the published RSA modulus: the classic algorithm-confusion
    /// attack that an asymmetric-only allowlist must refuse.
    /// </summary>
    public string CreateSymmetricConfusionToken(string? audience = null)
    {
        var publicModulus = _rsa.ExportParameters(false).Modulus!;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = IssuerName,
            Audience = audience ?? Audience,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(publicModulus) { KeyId = SigningKey.KeyId },
                SecurityAlgorithms.HmacSha256),
            Claims = BuildClaims("8f2b3c1d-0000-4000-8000-000000000002", null)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Mints an unsigned <c>alg=none</c> token, which must never be accepted.</summary>
    public static string CreateUnsignedToken(string? issuer = null, string? audience = null)
    {
        var now = DateTimeOffset.UtcNow;
        var header = Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Encode(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = issuer ?? Issuer,
            ["aud"] = audience ?? Audience,
            ["sub"] = "8f2b3c1d-0000-4000-8000-000000000003",
            ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds()
        }));
        return $"{header}.{payload}.";
    }

    /// <summary>
    /// Mints a structurally valid token signed by an RSA key the resource server has never seen. It
    /// carries the expected issuer and audience, so only signature validation can reject it.
    /// </summary>
    public string CreateTokenSignedByUnknownKey()
    {
        using var foreignKey = RSA.Create(2048);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(foreignKey) { KeyId = "mpcore-unknown-test-key" },
                SecurityAlgorithms.RsaSha256),
            Claims = BuildClaims("8f2b3c1d-0000-4000-8000-000000000004", null)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>
    /// Mints a correctly signed token with no <c>exp</c> claim at all, which a resource server that
    /// requires an expiration time must refuse.
    /// </summary>
    public string CreateTokenWithoutExpiry(string? audience = null)
    {
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = IssuerName,
            Audience = audience ?? Audience,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
            Claims = BuildClaims("8f2b3c1d-0000-4000-8000-000000000005", null)
        };

        return handler.CreateToken(descriptor);
    }

    public void Dispose() => _rsa.Dispose();

    private static Dictionary<string, object> BuildClaims(
        string subject,
        IDictionary<string, object>? claims)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal) { ["sub"] = subject };
        if (claims is null)
        {
            return result;
        }

        foreach (var pair in claims)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static string Encode(string value) =>
        Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(value));
}
