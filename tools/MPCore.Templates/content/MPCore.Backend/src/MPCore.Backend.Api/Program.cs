using MPCore.Backend.Api.Hosting;
// #if (shape == "service")
using MPCore.Backend.Application;
// #endif
using MPCore.Backend.Infrastructure;
using MPCore.Backend.Infrastructure.Persistence;
using MPCore.Hosting;
using MPCore.Localization;
using MPCore.Messaging.Wolverine;
using MPCore.Observability;
// #if (includeRest)
using MPCore.Observability.Prometheus;
// #endif
using MPCore.Security.AspNetCore;
using MPCore.Validation.FluentValidation;
// #if (includeGrpc)
using MPCore.Backend.Api.Grpc.Services;
using MPCore.Transport.Grpc;
// #endif
// #if (includeRest)
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MPCore.Backend.Api.Rest.Endpoints;
using MPCore.Transport.Http;
// #endif
// #if (messaging == "kafka")
using MPCore.Messaging.Wolverine.Kafka;
// #endif
// #if (messaging == "rabbitmq")
using MPCore.Messaging.Wolverine.RabbitMQ;
// #endif

var builder = WebApplication.CreateBuilder(args);

// Per-endpoint Kestrel "Protocols" values in appsettings.json are authoritative; no protocol is
// forced globally.
// #if (includeGrpc)
// A single cleartext Http1AndHttp2 endpoint cannot serve prior-knowledge h2c HTTP/2, so the
// endpoint that carries binary RPC declares Http2 exclusively.
// #endif
// #if (transport == "grpc")
const TransportMode HostTransport = TransportMode.Grpc;
// #endif
// #if (transport == "rest")
const TransportMode HostTransport = TransportMode.Rest;
// #endif
// #if (transport == "both")
const TransportMode HostTransport = TransportMode.Both;
// #endif
TransportEndpointGuard.Validate(builder.Configuration, HostTransport);

// Every log goes through MP Core's pipeline and nowhere else. WebApplication.CreateBuilder adds the console,
// debug and event-source providers, and each prints a log argument as it is: a protobuf request of a service
// named in AddMPCoreSensitiveMessages would reach the console whole, one-time code and token included, since
// Google.Protobuf prints even a debug_redact field (ADR-018). Clearing them must come before the foundation
// registers the pipeline, because ClearProviders also removes a provider registered earlier; the pipeline's
// console sink then keeps the logs on the console, after redaction.
builder.Logging.ClearProviders();
builder.Services.AddMPCoreFoundation(new MPCoreObservabilityOptions
{
    ServiceName = "MPCore.Backend",
    ServiceNamespace = "MPCORE_ORGANIZATION",
    ServiceVersion = typeof(Program).Assembly.GetName().Version?.ToString(),
    EnableOtlpExporter = builder.Configuration.GetValue("Observability:EnableOtlpExporter", false),
    EnableConsoleLogExporter = builder.Configuration.GetValue("Observability:EnableConsoleLogExporter", true),
    // Each signal has its own destination, sampling and redaction settings; see docs/architecture.md.
    Signals = builder.Configuration.GetSection("Observability").Get<MPCoreObservabilitySignals>()
});
// #if (includeRest)
// Prometheus pull is a REST-listener surface. It carries no anonymous metadata: the scraper must
// present a bearer token, or the deployment must confine the listener to the scraper's network.
var metricsScrapeEnabled = builder.Configuration.GetValue("Observability:Metrics:Prometheus:Enabled", false);
if (metricsScrapeEnabled)
{
    builder.Services.AddMPCorePrometheusScrape();
}
// #endif
// #if (shape == "service")
builder.Services.AddApplication();
// #else
// One registration per bounded-context module, each from its own project:
//     builder.Services.AddBillingModule();
// See src/Modules/README.md.
// #endif

