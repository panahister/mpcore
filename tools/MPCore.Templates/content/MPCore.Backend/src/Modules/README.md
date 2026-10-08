# Bounded Context Modules

Create a module only from an approved and final Bounded Context. Use the repository's
`mpcore-implement-ddd-module` skill before adding business code.

## Shape of a module

A module is **one project**, with the three layers as folders inside it. A second, small project holds
only what other modules are allowed to use.

```text
src/Modules/<Context>/
  <Product>.Modules.<Context>/
    Domain/                aggregates, entities, value objects, business rules, domain events
    Application/
      Commands/            one file per use case: the command record and its handler
      Queries/             one file per use case: the query record and its handler
      Views/               what queries and commands return (read models, output DTOs)
      Ports/               interfaces this module needs from outside: repositories, gateways
      Process/             process managers that react to other modules' answers
      Events/              handlers of this module's own domain events (cache eviction, alerts)
      Validators/          FluentValidation validators of the commands' input
      Contracts/           implementations of the interfaces published in the Contracts project, if any
    Infrastructure/        EF Core mappings, repository and gateway adapters, module registration
    Resources/             <Context>Messages.resx and one file per language
    AssemblyReference.cs
  <Product>.Modules.<Context>.Contracts/     only when other modules call this one
    messages and interfaces other modules may use, nothing else
```

Namespaces follow the folders: `<Product>.Modules.<Context>.Application.Commands`, and so on.

Dependencies:

- A module references `MPCore.*` abstraction packages, its own Contracts project, and the Contracts
  projects of the modules it talks to. **It never references another module's main project.**
- Nothing in a module references the host (`Api`) or the host's `Infrastructure` project.
- The host's `Infrastructure` project references each module only to apply its EF mappings to the one
  `AppDbContext`. The host's `Api` project references each module to register it.

### Why one project per module, and not three

The strongest boundary in a modular monolith is the one between bounded contexts. Eric Evans introduced
the bounded context in *Domain-Driven Design* (2003) as the limit inside which one model and one language
hold. Keeping two contexts apart is what allows each to change on its own. The layers inside one context
are a detail of how that context is built.

Simon Brown makes this argument in "The Missing Chapter" of Robert C. Martin's *Clean Architecture*
(2017). He calls the approach **package by component**: group code by the business capability it serves,
and let the compiler enforce the boundary between components rather than the boundaries between layers.
A project per module does exactly that. Ordering cannot use `Product` from Catalog because it cannot see
it; only Catalog's Contracts project is visible to it.

The alternatives were considered:

| Layout | Projects for four modules | Boundary between modules | Boundary between layers |
|---|---|---|---|
| Three projects per module | 12 | compiler | compiler |
| Three shared projects, modules as folders | 3 | tests only | compiler |
| **One project per module, layers as folders** | 4, plus Contracts | **compiler** | tests only |

- **Three projects per module** is the layout of Kamil Grzybek's *Modular Monolith with DDD* reference
  application, and of the Ordering service in Microsoft's eShop. It enforces everything, but most
  projects hold a few files, and every new module costs three project files. eShop itself splits only
  Ordering, whose domain is rich, and keeps Catalog and Basket as single projects.
- **Three shared projects** is the layout of Jason Taylor's and Steve Smith's (Ardalis) Clean
  Architecture templates. Those templates model **one** bounded context. In a modular monolith this
  layout loses the boundary that matters most.
- **One project per module** keeps the context boundary in the compiler and moves the layer boundary to
  architecture tests, which check namespaces: `Domain` uses no Entity Framework, no ASP.NET and no broker.

If one module's domain later grows rich enough to deserve the stronger split, that module alone can be
divided into Domain, Application and Infrastructure projects. Nothing else has to change.

**The rules are tests.** `tests/<Product>.Tests/ModuleRulesTests.cs` checks every module listed in
`HandlerAssemblies`: a module's `Domain` folder depends on neither its `Application` nor its
`Infrastructure` folder nor on a provider (Entity Framework, Npgsql, Wolverine, a broker, ASP.NET, gRPC);
its `Application` folder depends on neither its `Infrastructure` folder nor on a provider; and no module
references another module's main project. A module that is not listed in `HandlerAssemblies` is not
checked, and has no handlers either.

**What `internal` can and cannot hide.** Wolverine generates each handler's code in its own assembly and
constructs the handler's dependencies there. Handlers, messages, ports and the adapters Wolverine
constructs must therefore stay `public`. The boundary between modules comes from project references,
not from `internal`.

