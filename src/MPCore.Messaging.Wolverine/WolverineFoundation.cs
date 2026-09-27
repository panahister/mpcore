using System.Reflection;
using MPCore.Domain.Events;
using MPCore.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MPCore.Messaging.Abstractions;
using MPCore.Persistence.Abstractions;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;

namespace MPCore.Messaging.Wolverine;

/// <summary>The durable-messaging identity and outbox storage a host supplies to Wolverine.</summary>
public sealed class WolverineFoundationOptions
{
    /// <summary>Gets the required service name Wolverine reports.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Gets the required PostgreSQL connection string backing the durable outbox.</summary>
    public required string PersistenceConnectionString { get; init; }

    /// <summary>Gets the schema holding the outbox tables. Defaults to <c>wolverine</c>.</summary>
    public string PersistenceSchemaName { get; init; } = "wolverine";

    /// <summary>
    /// Gets the host's own assembly — normally <c>typeof(Program).Assembly</c>. Wolverine discovers the
    /// handlers in it in addition to the assemblies named with <c>DiscoverHandlersIn</c>. Left null, the
    /// process's entry assembly is used.
    /// </summary>
    /// <remarks>
    /// Wolverine otherwise infers its application assembly from whoever called <c>UseWolverine</c>. MP Core
    /// makes that call on the host's behalf, so the inference landed on <c>MPCore.Messaging.Wolverine</c> and
    /// the host project's own handlers — a Kafka consumer seam, for example — were never discovered.
    /// </remarks>
    public Assembly? ApplicationAssembly { get; init; }

    /// <summary>Validates the options.</summary>
    /// <exception cref="ArgumentException">A required value is missing.</exception>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ServiceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(PersistenceConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(PersistenceSchemaName);
    }
}

/// <summary>The Wolverine-backed <see cref="IMessagePublisher"/>.</summary>
/// <param name="messageBus">The Wolverine message bus.</param>
public sealed class WolverineMessagePublisher(IMessageBus messageBus) : IMessagePublisher
{
    /// <inheritdoc />
    public ValueTask PublishAsync(object message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // An integration event already knows its identity. Sending it as the idempotency key lets any
        // consumer, MP Core's inbox or another system's, recognise a second delivery of the same event.
        return message is IIntegrationEvent integrationEvent
            ? messageBus.PublishAsync(message, Delivery(new MessageDeliveryContext(
                integrationEvent.CorrelationId, integrationEvent.CausationId, null, integrationEvent.EventId.ToString("N")),
                integrationEvent))
            : messageBus.PublishAsync(message);
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(object message, MessageDeliveryContext delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        cancellationToken.ThrowIfCancellationRequested();
        return messageBus.PublishAsync(message, Delivery(delivery, message as IIntegrationEvent));
    }

    private static DeliveryOptions Delivery(MessageDeliveryContext delivery, IIntegrationEvent? integrationEvent)
    {
        var options = new DeliveryOptions();
        Add(options, MessageHeaders.CorrelationId, delivery.CorrelationId);
        Add(options, MessageHeaders.CausationId, delivery.CausationId);
        Add(options, MessageHeaders.TenantId, delivery.TenantId);
        Add(options, MessageHeaders.IdempotencyKey, delivery.IdempotencyKey ?? integrationEvent?.EventId.ToString("N"));
        if (integrationEvent is not null)
        {
            Add(options, MessageHeaders.EventVersion, integrationEvent.EventVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return options;
    }

    private static void Add(DeliveryOptions options, string header, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            options.Headers[header] = value;
        }
    }
}

/// <summary>Registration surface for durable Wolverine messaging with a PostgreSQL outbox.</summary>
public static class WolverineFoundationExtensions
{
    /// <summary>
    /// Composes the durable messaging foundation and makes <typeparamref name="TContext"/> the
    /// transaction owner through <see cref="IUnitOfWork"/>. Use this overload whenever application
    /// handlers declare the port; the non-generic one leaves Wolverine to recognise only the concrete
    /// context, which a provider-neutral handler never asks for.
    /// </summary>
    /// <typeparam name="TContext">The application context implementing <see cref="IUnitOfWork"/>.</typeparam>
    /// <param name="hostBuilder">The host builder.</param>
    /// <param name="foundation">Service name and durable-store settings.</param>
    /// <param name="configure">Additional Wolverine configuration, such as transports and routes.</param>
    public static IHostBuilder UseMPCoreWolverine<TContext>(
        this IHostBuilder hostBuilder,
        WolverineFoundationOptions foundation,
        Action<WolverineOptions>? configure = null)
        where TContext : DbContext, IUnitOfWork
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);

        // The named owner is authoritative. A host that registered several contexts would otherwise
        // resolve IUnitOfWork to whichever was registered first while Wolverine opened a transaction on
        // this one — the two would disagree silently. One host, one unit-of-work owner.
        hostBuilder.ConfigureServices(services => services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<TContext>()));

        return hostBuilder.UseMPCoreWolverine(foundation, options =>
        {
            options.UseEntityFrameworkCoreTransactions().WithDbContextAbstraction<IUnitOfWork, TContext>();
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Composes the durable messaging foundation without naming a transaction owner. Handlers must
    /// then take the concrete <c>DbContext</c> for the transactional middleware to apply; prefer the
    /// generic overload, which keeps handlers on the <see cref="IUnitOfWork"/> port.
    /// </summary>
    /// <param name="hostBuilder">The host builder.</param>
    /// <param name="foundation">The messaging identity and outbox storage.</param>
    /// <param name="configure">Optionally adds product transports and policies.</param>
    public static IHostBuilder UseMPCoreWolverine(
        this IHostBuilder hostBuilder,
        WolverineFoundationOptions foundation,
        Action<WolverineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(foundation);
        foundation.Validate();

        hostBuilder.ConfigureServices(services =>
        {
            services.AddSingleton(foundation);
            services.AddScoped<IMessagePublisher, WolverineMessagePublisher>();
        });

        hostBuilder.UseWolverine(options =>
        {
            // First, before anything asks Wolverine for its application assembly: see ApplicationAssembly.
            var applicationAssembly = foundation.ApplicationAssembly ?? Assembly.GetEntryAssembly();
            if (applicationAssembly is not null)
            {
                options.ApplicationAssembly = applicationAssembly;
            }

            options.ServiceName = foundation.ServiceName;
            options.PersistMessagesWithPostgresql(
                foundation.PersistenceConnectionString,
                foundation.PersistenceSchemaName);
            options.Policies.UseDurableLocalQueues();
            options.UseEntityFrameworkCoreTransactions();
            options.Policies.AutoApplyTransactions();

            // A handler that returns a failed result after changing tracked state must not commit it.
            // Applied to handlers that declare IUnitOfWork; see ResultFailureRollback.
            options.Policies.Add(new ResultFailureRollbackPolicy());

            // An attempt that failed takes its messages with it, so a retry starts with none; see
            // HandlerAttemptMiddleware.
            options.Policies.AddMiddleware(typeof(HandlerAttemptMiddleware));
            options.Policies.Add(new HandlerAttemptPolicy());

            // A handler with no request behind it runs as a named system actor, save included; see
            // HandlerActorMiddleware.
            options.Policies.AddMiddleware(typeof(HandlerActorMiddleware));

            // A broken business rule is a verdict, not a transient fault: every retry would replay the
            // same verdict against the same state. The message goes straight to the dead-letter queue,
            // where it stays visible. An inline invocation still receives the exception unchanged.
            options.OnException<BusinessRuleValidationException>().MoveToErrorQueue();
            configure?.Invoke(options);
        });

        return hostBuilder;
    }
}
