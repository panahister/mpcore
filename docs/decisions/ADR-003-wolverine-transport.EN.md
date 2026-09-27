# ADR-003 — Wolverine core with explicit Kafka and RabbitMQ adapters

- Status: Accepted
- Date: 2026-08-31

## Decision

Wolverine replaces MassTransit and CAP. Durable local queues and PostgreSQL persistence form the common core. Kafka is the default adapter for cross-service Integration Events. RabbitMQ is a separately referenced optional adapter for approved command/work-queue topologies. A generated backend selects an adapter at generation time; a runtime configuration switch does not bring both broker SDKs into every service.

## Guardrails

- Product code depends on `IMessagePublisher`, not transport SDKs.
- Only approved, versioned integration events cross service boundaries through Kafka.
- Topic/exchange naming and event versioning are explicit service contracts.
- Catch-all `PublishAllMessages()` routing is prohibited; every producer and consumer route is registered by its owning Bounded Context.
- Internal modular-monolith commands use durable local queues and PostgreSQL, not Kafka by default.
- At-least-once delivery, idempotency, partition/order, retry, dead-letter, and replay behavior are explicit per contract.
- Saga state belongs to the service that owns the process.
- Automatic broker provisioning is a development convenience and must be reviewed for production.