### Why these folders inside Application

- **One use case per file, the message and its handler together.** Jimmy Bogard's *Vertical Slice
  Architecture* (2018) groups code by the request it serves, because a request and its handler change
  together. Wolverine's own documentation writes a message and its handler side by side for the same
  reason. A file per use case also follows the .NET convention of one public type per file, with the
  handler as the one companion that exists only for that message.
- **`Commands/` and `Queries/`.** Command-query separation comes from Bertrand Meyer; Greg Young carried it
  to CQRS. Microsoft's eShop keeps `Commands/` and `Queries/` apart in its Ordering service, and Jason
  Taylor's template does the same within each feature. The split tells a reader at a glance which files
  change state.
- **A query only reads.** It implements `IQuery<T>`, declares no `IUnitOfWork`, publishes nothing, and
  reads through a read-model port that returns views, never through a repository, which hands out
  aggregates. It is the only thing an HTTP `GET` sends: RFC 9110 requires `GET` to be safe, so a retry, a
  browser prefetch or a cache must never change anything. When reading has a consequence, such as marking
  something as seen, that consequence is a command of its own. This is Meyer's command-query separation
  applied to messages; Young's CQRS adds that the read side has its own model.
- **`Views/`.** A view is the data a query or command returns to its caller: a read model, in CQRS terms,
  and a data transfer object (DTO) in general terms. "View" names its role more precisely than "DTO",
  because a command and an integration event are DTOs too. eShop calls the same types view models.
- **`Ports/`.** Alistair Cockburn introduced **Ports and Adapters**, also called Hexagonal Architecture, in
  2005. A port is an interface the application defines for what it needs from the outside; an adapter in
  `Infrastructure/` implements it. Other templates name this folder `Interfaces` or `Abstractions`; the
  meaning is the same.
- **`Process/`.** A class that reacts to other modules' answers and decides the next step is a **Process
  Manager**, a pattern Gregor Hohpe and Bobby Woolf describe in *Enterprise Integration Patterns* (2003).
  It is also called an orchestration-based saga. It chooses the next command; the rules stay in the
  aggregate. A command that a person starts, such as cancelling an order, belongs in `Commands/` even when
  it also sends compensating messages.
- **`Events/`.** Reactions to the module's own domain events, delivered in-process after the commit: a
  cache eviction, an alert to purchasing. They are handlers like any other, named `...Handler`.
- **`Validators/`.** Input validation checks the shape of a request: a required field, a length, a phone
  number format. It runs before the handler; see the next section.
- **`Contracts/`.** The module's implementations of the interfaces it publishes in its Contracts project,
  for example a lookup another module reads prices through. Application code, because it is a use case
  of this module driven from outside. A module that publishes only messages has no such folder.

## How one module makes another change

**A module writes only its own data.** When a use case of one module needs another module to change,
there are two ways, and both are supported:

| | A message | A call through Contracts |
|---|---|---|
| What it is | Publish a module message with `IMessagePublisher`. It leaves with your transaction (the outbox); the other module handles it from a durable local queue, in its own transaction. | Call an interface the other module publishes in its Contracts project, inside your transaction. |
| Consistency | Eventual: your answer can say "accepted", not "done". | Immediate: both change, or neither. |
| The receiver must be | Idempotent by a business key: a message can arrive twice. | Correct. |
| The other module refuses | Later, in a message; you answer with a compensation. | At once, as your own failure. |
| The module becomes a service | The message changes its transport. | The transaction is redesigned. |

**Use a message unless you have a reason not to.** Vaughn Vernon's aggregate rule (*Implementing
Domain-Driven Design*) is one aggregate per transaction and eventual consistency between them. Kamil
Grzybek's *Modular Monolith with DDD* integrates modules through events only. Microsoft's eShop checks out
by publishing a snapshot of the basket, from which Ordering creates the order. The class that receives
the answers and chooses the next step is the process manager in `Process/`.

**Use a call that writes only when** the two modules must change together **and** are meant to stay in
one deployment. Write that reason where the interface is declared, so the next reader knows it was a
decision.

**A call that reads is always fine.** It couples nothing a cache or a local copy could not replace.

When you publish a message for another module:

- Put a **snapshot** in it, everything the receiver needs. A receiver that has to call back for the rest
  has only moved the coupling.
