using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MPCore.Resilience.Http;

/// <summary>How a service proves who it is when it calls another service.</summary>
/// <remarks>
/// The OAuth 2.0 client credentials grant (RFC 6749, section 4.4): the service presents its own client
/// identifier and secret to the identity provider and receives a token issued to the service, not to a
/// person. The secret comes from configuration outside the repository, never from code.
/// </remarks>
public sealed class ServiceIdentityOptions
{
    /// <summary>
    /// The issuer, for example <c>https://identity.example/realms/shop</c>. The token endpoint is read
    /// from its discovery document (OpenID Connect Discovery 1.0). Not needed when
    /// <see cref="TokenEndpoint"/> is set.
    /// </summary>
    public Uri? Authority { get; set; }

    /// <summary>The token endpoint. When set, no discovery document is read.</summary>
    public Uri? TokenEndpoint { get; set; }

    /// <summary>The client identifier of this service at the identity provider.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The client secret. From user secrets or the environment; never in a committed file.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>The scope to ask for, when the identity provider needs one.</summary>
    public string? Scope { get; set; }

    /// <summary>How long before its expiry a token is replaced. Thirty seconds by default.</summary>
    public TimeSpan RefreshBeforeExpiry { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether the identity provider and the called service must be reached over HTTPS. True by default:
    /// a secret and a token do not travel in cleartext. A developer's machine turns it off.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>What a log may show of these options: never the secret.</summary>
    public override string ToString() => $"{nameof(ServiceIdentityOptions)} {{ ClientId = {ClientId}, Authority = {Authority}, TokenEndpoint = {TokenEndpoint} }}";

    internal IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            yield return "ClientId is required.";
        }

        if (string.IsNullOrWhiteSpace(ClientSecret))
        {
            yield return "ClientSecret is required. Set it in user secrets or the environment.";
        }

        if (TokenEndpoint is null && Authority is null)
        {
            yield return "Authority or TokenEndpoint is required.";
        }

        foreach (var address in new[] { TokenEndpoint, Authority })
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

        if (RefreshBeforeExpiry < TimeSpan.Zero)
        {
            yield return "RefreshBeforeExpiry must not be negative.";
        }
    }
}

/// <summary>The identity provider did not issue a token to this service.</summary>
/// <remarks>
/// An <see cref="HttpRequestException"/>, so that a caller that treats a call that could not be made as
/// "the other service is unavailable" treats this the same way. The message names the client and the
/// provider's error code; it never contains the secret or a token.
/// </remarks>
public sealed class ServiceIdentityException : HttpRequestException
{
    /// <summary>Creates the exception.</summary>
    public ServiceIdentityException(string message, Exception? inner = null, HttpStatusCode? statusCode = null)
        : base(message, inner, statusCode)
    {
    }
}

/// <summary>The token a service presents when it calls another service.</summary>
public interface IServiceTokenSource
{
    /// <summary>The current token; a new one is asked for when there is none or it is about to expire.</summary>
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets a token that was refused, so that the next call asks for a new one.</summary>
    void Forget(string token);
}

