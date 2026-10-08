using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MPCore.Security.AspNetCore;

/// <summary>
/// Maps a validated <see cref="ClaimsPrincipal"/> to the transport-free <see cref="CurrentActor"/>.
/// Nothing outside the configured mapping is copied, so no raw token, claim bag or provider type
/// can reach application code.
/// </summary>
internal sealed class ClaimsPrincipalActorMapper(
    IOptions<ActorClaimMappingOptions> options,
    ActorRoleExtractor roleExtractor)
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 8 };

    public CurrentActor Map(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not { IsAuthenticated: true })
        {
            return CurrentActor.Anonymous;
        }

        var mapping = options.Value;
        var subject = First(principal, mapping.SubjectClaim);
        if (string.IsNullOrWhiteSpace(subject))
        {
            // A validated token without a stable subject cannot produce a usable actor.
            return CurrentActor.Anonymous;
        }

        var userName = First(principal, mapping.UserNameClaim);
        var serviceClientId = First(principal, mapping.ServiceAccountClientIdClaim);
        // A service account carries a client id and either no user name or the provider's
        // service-account naming convention (Keycloak: service-account-<client>).
        var serviceByName = mapping.ServiceAccountUserNamePrefix is { Length: > 0 } prefix
                            && userName is not null
                            && userName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        var kind = !string.IsNullOrWhiteSpace(serviceClientId) && (string.IsNullOrWhiteSpace(userName) || serviceByName)
            ? ActorKind.Service
            : ActorKind.User;

        var builder = new CurrentActorBuilder(kind)
        {
            SubjectId = Truncate(subject),
            UserName = Truncate(userName),
            DisplayName = Truncate(First(principal, mapping.DisplayNameClaim)),
            Email = Truncate(First(principal, mapping.EmailClaim)),
            EmailVerified = Boolean(principal, mapping.EmailVerifiedClaim),
            PhoneNumber = Truncate(First(principal, mapping.PhoneNumberClaim)),
            PhoneNumberVerified = Boolean(principal, mapping.PhoneNumberVerifiedClaim),
            SessionId = Truncate(First(principal, mapping.SessionIdClaim)),
            ClientId = Truncate(First(principal, mapping.ClientIdClaim) ?? serviceClientId),
            Issuer = Truncate(First(principal, "iss")),
            // When the person authenticated, and only that (OpenID Connect Core 1.0, section 2). A token
            // refreshed later carries a newer iat; reading iat here made it look freshly authenticated.
            AuthenticatedAt = UnixTime(principal, "auth_time"),
            IssuedAt = UnixTime(principal, "iat"),
            ExpiresAt = UnixTime(principal, "exp")
        };

        foreach (var type in mapping.AdditionalClaims.Take(CurrentActor.MaximumAdditionalClaimCount))
        {
            if (string.IsNullOrWhiteSpace(type) || type.Length > CurrentActor.MaximumMemberLength)
            {
                continue;
            }

            // Exactly one value, of a bounded length: an ambiguous or over-long claim is left out, never cut.
            var values = principal.FindAll(type).Take(2).ToArray();
            if (values is [{ Value: { Length: > 0 and <= CurrentActor.MaximumMemberLength } value }] && !string.IsNullOrWhiteSpace(value))
            {
                builder.AdditionalClaims[type] = value;
            }
        }

        foreach (var scope in ReadScopes(principal, mapping.ScopeClaim))
        {
            builder.AddScope(scope);
        }

        foreach (var role in roleExtractor.Extract(principal))
        {
            builder.AddRole(role);
        }

        return builder.Build();
    }

    private static IEnumerable<string> ReadScopes(ClaimsPrincipal principal, string? claimType)
    {
        if (string.IsNullOrWhiteSpace(claimType))
        {
            yield break;
        }

        var emitted = 0;
        foreach (var claim in principal.FindAll(claimType))
        {
            foreach (var scope in SplitScopeValue(claim.Value))
            {
                if (emitted == CurrentActor.MaximumScopeCount)
                {
                    // The count cap is reached; nothing further can be admitted.
                    yield break;
                }

                if (scope.Length > CurrentActor.MaximumScopeLength)
                {
                    // Only the over-long entry is dropped, matching ActorRoleExtractor.Add. Aborting
                    // here would silently discard every remaining scope and cause a spurious 403.
                    continue;
                }

                emitted++;
                yield return scope;
            }
        }
    }

    private static IEnumerable<string> SplitScopeValue(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length > 0 && trimmed[0] == '[')
        {
            string[] parsed;
            try
            {
                using var document = JsonDocument.Parse(value, DocumentOptions);
                parsed = document.RootElement.ValueKind == JsonValueKind.Array
                    ? [.. document.RootElement.EnumerateArray()
                        .Where(static element => element.ValueKind == JsonValueKind.String)
                        .Select(static element => element.GetString()!)]
                    : [];
            }
            catch (JsonException)
            {
                parsed = [];
            }

            return parsed;
        }

        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? First(ClaimsPrincipal principal, string? claimType) =>
        string.IsNullOrWhiteSpace(claimType) ? null : principal.FindFirst(claimType)?.Value;

    private static bool Boolean(ClaimsPrincipal principal, string? claimType) =>
        bool.TryParse(First(principal, claimType), out var parsed) && parsed;

    private static DateTimeOffset? UnixTime(ClaimsPrincipal principal, string claimType) =>
        long.TryParse(
            First(principal, claimType),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var seconds) &&
        seconds is > 0 and < 253402300800
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static string? Truncate(string? value) =>
        value is { Length: > CurrentActor.MaximumMemberLength }
            ? value[..CurrentActor.MaximumMemberLength]
            : value;
}