- Give the thing being created its **identity** in the message (a version 7 UUID needs no coordination).
  Every receiver is then idempotent by it, and the caller has something to follow.
- Check **at your own edge** what the receiver would refuse. Once you have answered, the receiver's refusal
  can reach only an operator. Keep the two modules' rules together with a test that runs what your
  validator accepts against the receiver's rules; Ian Robinson described this as *consumer-driven
  contracts*.
- A module message **stays in the host**. It is not an integration event and has no broker route, so it
  may carry what an integration event must not, such as a payment token.

## Rules, validation and messages

- **Business rules live in the aggregate.** Derive a rule from `MPCore.Domain.Rules.BusinessRule` and check
  it with `CheckRule(...)` before the aggregate changes state. The pattern of a named rule object comes from
  Kamil Grzybek's reference application; keeping the invariant inside the aggregate comes from Evans and
  Vaughn Vernon (*Implementing Domain-Driven Design*, 2013), and Vladimir Khorikov calls the result an
  *always-valid domain model*. A rule has an error domain, a stable UPPER_SNAKE code and a message key,
  never a sentence.
- **Input validation uses FluentValidation**, written by Jeremy Skinner. A validator per command, in
  `Validators/`, registered by `AddMPCoreValidators`. MP Core runs it as Wolverine middleware before the
  handler, as Microsoft's eShop does for its Ordering commands. A validator never reads the database: a
  check that needs state is a business rule. Name a rule's code with `WithErrorCode("PHONE_FORMAT")` and
  its message key with `WithMessage("ordering.phone_format")`.
- **Value objects over primitives.** A price, a SKU or a phone number is a value object with its rule
  inside, not a `decimal` or a `string`. Evans introduced value objects; Vernon recommends preferring them
  to entities wherever identity does not matter. Martin Fowler and Kent Beck named their absence
  *primitive obsession* in *Refactoring* (1999).
  Derive from `MPCore.Domain.Model.ValueObject`, or write a `record` for a simple one. A child entity
  inside an aggregate derives from `Entity<TId>`.
- **Messages are keys, translated at the edge.** Each module has a resource file,
  `Resources/<Context>Messages.resx`, with one culture file per language, registered with
  `AddMPCoreMessageCatalog(catalog => catalog.AddResources<<Context>Messages>())`. The transport renders the
  key in the language the caller asked for (RFC 9457 keeps problem text for people and codes for
  programs). Translations an administrator edits at run time come from the optional package
  `MPCore.Localization.EntityFrameworkCore.PostgreSql`.

## Registering a module — explicit lines, no scanning

1. **Services.** Give the module one registration entry point, `AddXModule<TContext>(this IServiceCollection, …)`,
   in its `Infrastructure/` folder, and call it from the host next to `AddInfrastructure`. One entry point
   per module keeps composition reviewable.
2. **Handlers and validators.** Add the module's `AssemblyReference` to
   `src/<Product>.Api/Hosting/HandlerAssemblies.cs`. A module that is not listed there has no handlers and
   no validators, however complete its code looks: Wolverine discovers handlers only in the assemblies the
   host names.
3. **Mappings.** Apply the module's EF configurations in the host's `AppDbContext`, with
   `ApplyConfigurationsFromAssembly(<Product>.Modules.<Context>.AssemblyReference.Assembly)`.
4. **Messages.** Add the module's resource file to the message catalog in `Program.cs`.

### Two rules Wolverine enforces at run time, not at compile time

- **Register adapters by type, never with a lambda.** Wolverine generates each handler's code with its
  dependencies constructed inline, and refuses anything it could only obtain by service location
  (ADR-011 §7). `services.AddScoped<IOrderRepository>(sp => new OrderRepository(...))` compiles, and the
  first message fails with `InvalidServiceLocationException`. Make the adapter generic over the host's
  context instead — `OrderRepository<TContext>(TContext database)` — and register
  `services.AddScoped<IOrderRepository, OrderRepository<TContext>>()` in `AddXModule<TContext>`. The adapter
  then receives the very context instance whose transaction the handler runs in.
- **Name handler classes `...Handler` or `...Consumer`.** Wolverine discovers handlers by that convention.
  A class called `OrderProcess` with perfectly good `Handle` methods is never discovered; its messages have
  no route, and a published message with no route is dropped with only an informational log line.

Routes for integration events are declared per module as well: name the topic, exchange or queue that
the module owns. There is no catch-all publication anywhere in this repository.
