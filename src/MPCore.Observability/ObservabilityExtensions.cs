using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MPCore.Observability;

/// <summary>The OpenTelemetry resource identity and exporter switch for a host.</summary>
public sealed class MPCoreObservabilityOptions
{
    /// <summary>Gets the required service name reported on every signal.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Gets the service namespace, the group of services this one belongs to. Defaults to <c>default</c>.</summary>
    public string ServiceNamespace { get; init; } = "default";

    /// <summary>Gets the optional service version.</summary>
    public string? ServiceVersion { get; init; }

    /// <summary>Gets a value indicating whether the OTLP exporter is enabled.</summary>
    public bool EnableOtlpExporter { get; init; } = true;

    /// <summary>
    /// Per-signal destinations, sampling, scrape and redaction. Null means every signal follows
    /// <see cref="EnableOtlpExporter"/> with environment defaults, redaction on, no scrape.
    /// </summary>
    public MPCoreObservabilitySignals? Signals { get; init; }

    /// <summary>Validates the options.</summary>
    /// <exception cref="ArgumentException"><see cref="ServiceName"/> is missing.</exception>
    public void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(ServiceName);
}

/// <summary>Registration surface for MP Core tracing, metrics and log correlation.</summary>
public static class ObservabilityExtensions
{
    /// <summary>Registers OpenTelemetry logging, tracing and metrics for the host.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The resource identity and exporter switch.</param>
    public static IServiceCollection AddMPCoreObservability(
        this IServiceCollection services,
        MPCoreObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var plan = ObservabilityPlan.Resolve(options);
        services.AddSingleton(plan);

        services.Configure<OpenTelemetryLoggerOptions>(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var openTelemetry = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                options.ServiceName,
                options.ServiceNamespace,
                options.ServiceVersion))
            .WithLogging(logging =>
            {
                if (plan.RedactionEnabled)
                {
                    logging.AddProcessor(new SensitiveLogRecordProcessor(plan.SensitiveFields));
                }

                if (plan.Logs.Exporter == SignalExporter.Otlp)
                {
                    logging.AddOtlpExporter(ObservabilityPlan.LogsExporterName, exporter => Apply(exporter, plan.Logs));
                }
            })
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(plan.SamplingRatio)))
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource("Wolverine");
                if (plan.RedactionEnabled)
                {
                    tracing.AddProcessor(new SensitiveActivityProcessor(plan.SensitiveFields));
                }

                if (plan.Traces.Exporter == SignalExporter.Otlp)
                {
                    tracing.AddOtlpExporter(ObservabilityPlan.TracesExporterName, exporter => Apply(exporter, plan.Traces));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Wolverine names its meter after the service ("Wolverine:<ServiceName>"); the exact name
                    // "Wolverine" is kept for older versions that used it. MP Core's own meters, such as
                    // MPCore.Localization, all start with "MPCore.".
                    .AddMeter("Wolverine", "Wolverine:*", "MPCore.*");
                if (plan.Metrics.Exporter == SignalExporter.Otlp)
                {
                    metrics.AddOtlpExporter(ObservabilityPlan.MetricsExporterName, exporter => Apply(exporter, plan.Metrics));
                }
            });

        _ = openTelemetry;
        return services;
    }

    private static void Apply(OtlpExporterOptions exporter, ResolvedDestination destination)
    {
        if (destination.Endpoint is not null)
        {
            exporter.Endpoint = destination.Endpoint;
        }

        exporter.Protocol = destination.Protocol == OtlpTransport.HttpProtobuf ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        if (destination.Headers is not null)
        {
            exporter.Headers = destination.Headers;
        }
    }
}

/// <summary>
/// The field names that must never be emitted to a log, span or metric. The list is advisory to
/// products and is deliberately conservative.
/// </summary>
public static class SensitiveDataPolicy
{
    /// <summary>The default set of field names treated as sensitive.</summary>
    public static readonly ISet<string> DefaultSensitiveFields = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "access_token",
        "refresh_token",
        "authorization",
        "national_id",
        "mobile",
        "email",
        "kyc_evidence",
        "secret",
        "client_secret",
        "api_key",
        "apikey",
        "token",
        "id_token",
        "iban",
        "card_number",
        "pan",
        "cvv",
        "pin",
        "otp",
        "phone",
        "ssn"
    };
}
