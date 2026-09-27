using Wolverine;
using Wolverine.RabbitMQ;

namespace MPCore.Messaging.Wolverine.RabbitMQ;

/// <summary>The RabbitMQ broker coordinates a host supplies for command and work-queue transport.</summary>
public sealed class RabbitMqTransportOptions
{
    /// <summary>
    /// Gets the required AMQP connection string. It is supplied from host configuration or a secret
    /// store and is never committed to a repository.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Gets a value indicating whether exchanges and queues may be created at startup. Keep this off
    /// outside local development, where topology is owned by the platform.
    /// </summary>
    public bool AutoProvision { get; init; }

    /// <summary>Validates the options.</summary>
    /// <exception cref="ArgumentException"><see cref="ConnectionString"/> is missing.</exception>
    public void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
}

/// <summary>Registration surface for the RabbitMQ command and work-queue transport.</summary>
public static class RabbitMqTransportExtensions
{
    /// <summary>Adds the RabbitMQ transport to a Wolverine configuration.</summary>
    /// <param name="options">The Wolverine options.</param>
    /// <param name="transport">The broker coordinates.</param>
    public static WolverineOptions UseMPCoreRabbitMq(
        this WolverineOptions options,
        RabbitMqTransportOptions transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        transport.Validate();

        var rabbitMq = options.UseRabbitMq(new Uri(transport.ConnectionString));
        if (transport.AutoProvision)
        {
            rabbitMq.AutoProvision();
        }

        // A Bounded Context owns explicit exchange/queue routing.
        return options;
    }
}
