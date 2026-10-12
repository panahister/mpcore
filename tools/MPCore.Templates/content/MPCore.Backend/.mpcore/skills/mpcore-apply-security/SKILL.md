---
name: mpcore-apply-security
description: Apply authorization and current-actor integration to an approved capability, and the rules of an identity-provider administration adapter. For MPCORE_ORGANIZATION.MPCORE_COMPONENT backends generated from MP Core MPCORE_VERSION.
---

# Apply security

Use when a capability needs to know who is calling, or must be restricted.

## Security invariants

- Work that runs outside a request — a scheduled job, a message consumer — opens
  `SystemActorScope.Enter("<job-name>")` so the actor is the named system, never anonymous. A
  service account (client credentials) is `ActorKind.Service`; branch on the kind, not on a name.

The host is a bearer-only OAuth 2.0 / OIDC resource server. Identity comes from the validated token
through `ICurrentActorAccessor`; a `CurrentActor` is built only by the framework's validating builder.

- Never trust a user id, tenant, role or subject taken from a request body, query string, route value
  or an arbitrary header. A forwarded-identity header guard exists precisely because such headers are
  client- and proxy-controlled.
- Roles reaching an authorization decision come only from the configured `RoleSources`. A top-level
  `role` claim in a token is discarded by design; it is MP Core's synthesized output, never an input.
- Every endpoint without authorization metadata is already protected by the authenticated fallback
  policy. Add `RequireScope`/`RequireRole` policies for narrower access; never add `AllowAnonymous`
  outside the health probes.
- Login, signup, OTP, password reset and password change are never implemented here: they are the
  identity provider's own flows.
