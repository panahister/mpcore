# MPCORE_ORGANIZATION.MPCORE_COMPONENT

Generated from MP Core `MPCORE_VERSION`. The framework arrives as NuGet packages; its source is not
copied here.

## Start here

| | |
|---|---|
| 🚀 **[Quick start](docs/getting-started.md)** | Restore, build, run, and the settings you must replace first |
<!--#if (includeAiSkills) -->
| 🤖 **[AI skills](docs/ai-skills.md)** | What each of the 9 skills does, and a ready-made prompt for it |
<!--#endif -->
| 🏗️ **[Architecture of this project](docs/architecture.md)** | Components, layers, request path and trust boundary — for *these* settings |
| 📦 **[MP Core capabilities](docs/capabilities.md)** | Everything the framework supports, and what is deliberately absent |
| 🧭 **[Business development path](docs/development-workflow.md)** | How an empty scaffold becomes a shipped capability |
| 📚 **[Examples](docs/examples/)** | Worked, hypothetical illustrations — not code to keep |

This project has business behaviour to be written, not removed. It ships a technical foundation:
host, transport, persistence, security and observability wiring — and nothing about your domain.

## How it was generated

| Setting | Value |
|---|---|
| Shape | `MPCORE_SHAPE_LABEL` |
| Transport | `MPCORE_TRANSPORT_LABEL` |
| Messaging | `MPCORE_MESSAGING_LABEL` |
| Business audit | `MPCORE_BUSINESS_AUDIT_LABEL` |
| Cache | `MPCORE_CACHE_LABEL` |
| Time series | `MPCORE_TIMESERIES_LABEL` |
| AI tooling | `MPCORE_AI_TOOLING` |

Recorded in `.mpcore/template-manifest.json`. These are decisions already made: guidance for a
transport or broker this project does not have simply does not apply.

Some of them can be changed later — see
[changing these settings](docs/getting-started.md#changing-these-settings-later). Others are a
migration the tooling deliberately refuses to perform for you.

<!--#if (includeAiSkills) -->
## Working with an AI assistant

Nothing here installs a tool or grants a permission. These are project instructions and discoverable
procedures that each assistant reads.

<!--#if (includeCodex) -->
- **Codex** reads `AGENTS.md`, discovers skills from `.agents/skills/`.
<!--#endif -->
<!--#if (includeClaude) -->
- **Claude Code** reads `CLAUDE.md`, discovers skills from `.claude/skills/`.
<!--#endif -->

Those are thin adapters. Each skill's instructions live once at `.mpcore/skills/<name>/SKILL.md`;
both adapters point there. Edit the body, never an adapter. Start at
**[docs/ai-skills.md](docs/ai-skills.md)** — it explains every skill in plain terms and gives you a
prompt you can paste.

<!--#endif -->
## Transport

One host project, `MPCore.Backend.Api`, serves the transport chosen at generation time.

| `--transport` | Kestrel endpoints | Mapped surfaces |
|---|---|---|
| `grpc` | `Grpc` `http://0.0.0.0:8081` `Http2` | `PlatformProbe`, `grpc.health.v1.Health` |
| `rest` | `Rest` `http://0.0.0.0:8080` `Http1AndHttp2` | `/v1/platform/status`, `/health/live`, `/health/ready`, `/health/startup` |
| `both` | `Rest` `:8080` `Http1AndHttp2` and `Grpc` `:8081` `Http2` | both sets, one port each |

Kestrel does not sniff the HTTP/2 connection preface. A single cleartext `Http1AndHttp2` endpoint serves HTTP/1.1 and rejects prior-knowledge h2c gRPC with `HTTP_1_1_REQUIRED`, so `both` uses two cleartext ports by default. A single-port `both` deployment is supported only over TLS, where ALPN performs real negotiation; set `Transport:EnforcePortSeparation` to `false` in that case. `Api/Hosting/TransportEndpointGuard.cs` fails the host at boot when the configuration cannot serve the generated transport, when a declared `Transport:RestPort` or `Transport:GrpcPort` matches no configured Kestrel endpoint, or when port separation is left enabled on a single endpoint.

### Endpoint-to-port binding

With `--transport both` and `Transport:EnforcePortSeparation` set to `true`, each endpoint is bound to the Kestrel listener it may be served from. The binding is `Api/Hosting/TransportPortSeparation.cs`: `RequireListenerPort(port)` marks the endpoint and `UseTransportPortSeparation()`, registered immediately after `UseRouting()`, compares the mark against `HttpContext.Connection.LocalPort`. A request that reaches an endpoint through the other listener gets `404`.

ASP.NET Core's `RequireHost("*:{port}")` is deliberately not used. Its matcher evaluates `HttpRequest.Host`, that is the `Host` header on HTTP/1.1 and the `:authority` pseudo-header on HTTP/2. Both are client- and proxy-controlled, so the constraint is spoofable; and a gateway that forwards `Host: api.example.com` with no port -- the APISIX and nginx `proxy_set_header Host $host` default -- would match no port constraint and receive `404` for every REST endpoint, health probes included. `Connection.LocalPort` is the accepting socket's port: server state that no header can influence.

Behind the gateway this means nothing needs to be configured. Point the REST upstream at `:8080` and the gRPC upstream at `:8081` and forward whatever `Host` value the deployment requires.

TLS is terminated by the edge gateway, so the generated defaults are cleartext. `Security:RequireHttpsMetadata` concerns the identity-provider metadata URL and stays `true` regardless.

Two advisories, neither of which blocks generation:

- `--transport rest` with `--shape modular-monolith`: keep one route prefix per module so module boundaries survive at the edge;
- `--transport both`: every use case is exposed twice, so decide which transport is the supported contract for each consumer instead of letting the two drift.

## Security

The host is a bearer-only OAuth 2.0 / OIDC resource server. Login, signup, OTP, forgot-password, change-password and identity-provider administration are Product surfaces and are never implemented here.

`Security:Authority` and `Security:Audiences` have no usable defaults and must be replaced before the host starts. `ConnectionStrings:PostgreSql` and `Messaging:RabbitMq:ConnectionString` ship with `replace-me` credentials for the same reason: the generated file must never contain a working credential, not even a well-known development one.

Every endpoint without authorization metadata is protected by the authenticated fallback policy. The only anonymous endpoints are the health probes, which return the aggregate status word only. `Security:AllowAnonymousHealthEndpoints` is read by this host, which maps the probes and applies `AllowAnonymous()` to them; MP Core does not map them and therefore does not carry that setting.

Roles reaching an authorization decision are derived only from the configured `RoleSources` -- `realm_access.roles` and `resource_access.*.roles` by default. A top-level `role` claim inside a token is discarded, because that claim type is MP Core's synthesized output and never an input.

Never place a client secret, realm export or credential in this repository.
