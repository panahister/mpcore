# Getting started

From nothing to a running backend with a secured endpoint, a command, a business rule and a test.

## What you need

- The .NET SDK `10.0.400` or a later patch of it.
- PostgreSQL. Every MP Core backend has one database, which also holds the message outbox.
- What your options need: Kafka or RabbitMQ for `--messaging`, Redis for `--cache redis` or `hybrid`,
  an OpenID Connect identity provider such as Keycloak for tokens.
- On an Apple Silicon Mac, either Rosetta or `brew install protobuf grpc`, to compile `.proto` files.

The [Storefront sample](https://github.com/panahister/mpcore-storefront-sample) brings all of these in one
`docker compose` file, which is the fastest way to have them.

## 1. Install the generator and the template

```bash
dotnet tool install --global MPCore.Cli --version 0.9.0
dotnet new install MPCore.Templates::0.9.0
```

The two are one cohort and must be of one version; the generator checks.

## 2. Generate

```bash
mpcore new backend --organization Acme --component Orders --output ./orders \
  --shape service --transport rest --messaging none --cache memory
```

| Option | Choose |
|---|---|
| `--shape` | `service` for one bounded context; `modular-monolith` for several in one host, one project each |
| `--transport` | `rest`, `grpc` or `both`. Required: the protocol surface is a decision, never a default |
| `--messaging` | `kafka`, `rabbitmq`, or `none` for durable local queues only |
| `--cache` | `none`, `memory`, `redis`, or `hybrid` for in-process plus Redis |
| `--business-audit` | `postgresql` to record who changed what, in the transaction of the change |
| `--timeseries` | `timescale` for hypertables |

Or start from a preset: `mpcore new backend --preset api ...`; `mpcore new backend --list-presets`
shows them. At a terminal, leaving options out starts a short wizard.

What you get is a solution that builds, with `docs/getting-started.md` written for the options you chose.
Follow that file to configure the database and the identity provider and to run the host.

## 3. Write a use case

A use case is one file: a message and the static method that handles it.

```csharp
public sealed record PlaceOrder(string Sku, int Quantity) : ICommand<Result<OrderView>>;

public static class PlaceOrderHandler
{
    public static async Task<Result<OrderView>> Handle(
        PlaceOrder command,
        ICurrentActorAccessor actor,     // who is asking: from the token, never from the request
        IOrderRepository orders,         // a port this module defines
        IMessagePublisher publisher,     // messages leave with the commit
        IUnitOfWork unitOfWork,          // declaring it puts the handler in a transaction
        IClock clock,
        CancellationToken cancellationToken)
    {
        var order = Order.Place(actor.Current.SubjectId!, command.Sku, command.Quantity, clock.UtcNow);
        orders.Add(order);
        await publisher.PublishAsync(new OrderPlaced(order.Id), cancellationToken);
        return Result<OrderView>.Success(OrderViews.Of(order));
    }
}
```

Three things the handler does not do, because the framework does:

- **It does not save.** Declaring `IUnitOfWork` is what places the handler in a transaction. After the
  handler returns, MP Core saves and commits, and only then releases what was published.
- **It does not validate the shape of its input.** A FluentValidation validator for `PlaceOrder`, in the
  same assembly, runs first; an invalid command never arrives.
- **It does not build an error response.** It returns a failure, or the aggregate throws a broken rule.

## 4. Write the rule

A business rule is a named class. The aggregate checks it before it changes anything.

```csharp
public sealed class QuantityMustBePositive(int quantity)
    : BusinessRule("acme.orders", "QUANTITY_NOT_POSITIVE", "orders.quantity_not_positive")
{
    public override bool IsBroken() => quantity <= 0;
}

public static Order Place(string buyerId, string sku, int quantity, DateTimeOffset now)
{
    CheckRule(new QuantityMustBePositive(quantity));
    ...
}
```

A caller that breaks it receives `422` with the code `acme.orders/QUANTITY_NOT_POSITIVE` and the text of
`orders.quantity_not_positive` in the language they asked for.

## 5. Expose it

```csharp
orders.MapPost("/", static async (PlaceOrder request, IMessageBus bus, CancellationToken ct) =>
        (await bus.InvokeAsync<Result<OrderView>>(request, ct))
        .ToHttpResult(order => Results.Created($"/v1/orders/{order.OrderId}", order)))
    .RequireAuthorization("orders.customer");
```

Every endpoint without such a line still needs a token: MP Core protects by default, and an endpoint is
anonymous only where you say so.

## 6. Test it

A handler takes its collaborators as parameters, so a test calls it with fakes: no container, no database,
no mocking library.

```csharp
var result = await PlaceOrderHandler.Handle(
    new PlaceOrder("TNT-1", 2), FakeActor.User("sara"), orders, publisher, new FakeUnitOfWork(), clock,
    CancellationToken.None);

Assert.True(result.IsSuccess);
Assert.Single(publisher.OfType<OrderPlaced>());
```

## Where to go next

- [Backend engineering conventions](../BACKEND-CONVENTIONS.md): decide ownership and carry a vertical slice from requirement to connected evidence.
- [Concepts](concepts.md): what happens around your handler, and what is guaranteed.
- The sample, for a whole business: four modules, three hosts, and scenarios you can run and read.
