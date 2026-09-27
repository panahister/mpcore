using MPCore.Messaging.Wolverine;
using MPCore.Messaging.Wolverine.Kafka;
using MPCore.Messaging.Wolverine.RabbitMQ;

namespace MPCore.Messaging.Tests;

public sealed class MessagingOptionsTests
{
    [Fact]
    public void Foundation_requires_a_service_and_durable_store()
    {
        var options = new WolverineFoundationOptions
        {
            ServiceName = "test-service",
            PersistenceConnectionString = "Host=localhost;Database=test"
        };

        options.Validate();
    }

    [Fact]
    public void Kafka_requires_bootstrap_servers() =>
        Assert.Throws<ArgumentException>(() => new KafkaTransportOptions
        {
            BootstrapServers = string.Empty
        }.Validate());

    [Fact]
    public void RabbitMq_requires_a_connection_string() =>
        Assert.Throws<ArgumentException>(() => new RabbitMqTransportOptions
        {
            ConnectionString = string.Empty
        }.Validate());
}
