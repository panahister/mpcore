using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MPCore.Security.AspNetCore;

/// <summary>
/// The deny-list of gateway identity headers that must never influence a MP Core authorization
/// decision. Identity originates exclusively from a validated bearer token.
/// </summary>
public sealed class ForwardedIdentityHeaderOptions
{
    /// <summary>The default deny-list. Names are matched case-insensitively.</summary>
    public static readonly IReadOnlyList<string> DefaultDeniedHeaders =
    [
        "x-user-id",
        "x-user-name",
        "x-userinfo",
        "x-forwarded-user",
        "x-authenticated-userid",
        "x-authenticated-scope",
        "x-consumer-id",
        "x-consumer-username",
        "x-consumer-custom-id",
        "x-credential-identifier"
    ];

    /// <summary>Gets the mutable deny-list, seeded with <see cref="DefaultDeniedHeaders"/>.</summary>
    public IList<string> DeniedHeaders { get; } = [.. DefaultDeniedHeaders];
}

/// <summary>
/// Strips forwarded identity headers before authentication runs, so no downstream component can
/// construct an actor from a header a caller controls.
/// </summary>
internal sealed class ForwardedIdentityHeaderGuard(
    RequestDelegate next,
    IOptions<ForwardedIdentityHeaderOptions> options,
    ILogger<ForwardedIdentityHeaderGuard> logger)
{
    private readonly string[] _denied = [.. options.Value.DeniedHeaders
        .Where(static header => !string.IsNullOrWhiteSpace(header))
        .Select(static header => header.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)];

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<string>? stripped = null;
        foreach (var header in _denied)
        {
            if (context.Request.Headers.Remove(header))
            {
                (stripped ??= []).Add(header);
            }
        }

        if (stripped is not null)
        {
            logger.LogWarning(
                "Stripped {StrippedHeaderCount} forwarded identity header(s) {StrippedHeaderNames} from an inbound request; identity is taken only from a validated bearer token.",
                stripped.Count,
                string.Join(", ", stripped));
        }

        return next(context);
    }
}
