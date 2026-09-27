using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MPCore.Security.AspNetCore;

/// <summary>
/// Fails host startup when the bearer configuration cannot be trusted. Every check is a documented
/// ADR-007 guarantee, so a misconfigured host never reaches the first request.
/// </summary>
internal sealed class MPCoreBearerOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<MPCoreBearerOptions>
{
    public ValidateOptionsResult Validate(string? name, MPCoreBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        var isDevelopment = environment.IsDevelopment();
        if (!options.RequireHttpsMetadata && !isDevelopment)
        {
            failures.Add(
                "Security:RequireHttpsMetadata may be false only in the Development environment.");
        }

        if (string.IsNullOrWhiteSpace(options.Authority))
        {
            failures.Add("Security:Authority is required and has no default.");
        }
        else if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority))
        {
            failures.Add("Security:Authority must be an absolute URI.");
        }
        else if (!string.Equals(authority.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
                 !(isDevelopment && !options.RequireHttpsMetadata &&
                   string.Equals(authority.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
        {
            failures.Add(
                "Security:Authority must use https. A cleartext authority is accepted only in the " +
                "Development environment together with Security:RequireHttpsMetadata=false.");
        }

        if (string.IsNullOrWhiteSpace(options.ResolvedIssuer))
        {
            failures.Add("Security:ValidIssuer must be non-empty; it defaults to Security:Authority.");
        }

        if (options.ValidAudiences.Count == 0)
        {
            failures.Add(
                "Security:ValidAudiences is required and must contain at least one audience that " +
                "identifies this resource server.");
        }

        if (options.ValidAudiences.Any(static audience =>
                string.Equals(
                    audience?.Trim(),
                    MPCoreBearerOptions.RejectedDefaultAudience,
                    StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add(
                "The identity-provider default 'account' audience does not identify this resource " +
                "server and is rejected. Configure a dedicated audience.");
        }

        if (options.ValidAudiences.Any(static audience => string.IsNullOrWhiteSpace(audience)))
        {
            failures.Add("Security:ValidAudiences cannot contain empty entries.");
        }

        if (options.ValidAlgorithms.Count == 0)
        {
            failures.Add("Security:ValidAlgorithms must contain at least one asymmetric algorithm.");
        }

        var rejected = options.ValidAlgorithms
            .Where(algorithm => !MPCoreBearerOptions.AsymmetricAlgorithms.Contains(
                algorithm ?? string.Empty,
                StringComparer.OrdinalIgnoreCase))
            .Select(static algorithm => string.IsNullOrWhiteSpace(algorithm) ? "(empty)" : algorithm)
            .ToArray();
        if (rejected.Length > 0)
        {
            failures.Add(
                "Security:ValidAlgorithms accepts asymmetric algorithms only (RS/PS/ES). " +
                $"Rejected: {string.Join(", ", rejected)}.");
        }

        if (options.ClockSkew < TimeSpan.Zero || options.ClockSkew > TimeSpan.FromMinutes(5))
        {
            failures.Add("Security:ClockSkew must be between zero and five minutes.");
        }

        if (string.IsNullOrWhiteSpace(options.AuthenticationScheme))
        {
            failures.Add("Security:AuthenticationScheme must be non-empty.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
