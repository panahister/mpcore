using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MPCore.Resilience.Http;

/// <summary>
/// Where the identity provider's introspection endpoint is, and how this backend authenticates to it (RFC 7662).
/// </summary>
/// <remarks>
/// No answer is cached by default: before a high-risk change the question goes to the provider every time,
/// because a cached answer can outlive a revocation (RFC 7662, section 4). The secret comes from configuration
/// outside the repository, never from code.
/// </remarks>
public sealed class TokenIntrospectionOptions
{
    /// <summary>The issuer; the endpoint is read from its discovery document. Not needed with <see cref="IntrospectionEndpoint"/>.</summary>
    public Uri? Authority { get; set; }

    /// <summary>The introspection endpoint. When set, no discovery document is read.</summary>
    public Uri? IntrospectionEndpoint { get; set; }

    /// <summary>The client identifier this backend authenticates with (RFC 7662, section 2.1).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The client secret. From user secrets or the environment; never in a committed file.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Whether the provider must be reached over HTTPS. True by default; a developer's machine turns it off.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>How long one question may take before the answer counts as inactive. Five seconds by default.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest time an active answer is kept, and never past the token's <c>exp</c>. Zero by default: no cache.
    /// An inactive answer is never kept.
    /// </summary>
    public TimeSpan MaximumCacheDuration { get; set; } = TimeSpan.Zero;

    /// <summary>What a log may show of these options: never the secret.</summary>
    public override string ToString() =>
        $"{nameof(TokenIntrospectionOptions)} {{ ClientId = {ClientId}, Authority = {Authority}, IntrospectionEndpoint = {IntrospectionEndpoint} }}";

    internal IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            yield return "ClientId is required: the introspection endpoint authenticates its caller.";
        }

        if (string.IsNullOrWhiteSpace(ClientSecret))
        {
            yield return "ClientSecret is required. Set it in user secrets or the environment.";
        }

        if (IntrospectionEndpoint is null && Authority is null)
        {
            yield return "Authority or IntrospectionEndpoint is required.";
        }

        foreach (var address in new[] { IntrospectionEndpoint, Authority })
        {
            if (address is null)
            {
                continue;
            }

            if (!address.IsAbsoluteUri)
            {
                yield return $"'{address}' must be an absolute address.";
            }
            else if (RequireHttps && address.Scheme != Uri.UriSchemeHttps)
            {
                yield return $"'{address}' must use HTTPS. RequireHttps may be turned off on a developer's machine only.";
            }
        }

        if (Timeout <= TimeSpan.Zero)
        {
            yield return "Timeout must be positive.";
        }

        if (MaximumCacheDuration < TimeSpan.Zero)
        {
            yield return "MaximumCacheDuration must not be negative.";
        }
    }
}

/// <summary>What the identity provider said about a token. Never the token itself.</summary>
public sealed record TokenIntrospectionResult
{
    /// <summary>The answer for anything but an active token: an error, a timeout, an unreadable answer, an inactive token.</summary>
    public static TokenIntrospectionResult Inactive { get; } = new();

    /// <summary>Gets a value indicating whether the provider answered <c>"active": true</c> for a token that has not expired.</summary>
    public bool Active { get; init; }

    /// <summary>Gets the subject (<c>sub</c>).</summary>
    public string? Subject { get; init; }

    /// <summary>Gets the session (<c>sid</c>).</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the client the token was issued to (<c>client_id</c>).</summary>
    public string? ClientId { get; init; }

    /// <summary>Gets the expiry (<c>exp</c>).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Asks the identity provider whether a token is still active (RFC 7662).</summary>
public interface ITokenIntrospector
{
    /// <summary>
    /// Asks whether the token is active. Never throws for an answer it cannot use: those are
    /// <see cref="TokenIntrospectionResult.Inactive"/>. Only the caller's own cancellation is thrown.
    /// </summary>
    /// <param name="token">The token to ask about.</param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    Task<TokenIntrospectionResult> IntrospectAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>Registration of the token-introspection client.</summary>
public static class TokenIntrospection
{
    /// <summary>
    /// The name of the client that talks to the introspection endpoint. Configure it like any other named
    /// client, for example to trust a private certificate authority.
    /// </summary>
    public const string ClientName = "MPCore.TokenIntrospection";

    /// <summary>Registers <see cref="ITokenIntrospector"/>; the options are validated at startup.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Where the endpoint is, who this backend is, and whether answers are cached.</param>
    public static IServiceCollection AddMPCoreTokenIntrospection(this IServiceCollection services, Action<TokenIntrospectionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<TokenIntrospectionOptions>().Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TokenIntrospectionOptions>, Explain>());
        services.AddHttpClient(ClientName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITokenIntrospector, TokenIntrospector>();
        return services;
    }

