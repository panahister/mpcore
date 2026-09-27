using Wolverine;
using Wolverine.Kafka;

namespace MPCore.Messaging.Wolverine.Kafka;

/// <summary>The Kafka broker coordinates a host supplies for integration-event transport.</summary>
public sealed class KafkaTransportOptions
{
    /// <summary>Gets the required Kafka bootstrap servers. No credential belongs here.</summary>
    public required string BootstrapServers { get; init; }

    /// <summary>
    /// Gets a value indicating whether topics may be created at startup. Keep this off outside
    /// local development, where topic shape is owned by the platform.
    /// </summary>
    public bool AutoProvision { get; init; }

    /// <summary>Validates the options.</summary>
    /// <exception cref="ArgumentException"><see cref="BootstrapServers"/> is missing.</exception>
    public void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(BootstrapServers);
}

/// <summary>Registration surface for the Kafka integration-event transport.</summary>
public static class KafkaTransportExtensions
{
    /// <summary>Adds the Kafka transport to a Wolverine configuration.</summary>
    /// <param name="options">The Wolverine options.</param>
    /// <param name="transport">The broker coordinates.</param>
    public static WolverineOptions UseMPCoreKafka(
        this WolverineOptions options,
        KafkaTransportOptions transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        transport.Validate();

        var kafka = options.UseKafka(transport.BootstrapServers);
        if (transport.AutoProvision)
        {
            kafka.AutoProvision();
        }

        // A Bounded Context must register every publish/listen route explicitly.
        // The foundation deliberately does not use PublishAllMessages().
        return options;
    }
}
