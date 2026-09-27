namespace MPCore.Observability;

/// <summary>Where a signal goes.</summary>
public enum SignalExporter
{
    /// <summary>The signal is collected in-process only (health, tests, local runs).</summary>
    None = 0,

    /// <summary>OTLP push to the configured endpoint, or to the OTEL_EXPORTER_OTLP_* environment when no endpoint is set.</summary>
    Otlp = 1,
}

/// <summary>OTLP wire protocol.</summary>
public enum OtlpTransport
{
    /// <summary>gRPC, the default. Port 4317 by convention.</summary>
    Grpc = 0,

    /// <summary>HTTP with protobuf payloads. Port 4318 by convention.</summary>
    HttpProtobuf = 1,
}

/// <summary>Destination of one signal. Each signal has its own, so logs, metrics and traces can go to different backends.</summary>
public class SignalDestination
{
    /// <summary>Exporter for this signal. Null falls back to <see cref="MPCoreObservabilityOptions.EnableOtlpExporter"/>.</summary>
    public SignalExporter? Exporter { get; set; }

    /// <summary>Absolute OTLP endpoint for this signal. Null lets the exporter use its environment variables and defaults.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Wire protocol for this signal.</summary>
    public OtlpTransport Protocol { get; set; } = OtlpTransport.Grpc;

    /// <summary>
    /// OTLP headers in <c>key=value,key2=value2</c> form, typically an API key for the backend. Bind it
    /// from the environment or a secret store; it is never written to logs by this package.
    /// </summary>
    public string? Headers { get; set; }
}

/// <summary>Trace destination plus sampling.</summary>
public sealed class TraceSignalOptions : SignalDestination
{
    /// <summary>Share of new traces to record, 0 to 1. Child spans follow their parent's decision. 1 records everything.</summary>
    public double SamplingRatio { get; set; } = 1.0;
}

/// <summary>Metric destination plus the optional Prometheus scrape surface.</summary>
public sealed class MetricSignalOptions : SignalDestination
{
    /// <summary>Prometheus pull endpoint. Registered by the host through the Prometheus package; off by default.</summary>
    public PrometheusScrapeOptions Prometheus { get; set; } = new();
}

/// <summary>Prometheus scrape settings. The endpoint is protected like every other endpoint unless the host decides otherwise.</summary>
public sealed class PrometheusScrapeOptions
{
    /// <summary>Whether the host maps the scrape endpoint.</summary>
    public bool Enabled { get; set; }

    /// <summary>Path of the scrape endpoint.</summary>
    public string Path { get; set; } = "/metrics";
}

/// <summary>Redaction of sensitive values in log attributes and trace tags.</summary>
public sealed class RedactionOptions
{
    /// <summary>On by default. Turning it off is a deliberate decision that belongs in a review.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Attribute and tag names to redact in addition to <see cref="SensitiveDataPolicy.DefaultSensitiveFields"/>.</summary>
    public IList<string> AdditionalSensitiveFields { get; set; } = [];
}

/// <summary>Per-signal configuration. Bind from the <c>Observability</c> configuration section.</summary>
public sealed class MPCoreObservabilitySignals
{
    /// <summary>Log destination.</summary>
    public SignalDestination Logs { get; set; } = new();

    /// <summary>Metric destination and scrape surface.</summary>
    public MetricSignalOptions Metrics { get; set; } = new();

    /// <summary>Trace destination and sampling.</summary>
    public TraceSignalOptions Traces { get; set; } = new();

    /// <summary>Redaction policy.</summary>
    public RedactionOptions Redaction { get; set; } = new();
}

/// <summary>One resolved destination: what the pipeline will actually do for a signal.</summary>
/// <param name="Exporter">Effective exporter.</param>
/// <param name="Endpoint">Effective absolute endpoint, or null for environment defaults.</param>
/// <param name="Protocol">Effective protocol.</param>
/// <param name="Headers">Effective headers; never logged.</param>
public sealed record ResolvedDestination(SignalExporter Exporter, Uri? Endpoint, OtlpTransport Protocol, string? Headers);