    /// <summary>Says what is wrong, one line per problem, and never prints a value.</summary>
    private sealed class Explain : IValidateOptions<TokenIntrospectionOptions>
    {
        public ValidateOptionsResult Validate(string? name, TokenIntrospectionOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            var problems = options.Problems().Select(static problem => $"Token introspection: {problem}").ToList();
            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }
    }
}

/// <summary>
/// Posts the token to the introspection endpoint with the client's own credentials, and fails closed.
/// </summary>
internal sealed class TokenIntrospector(
    IOptions<TokenIntrospectionOptions> options,
    IHttpClientFactory clients,
    TimeProvider time,
    ILogger<TokenIntrospector> logger) : ITokenIntrospector
{
    private const int MaximumCachedAnswers = 10_000;
    private readonly ConcurrentDictionary<string, (TokenIntrospectionResult Result, DateTimeOffset Until)> _cache = new(StringComparer.Ordinal);
    private Uri? _endpoint;

    public async Task<TokenIntrospectionResult> IntrospectAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return TokenIntrospectionResult.Inactive;
        }

        var settings = options.Value;
        var caching = settings.MaximumCacheDuration > TimeSpan.Zero;

        // The cache is keyed by a hash of the token, so the cache never holds a token.
        var key = caching ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) : string.Empty;
        if (caching && _cache.TryGetValue(key, out var cached))
        {
            if (time.GetUtcNow() < cached.Until)
            {
                return cached.Result;
            }

            _cache.TryRemove(key, out _);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        TokenIntrospectionResult result;
        try
        {
            result = await AskAsync(settings, token, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Token introspection did not answer within {Timeout}; the token counts as inactive.", settings.Timeout);
            return TokenIntrospectionResult.Inactive;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Token introspection failed with {ExceptionType}; the token counts as inactive.", exception.GetType().Name);
            return TokenIntrospectionResult.Inactive;
        }

        if (caching && result.Active)
        {
            Remember(key, result, settings.MaximumCacheDuration);
        }

        return result;
    }

    private async Task<TokenIntrospectionResult> AskAsync(TokenIntrospectionOptions settings, string token, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(TokenIntrospection.ClientName);
        var endpoint = _endpoint ?? settings.IntrospectionEndpoint ?? await DiscoverAsync(client, settings, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            return TokenIntrospectionResult.Inactive;
        }

        if (settings.RequireHttps && endpoint.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogWarning("The introspection endpoint is not HTTPS; no token is sent to it, and the token counts as inactive.");
            return TokenIntrospectionResult.Inactive;
        }

        _endpoint = endpoint;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent([new("token", token), new("token_type_hint", "access_token")])
        };

        // The client authenticates itself (RFC 7662, section 2.1) in the Authorization header (RFC 6749,
        // section 2.3.1), which a proxy is less likely to log than a body.
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(settings.ClientId)}:{Uri.EscapeDataString(settings.ClientSecret)}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Token introspection answered HTTP {StatusCode}; the token counts as inactive.", (int)response.StatusCode);
            return TokenIntrospectionResult.Inactive;
        }

        using var answer = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        if (answer is null || answer.RootElement.ValueKind != JsonValueKind.Object)
        {
            logger.LogWarning("Token introspection answered with something that is not a JSON object; the token counts as inactive.");
            return TokenIntrospectionResult.Inactive;
        }

        var root = answer.RootElement;
        if (!root.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True)
        {
            return TokenIntrospectionResult.Inactive;
        }

        var expiresAt = root.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds) && seconds is > 0 and < 253402300800
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : (DateTimeOffset?)null;
        if (expiresAt is { } expiry && expiry <= time.GetUtcNow())
        {
            // Active by the provider's word and expired by its own claim: the stricter wins.
            return TokenIntrospectionResult.Inactive;
        }

        return new TokenIntrospectionResult
        {
            Active = true,
            Subject = Text(root, "sub"),
            SessionId = Text(root, "sid"),
            ClientId = Text(root, "client_id"),
            ExpiresAt = expiresAt
        };
    }

    private async Task<Uri?> DiscoverAsync(HttpClient client, TokenIntrospectionOptions settings, CancellationToken cancellationToken)
    {
        var document = new Uri(settings.Authority!.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration");
        using var response = await client.GetAsync(document, cancellationToken).ConfigureAwait(false);
        using var answer = response.IsSuccessStatusCode ? await ReadAsync(response, cancellationToken).ConfigureAwait(false) : null;
        if (answer is { RootElement.ValueKind: JsonValueKind.Object } &&
            answer.RootElement.TryGetProperty("introspection_endpoint", out var endpoint) &&
            Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var address))
        {
            return address;
        }

        logger.LogWarning("The discovery document names no introspection endpoint; the token counts as inactive.");
        return null;
    }

    private void Remember(string key, TokenIntrospectionResult result, TimeSpan maximum)
    {
        var now = time.GetUtcNow();
        var until = now + maximum;
        if (result.ExpiresAt is { } expiry && expiry < until)
        {
            until = expiry;
        }

        if (until <= now)
        {
            return;
        }

        if (_cache.Count >= MaximumCachedAnswers)
        {
            foreach (var stale in _cache.Where(entry => entry.Value.Until <= now).Select(static entry => entry.Key).ToList())
            {
                _cache.TryRemove(stale, out _);
            }

            if (_cache.Count >= MaximumCachedAnswers)
            {
                return;
            }
        }

        _cache[key] = (result, until);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<JsonDocument?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