// Failures reach the caller as message keys, rendered here in the caller's language. MP Core's own
// messages ship in English, its only built-in language; this repository adds the languages it serves,
// each module with its resource file. See docs/architecture.md, "Business rules, validation and messages".
builder.Services.AddMPCoreMessageCatalog();
// Input validators (FluentValidation) of every handler assembly. They run before the handler.
foreach (var assembly in HandlerAssemblies.All)
{
    builder.Services.AddMPCoreValidators(assembly);
}

// Bearer-only OIDC resource server. Login, signup, OTP and password flows are the identity provider's
// and are never implemented here. Administering the identity provider happens only in the backend the
// owner names for it, under the rules of the mpcore-apply-security skill (MP Core ADR-019).
builder.Services.AddMPCoreBearerAuthentication(options =>
{
    options.Authority = builder.Configuration["Security:Authority"]
        ?? throw new InvalidOperationException("Security:Authority is required.");
    options.RequireHttpsMetadata = builder.Configuration.GetValue("Security:RequireHttpsMetadata", true);
    foreach (var audience in builder.Configuration.GetSection("Security:Audiences").Get<string[]>() ?? [])
    {
        options.ValidAudiences.Add(audience);
    }
});
// Claim mapping follows the identity provider. Keycloak-shaped by default (realm and client roles,
// preferred_username, azp, service-account-* convention); GenericOidc reads a flat roles claim.
builder.Services.AddMPCoreCurrentActor(mapping =>
{
    if (string.Equals(builder.Configuration["Security:ClaimMapping:Preset"], "GenericOidc", StringComparison.OrdinalIgnoreCase))
    {
        mapping.UseGenericOidc();
    }
    else
    {
        mapping.UseKeycloakDefaults();
    }
});
// The tenant, when the token names one. Business code reads ITenantContext; audit records it.
builder.Services.AddMPCoreTenancyFromClaim(builder.Configuration["Security:TenantClaim"] ?? "tenant_id");
builder.Services.AddForwardedIdentityHeaderGuard();
// X-Forwarded-* is honoured only from the proxies listed here (APISIX or another gateway). Empty
// means the host reasons from the real connection and ignores the headers entirely.
builder.Services.AddMPCoreGatewayForwarding(options =>
{
    foreach (var proxy in builder.Configuration.GetSection("Gateway:TrustedProxies").Get<string[]>() ?? [])
    {
        options.TrustedProxies.Add(proxy);
    }
});

// Mutual TLS between services is opt-in (MP Core ADR-017). When on, every TLS listener requires a client
// certificate from the configured authorities with a listed workload name; the bearer token stays the
// caller of every request. A listener becomes TLS in Kestrel:Endpoints, with its own certificate.
if (builder.Configuration.GetValue("Security:MutualTls:Enabled", false))
{
    builder.Services.AddMPCoreMutualTls(options => builder.Configuration.GetSection("Security:MutualTls").Bind(options));
}

// MP Core does not map the health probes, so it cannot enforce anonymous access to them. This host
// maps them and therefore owns the decision, applied with AllowAnonymous() where they are mapped.
var allowAnonymousHealthEndpoints =
    builder.Configuration.GetValue("Security:AllowAnonymousHealthEndpoints", true);
builder.Services.AddMPCoreAuthorization();

// Description surfaces are opt-in and default to Development only: an OpenAPI document and gRPC
// reflection describe the entire API to whoever can reach them.
var enableOpenApi = DeveloperEndpoints.IsDescriptionSurfaceEnabled(
    builder.Configuration, builder.Environment, "Transport:EnableOpenApi");
var enableGrpcReflection = DeveloperEndpoints.IsDescriptionSurfaceEnabled(
    builder.Configuration, builder.Environment, "Transport:EnableGrpcReflection");

// A description surface is only reachable without a token in Development. Enabling one explicitly in
// another environment keeps it behind the authenticated fallback: the flag says "expose it", not
// "expose it to anyone".
var anonymousDescriptionSurface = builder.Environment.IsDevelopment();

// What "alive" and "ready" mean is the same on every transport: Hosting/HostHealthChecks.cs.
builder.Services.AddHostHealthChecks();