/// <summary>
/// The composition decided from options, computed before anything is registered so it can be
/// inspected and tested without starting a pipeline.
/// </summary>
public sealed class ObservabilityPlan
{
    /// <summary>Named OTLP options for logs.</summary>
    public const string LogsExporterName = "mpcore-logs";

    /// <summary>Named OTLP options for metrics.</summary>
    public const string MetricsExporterName = "mpcore-metrics";

    /// <summary>Named OTLP options for traces.</summary>
    public const string TracesExporterName = "mpcore-traces";

    private ObservabilityPlan(ResolvedDestination logs, ResolvedDestination metrics, ResolvedDestination traces, double samplingRatio, bool prometheus, string prometheusPath, bool redaction, IReadOnlySet<string> sensitiveFields)
    {
        Logs = logs;
        Metrics = metrics;
        Traces = traces;
        SamplingRatio = samplingRatio;
        PrometheusScrapeEnabled = prometheus;
        PrometheusScrapePath = prometheusPath;
        RedactionEnabled = redaction;
        SensitiveFields = sensitiveFields;
    }

    /// <summary>Log destination.</summary>
    public ResolvedDestination Logs { get; }

    /// <summary>Metric destination.</summary>
    public ResolvedDestination Metrics { get; }

    /// <summary>Trace destination.</summary>
    public ResolvedDestination Traces { get; }

    /// <summary>Effective sampling ratio.</summary>
    public double SamplingRatio { get; }

    /// <summary>Whether the host should map the Prometheus scrape endpoint.</summary>
    public bool PrometheusScrapeEnabled { get; }

    /// <summary>Scrape path.</summary>
    public string PrometheusScrapePath { get; }

    /// <summary>Whether redaction processors are attached.</summary>
    public bool RedactionEnabled { get; }

    /// <summary>Names redacted in log attributes and trace tags, case-insensitive.</summary>
    public IReadOnlySet<string> SensitiveFields { get; }

    /// <summary>Resolves and validates the plan. Throws on an invalid ratio, endpoint or path.</summary>
    public static ObservabilityPlan Resolve(MPCoreObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var signals = options.Signals ?? new MPCoreObservabilitySignals();
        var fallback = options.EnableOtlpExporter ? SignalExporter.Otlp : SignalExporter.None;

        var ratio = signals.Traces.SamplingRatio;
        if (double.IsNaN(ratio) || ratio < 0 || ratio > 1)
        {
            throw new ArgumentException("Observability:Traces:SamplingRatio must be between 0 and 1.", nameof(options));
        }

        var path = signals.Metrics.Prometheus.Path;
        if (signals.Metrics.Prometheus.Enabled && (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/')))
        {
            throw new ArgumentException("Observability:Metrics:Prometheus:Path must start with '/'.", nameof(options));
        }

        var fields = new HashSet<string>(SensitiveDataPolicy.DefaultSensitiveFields, StringComparer.OrdinalIgnoreCase);
        foreach (var field in signals.Redaction.AdditionalSensitiveFields)
        {
            if (!string.IsNullOrWhiteSpace(field))
            {
                fields.Add(field.Trim());
            }
        }

        return new ObservabilityPlan(
            Resolve(signals.Logs, fallback, "Logs"),
            Resolve(signals.Metrics, fallback, "Metrics"),
            Resolve(signals.Traces, fallback, "Traces"),
            ratio,
            signals.Metrics.Prometheus.Enabled,
            path,
            signals.Redaction.Enabled,
            fields);
    }

    private static ResolvedDestination Resolve(SignalDestination destination, SignalExporter fallback, string signal)
    {
        var exporter = destination.Exporter ?? fallback;
        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(destination.Endpoint))
        {
            if (!Uri.TryCreate(destination.Endpoint, UriKind.Absolute, out endpoint))
            {
                throw new ArgumentException($"Observability:{signal}:Endpoint must be an absolute URI.");
            }
        }

        return new ResolvedDestination(exporter, endpoint, destination.Protocol, string.IsNullOrWhiteSpace(destination.Headers) ? null : destination.Headers);
    }
}
