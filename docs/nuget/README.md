# MP Core

A backend framework for .NET 10 that makes the decisions every backend makes again, once, with a test for
each: how a command runs, who opens and commits the transaction, how a message leaves with that
transaction, how a failure becomes an HTTP or gRPC answer, how a token is validated, where logs and traces
go.

This package is one of a cohort that shares one version. Most projects do not reference the packages by
hand: they are generated.

```
dotnet tool install --global MPCore.Cli
dotnet new install MPCore.Templates
mpcore new backend --organization Acme --component Orders --output ./orders
```

| You write | MP Core does |
|---|---|
| A handler: a static method that takes a command and the ports it needs | Opens the transaction, runs validators first, saves, commits, and only then releases the messages the handler published |
| A business rule as a named class, checked by the aggregate | Reports it under its own code, as Problem Details or a rich gRPC status, in the caller's language |
| `RequireIdempotencyKey()` on an endpoint | Stores the key and the answer in the transaction that commits the change; a repeat receives the stored answer |
| An integration event raised by an aggregate | Delivers it to Kafka or RabbitMQ after the commit; a consumer's inbox stops a second delivery |
| Nothing | Token validation, protect-by-default endpoints, a named actor in every audit record, telemetry with redaction |

## Learn more

- Source, documentation and the reference architecture: https://github.com/panahister/mpcore
- A complete example, three backends behind a gateway: https://github.com/panahister/mpcore-storefront-sample
- Release notes: https://github.com/panahister/mpcore/tree/main/docs/releases

Licensed under Apache-2.0.