// #if (includeGrpc)
builder.Services.AddGrpc().AddMPCoreFailureHandling();
// The empty service name is the whole host; "live" asks the process only.
builder.Services.AddGrpcHealthChecks(options =>
    options.Services.Map(HostHealthChecks.Live, static check => check.Tags.Contains(HostHealthChecks.Live)));
if (enableGrpcReflection)
{
    builder.Services.AddGrpcReflection();
}
// #endif
// #if (includeRest)
builder.Services.AddMPCoreHttpFailureHandling();
builder.Services.AddMPCoreProblemDetailsSecurityResponses();
if (enableOpenApi)
{
    builder.Services.AddOpenApi();
}
// #endif

var databaseConnection = builder.Configuration.GetConnectionString("PostgreSql")
    ?? throw new InvalidOperationException("ConnectionStrings:PostgreSql is required.");
//#if (includeCacheConnection)
var cacheConnection = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is required for the selected cache.");
builder.Services.AddInfrastructure(databaseConnection, cacheConnection);
//#else
builder.Services.AddInfrastructure(databaseConnection);
//#endif
// AppDbContext is named here as the transaction owner, so a handler can depend on IUnitOfWork and
// still run inside the Entity Framework transaction whose commit releases its outgoing messages.
builder.Host.UseMPCoreWolverine<AppDbContext>(
    new WolverineFoundationOptions
    {
        ServiceName = "MPCore.Backend",
        PersistenceConnectionString = databaseConnection,
        PersistenceSchemaName = "wolverine",
        // This project's own handlers are discovered from here, in addition to HandlerAssemblies.All.
        ApplicationAssembly = typeof(Program).Assembly
    },
    options =>
    {
        // Handlers are discovered only in the assemblies this host names. Nothing is scanned
        // implicitly and no catch-all policy exists; see Hosting/HandlerAssemblies.cs.
        options.DiscoverHandlersIn(HandlerAssemblies.All);
        // A message whose validators fail never reaches its handler; the caller receives MP Core's
        // validation failure with one violation per field.
        options.UseMPCoreFluentValidation();
        // #if (messaging == "kafka")
        options.UseMPCoreKafka(new KafkaTransportOptions
        {
            BootstrapServers = builder.Configuration["Messaging:Kafka:BootstrapServers"]
                ?? throw new InvalidOperationException("Messaging:Kafka:BootstrapServers is required."),
            AutoProvision = builder.Configuration.GetValue("Messaging:AutoProvision", false)
        });
        // #endif
        // #if (messaging == "rabbitmq")
        options.UseMPCoreRabbitMq(new RabbitMqTransportOptions
        {
            ConnectionString = builder.Configuration["Messaging:RabbitMq:ConnectionString"]
                ?? throw new InvalidOperationException("Messaging:RabbitMq:ConnectionString is required."),
            AutoProvision = builder.Configuration.GetValue("Messaging:AutoProvision", false)
        });
        // #endif
    });

var app = builder.Build();

// ADR-007 pipeline order. UseAuthentication always precedes UseAuthorization, and both follow
// UseRouting so the authenticated fallback policy sees resolved endpoint metadata.
// A client certificate is believed from a trusted proxy only, so it is read before gateway forwarding
// replaces the proxy's address. Without mutual TLS this does nothing.
app.UseMPCoreCertificateForwarding();
// Forwarded headers come first, so everything after it sees the scheme and client the gateway saw.
app.UseMPCoreGatewayForwarding();
// #if (includeRest)
app.UseMPCoreProblemDetails();
app.UseMPCoreRequestContext();
// #endif
app.UseForwardedIdentityHeaderGuard();
// #if (includeRest)
if (enableOpenApi && anonymousDescriptionSurface)
{
    // The UI is a static shell that a browser navigates to, so it cannot carry a bearer token, and
    // the authenticated fallback policy applies to middleware-served content as well as to mapped
    // endpoints. It is therefore mounted ahead of authentication and only in Development. Outside
    // Development it is not served at all; the document endpoint remains and stays protected.
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "MPCORE_ORGANIZATION.MPCORE_COMPONENT v1");
        options.RoutePrefix = "openapi-ui";
    });
}
// #endif