- Administration of the identity provider (its users, their roles and groups) is implemented only in the
  one backend the owner names as its administration adapter, under the rules of
  [MP Core ADR-019](https://github.com/panahister/mpcore/blob/main/docs/decisions/ADR-019-identity-provider-administration-by-a-governed-adapter.EN.md). Any other backend sends the adapter a
  command and never calls the provider's administration API itself. See "An identity-provider
  administration adapter" below.
- Never write a realm URL, client secret, connection string or credential into this repository.

## Steps

1. Resolve the caller through `ICurrentActorAccessor`. Pass the actor into the Application layer as a
   value; do not let Domain depend on HTTP or on the accessor.
2. Choose the narrowest control that satisfies the approved requirement: the authenticated fallback,
   a scope policy, or a role policy. Ask which roles or scopes apply — never invent them, and never
   hardcode a realm, client id or role name that the owner has not given you.
3. Enforce ownership in the domain, not only in the policy: a caller authorized to call an operation
   is not automatically authorized to act on a particular record.
4. Expect `401` for an absent or invalid token and `403` for an authenticated caller lacking rights,
   with no configuration detail in the response body.

## An identity-provider administration adapter

Only in the backend the owner has named as the adapter; anywhere else, stop and ask. The rules
(MP Core ADR-019):

- **A closed catalogue of typed commands**, one handler each, every one approved by the owner. There is
  no pass-through: no command forwards a provider path, an HTTP method, a raw payload or a query. No
  command sets or resets a password, enrols a one-time code, signs in as a user, reads or issues a user's
  token, or configures a realm, a client or a flow (that is the platform's work).
- **Its own service identity**: a confidential client with the client credentials grant, through
  `AddMPCoreServiceIdentity`, granted only the provider roles the catalogue needs. Never a person's token,
  never an administrator's account. Its secret is configuration, never in this repository.
- **Every caller authorized for every command**: the callers it lists and, where a person asked for the
  change, `RequireSubjectEvidence` and `RequireResourceKey`. The user a command acts on is a parameter,
  never the caller's identity.
- **Every write audited** with `IBusinessAuditRecorder`: who asked, the command, the target's identifier,
  the outcome as the provider answered; a refused or failed attempt too, with `RecordAttemptAsync`.
  Identifiers only, never a credential or the provider's response body.
- **No administration token**, client secret or credential in any log, trace, audit record, message,
  problem document or response. The administration token is obtained and presented by
  `AddMPCoreServiceIdentity` (MP Core ADR-014), which never gives it to application code; any credential
  the adapter itself holds, such as the client's secret read from configuration or a token obtained by any
  other path, is a `SensitiveValue` (MP Core ADR-018). The adapter's client to the provider is registered
  with `RedactLoggedHeaders` for `Authorization`, or with its loggers removed, and a test captures the log
  of one call and asserts that it holds no token; the adapter maps the provider's errors to this backend's
  own failure codes and never returns or logs the provider's body.
- **Every write idempotent**: it sets a state rather than adding to one, because a command can arrive
  twice; no provider call inside a transaction a concurrent attempt can lose.

Steps:

1. Confirm with the owner that this backend is the adapter, and which commands the catalogue holds.
2. Declare a port in `Application/Ports` with one method per command, and implement it in
   `Infrastructure` with an HTTP client registered with `AddMPCoreResilientHttpClient` and
   `AddMPCoreServiceIdentity`, against the provider's documented administration API.
3. Give each command its handler, its authorization and its audit record.
4. Test a caller that is not listed, a caller without the right, a refusal by the provider (recorded, and
   answered without the provider's body), and a captured log of one call that holds no token.

The registration of the client to the provider, and the test that captures the log of one call. The test is
an example a product copies into its tests project and adapts; a stub stands for the provider:

```csharp
// The administration client, and a captured log of one call (MP Core ADR-019, section 6).
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MPCore.Resilience.Http;

namespace IdentityAdministration.Example;

public sealed class AdministrationClientLogTests
{
    private const string Token = "administration-token-7f3a";

    [Fact]
    public async Task A_captured_log_of_one_call_to_the_provider_holds_no_token()
    {
        var log = new CapturedLog();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(log));

        // The registration. The token is obtained and presented by AddMPCoreServiceIdentity, and the
        // Authorization header is redacted in the client's own logs.
        services.AddMPCoreResilientHttpClient("identity-administration", client => client.BaseAddress = new Uri("https://identity.invalid/admin/realms/shop/"))
            .AddMPCoreServiceIdentity(options =>
            {
                options.TokenEndpoint = new Uri("https://identity.invalid/realms/shop/protocol/openid-connect/token");
                options.ClientId = "identity-administration";
                options.ClientSecret = "read-from-configuration";
            })
            .RedactLoggedHeaders(["Authorization"])
            .ConfigurePrimaryHttpMessageHandler(() => new Provider());
        services.AddHttpClient(ServiceIdentity.TokenClientName).ConfigurePrimaryHttpMessageHandler(() => new Provider());

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("identity-administration");
        using var response = await client.GetAsync(new Uri("users?username=someone", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(log.Entries, entry => entry.Contains("Authorization", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, entry => entry.Contains(Token, StringComparison.Ordinal));
    }

    /// <summary>Stands for the provider: issues the token, then answers the call.</summary>
    private sealed class Provider : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)
                        ? $"{{\"access_token\":\"{Token}\",\"token_type\":\"Bearer\",\"expires_in\":300}}"
                        : "[]",
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    /// <summary>Every log entry, its message and its scopes, as text.</summary>
    private sealed class CapturedLog : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopes;

        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Logger(CapturedLog owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes?.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var text = new StringBuilder($"{category}: {formatter(state, exception)} {exception}");
                owner._scopes?.ForEachScope((scope, builder) => builder.Append(" | ").Append(scope), text);
                owner.Entries.Enqueue(text.ToString());
            }
        }
    }
}
```

## Verification

Test the negative paths: no token, valid token without the right, and a caller acting on another
actor's record. A capability tested only on its happy path has no evidence of being protected.

## Project configuration is authoritative

Read `.mpcore/template-manifest.json` first. Its `schemaVersion` tells you how to read it; treat an
unrecognized value as incompatible rather than guessing. It records `shape`, `transport`, `messaging`
and `aiTooling`, plus the `mpcoreVersion`, `templateVersion` and `cliVersion` that produced this
repository. Those values are decisions already made; they are not defaults to revisit.

- `transport` `grpc` means no REST surface exists. `rest` means no proto or gRPC service exists.
  Do not add the other transport. Changing transport is a scope change the human owner must make.
- `messaging` `none` means no external broker is configured. Wolverine local queues and PostgreSQL
  message storage still exist for in-process work; that is not Kafka or RabbitMQ. Do not add a broker
  package, topic, exchange or connection string.
- `shape` `service` has no `src/Modules`. `modular-monolith` places each bounded context in one
  project under `src/Modules/<Context>`, with Domain, Application and Infrastructure folders, and
  module boundaries must survive at the edge. `src/Modules/README.md` is the layout.

MP Core arrives as NuGet packages pinned to `mpcoreVersion`. Never copy MP Core source into this
repository and never edit a package to change framework behaviour.

## Boundaries

- Implement only what the human owner approved. If the requirement is ambiguous, list the specific
  ambiguities and ask; do not invent business rules, statuses, limits or workflows.
- Do not weaken authorization, delete a failing assertion, or relax a security default to make an
  example pass.
- Preserve unrelated work. Report what you changed and what you did not.
- Report actual command output. A specification is not evidence that something ran.