/// <summary>
/// Asks the identity provider for a token with the client credentials grant, and keeps it until shortly
/// before it expires. One request at a time: callers that arrive together wait for the same answer.
/// </summary>
internal sealed class ClientCredentialsTokenSource(
    string name, IOptionsMonitor<ServiceIdentityOptions> options, IHttpClientFactory clients, TimeProvider time)
    : IServiceTokenSource, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _replaceAt;
    private Uri? _tokenEndpoint;

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (Current() is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current() is { } arrivedMeanwhile)
            {
                return arrivedMeanwhile;
            }

            var settings = options.Get(name);
            var (token, lifetime) = await RequestAsync(settings, cancellationToken).ConfigureAwait(false);
            _replaceAt = time.GetUtcNow() + lifetime - settings.RefreshBeforeExpiry;
            Volatile.Write(ref _token, token);
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Forget(string token) => Interlocked.CompareExchange(ref _token, null, token);

    public void Dispose() => _gate.Dispose();

    private string? Current() => Volatile.Read(ref _token) is { } token && time.GetUtcNow() < _replaceAt ? token : null;

    private async Task<(string Token, TimeSpan Lifetime)> RequestAsync(ServiceIdentityOptions settings, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(ServiceIdentity.TokenClientName);
        var endpoint = _tokenEndpoint ??= settings.TokenEndpoint ?? await DiscoverAsync(client, settings, cancellationToken).ConfigureAwait(false);

        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
        if (!string.IsNullOrWhiteSpace(settings.Scope))
        {
            form.Add(new("scope", settings.Scope));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        // The credentials travel in the Authorization header, which RFC 6749 section 2.3.1 asks every
        // provider to support, and not in the body, which a proxy is more likely to log.
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(settings.ClientId)}:{Uri.EscapeDataString(settings.ClientSecret)}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ServiceIdentityException($"The identity provider did not answer the service '{settings.ClientId}'.", exception);
        }

        using (response)
        {
            using var answer = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = answer is not null && answer.RootElement.TryGetProperty("error", out var code) ? code.GetString() : null;
                throw new ServiceIdentityException(
                    $"The identity provider refused the service '{settings.ClientId}': {error ?? "no error code"} (HTTP {(int)response.StatusCode}).",
                    statusCode: response.StatusCode);
            }

            if (answer is null
                || !answer.RootElement.TryGetProperty("access_token", out var token) || token.GetString() is not { Length: > 0 } value)
            {
                throw new ServiceIdentityException($"The identity provider answered the service '{settings.ClientId}' without a token.");
            }

            var seconds = answer.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetDouble(out var given) ? given : 60;
            return (value, TimeSpan.FromSeconds(seconds));
        }
    }

    private static async Task<Uri> DiscoverAsync(HttpClient client, ServiceIdentityOptions settings, CancellationToken cancellationToken)
    {
        var document = new Uri(settings.Authority!.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration");
        try
        {
            using var response = await client.GetAsync(document, cancellationToken).ConfigureAwait(false);
            using var answer = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && answer is not null
                && answer.RootElement.TryGetProperty("token_endpoint", out var endpoint)
                && Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out var address)
                && (!settings.RequireHttps || address.Scheme == Uri.UriSchemeHttps))
            {
                return address;
            }
        }
        catch (HttpRequestException exception)
        {
            throw new ServiceIdentityException($"The identity provider's discovery document could not be read: {document}.", exception);
        }

        throw new ServiceIdentityException($"The identity provider's discovery document names no usable token endpoint: {document}.");
    }

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

/// <summary>Puts the service's token on every request of one client.</summary>
internal sealed class ServiceTokenHandler(IServiceTokenSource tokens, bool requireHttps) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (requireHttps && request.RequestUri is { IsAbsoluteUri: true } address && address.Scheme != Uri.UriSchemeHttps)
        {
            throw new ServiceIdentityException($"The service's token is not sent over cleartext: {address.GetLeftPart(UriPartial.Authority)}.");
        }

        var token = await tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The token was refused: its key was rotated, or the client was changed. The answer goes back
            // as it is, and the next call asks for a new token.
            tokens.Forget(token);
        }

        return response;
    }
}

/// <summary>Registration of a service's identity on an outbound client.</summary>
public static class ServiceIdentity
{
    /// <summary>
    /// The name of the client that talks to the identity provider. Configure it like any other named
    /// client, for example to trust a private certificate authority.
    /// </summary>
    public const string TokenClientName = "MPCore.ServiceIdentity";

    /// <summary>
    /// Makes every request of this client carry a token issued to the service itself (OAuth 2.0 client
    /// credentials). Works on any client built by <see cref="IHttpClientFactory"/>: a REST client, and
    /// a gRPC client registered with <c>AddGrpcClient</c>.
    /// </summary>
    /// <remarks>
    /// Add it after the resilience handler, as <see cref="ResilientHttpClientExtensions.AddMPCoreResilientHttpClient"/>
    /// returns the builder: every attempt then carries the current token, and a retry after the token
    /// expired carries the new one.
    /// </remarks>
    /// <param name="builder">The client's builder.</param>
    /// <param name="configure">Who the service is, and where the identity provider is.</param>
    public static IHttpClientBuilder AddMPCoreServiceIdentity(this IHttpClientBuilder builder, Action<ServiceIdentityOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var name = builder.Name;
        builder.Services.AddOptions<ServiceIdentityOptions>(name).Configure(configure).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ServiceIdentityOptions>>(new Explain(name));
        builder.Services.AddHttpClient(TokenClientName);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddKeyedSingleton<IServiceTokenSource>(name, static (provider, key) => new ClientCredentialsTokenSource(
            (string)key!, provider.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>(),
            provider.GetRequiredService<IHttpClientFactory>(), provider.GetRequiredService<TimeProvider>()));
        return builder.AddHttpMessageHandler(provider => new ServiceTokenHandler(
            provider.GetRequiredKeyedService<IServiceTokenSource>(name),
            provider.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>().Get(name).RequireHttps));
    }

    /// <summary>Says what is wrong, one line per problem, and never prints a value.</summary>
    private sealed class Explain(string name) : IValidateOptions<ServiceIdentityOptions>
    {
        public ValidateOptionsResult Validate(string? optionsName, ServiceIdentityOptions options)
        {
            if (optionsName != name)
            {
                return ValidateOptionsResult.Skip;
            }

            var problems = options.Problems().Select(problem => $"Service identity of the client '{name}': {problem}").ToList();
            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }
    }
}