app.UseRouting();
// #if (transport == "both")
// Runs after routing so the resolved endpoint's listener binding can be checked, and before
// authentication so a misrouted call is refused without evaluating any credential.
app.UseTransportPortSeparation();
// #endif
app.UseAuthentication();
// An endpoint marked RequireWorkloadCertificate() without a valid certificate is refused with 401 here.
// Without mutual TLS this does nothing.
app.UseMPCoreMutualTls();
app.UseAuthorization();

// #if (includeGrpc)
var grpcProbeEndpoint = app.MapGrpcService<PlatformProbeService>();
var grpcHealthEndpoint = app.MapGrpcHealthChecksService();
if (allowAnonymousHealthEndpoints)
{
    grpcHealthEndpoint.AllowAnonymous();
}
IEndpointConventionBuilder? grpcReflection = null;
if (enableGrpcReflection)
{
    // Reflection lets grpcui and grpcurl discover services without a local .proto copy. Outside
    // Development it stays behind the authenticated fallback, so enabling it on a shared host does not
    // hand the service inventory to an anonymous caller.
    grpcReflection = app.MapGrpcReflectionService();
    if (anonymousDescriptionSurface)
    {
        grpcReflection.AllowAnonymous();
    }
}
// #endif

// #if (includeRest)
IEndpointConventionBuilder? openApiDocument = null;
if (enableOpenApi)
{
    // The document describes the whole REST surface. Anonymous only in Development; when enabled
    // elsewhere it exists but requires a token like every other endpoint.
    openApiDocument = app.MapOpenApi();
    if (anonymousDescriptionSurface)
    {
        openApiDocument.AllowAnonymous();
    }
}

var restProbeEndpoints = app.MapPlatformProbeEndpoints();
var livenessEndpoint = app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = static check => check.Tags.Contains(HostHealthChecks.Live),
        ResponseWriter = WriteAggregateStatusAsync
    });
var readinessEndpoint = app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions { ResponseWriter = WriteAggregateStatusAsync });
var startupEndpoint = app.MapHealthChecks(
    "/health/startup",
    new HealthCheckOptions { ResponseWriter = WriteAggregateStatusAsync });
if (allowAnonymousHealthEndpoints)
{
    livenessEndpoint.AllowAnonymous();
    readinessEndpoint.AllowAnonymous();
    startupEndpoint.AllowAnonymous();
}

IEndpointConventionBuilder? metricsScrape = null;
if (metricsScrapeEnabled)
{
    metricsScrape = app.MapMPCorePrometheusScrape(app.Configuration.GetValue("Observability:Metrics:Prometheus:Path", "/metrics")!);
}
// #endif

// #if (transport == "both")
// Endpoints are bound to the Kestrel listener they may be served from, never to the client-supplied
// Host header. TransportEndpointGuard has already proved both ports match real listeners.
if (app.Configuration.GetValue("Transport:EnforcePortSeparation", true))
{
    var restPort = app.Configuration.GetValue("Transport:RestPort", 8080);
    var grpcPort = app.Configuration.GetValue("Transport:GrpcPort", 8081);
    grpcProbeEndpoint.RequireListenerPort(grpcPort);
    grpcHealthEndpoint.RequireListenerPort(grpcPort);
    restProbeEndpoints.RequireListenerPort(restPort);
    livenessEndpoint.RequireListenerPort(restPort);
    readinessEndpoint.RequireListenerPort(restPort);
    startupEndpoint.RequireListenerPort(restPort);
    openApiDocument?.RequireListenerPort(restPort);
    metricsScrape?.RequireListenerPort(restPort);
    grpcReflection?.RequireListenerPort(grpcPort);
}
// #endif

await app.RunAsync();

// #if (includeRest)
// Health responses expose the aggregate status word only. Check names, dependency hosts, durations
// and exception text are never disclosed anonymously.
static Task WriteAggregateStatusAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "text/plain; charset=utf-8";
    return context.Response.WriteAsync(report.Status.ToString());
}
// #endif

public partial class Program;
