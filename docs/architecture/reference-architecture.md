# Reference architecture

A backend platform from the caller to the database, and where MP Core sits in it. For every part this
document says what the part is for, what MP Core does with it, what MP Core deliberately leaves to it, and
how far the combination was proved.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../images/reference-architecture-dark.svg">
  <img alt="A backend platform: an API gateway, an identity provider, backends built on MP Core, Kafka and RabbitMQ, PostgreSQL, TimescaleDB and Redis, and OpenTelemetry" src="../images/reference-architecture-light.svg" width="100%">
</picture>

**How to read "proved".** MP Core relies on standards, not on products. A product that speaks the
standard fits; that is a claim about design. *Run* is a claim about evidence: the
[Storefront sample](https://github.com/panahister/mpcore-storefront-sample) starts the product and its
scenarios pass through it. This document never says *run* about something that was only designed for.

| Part | Product | State |
|---|---|---|
| Edge | Apache APISIX | run |
| Edge | WSO2 API Manager, or another gateway | fits by standard; not run |
| Identity | Keycloak | run |
| Identity | WSO2 Identity Server, Microsoft Entra ID, another OpenID Connect provider | fits by standard; unit tests only |
| Messaging | Apache Kafka, RabbitMQ | run |
| Data | PostgreSQL, TimescaleDB, Redis | run |
| Observability | OpenTelemetry Collector, Jaeger, Prometheus, Grafana | run |

## 1. The principles the picture follows

| Principle | What it means here | Source |
|---|---|---|
| Every part has one job | The edge routes and ends TLS. The identity provider signs people in. A backend decides. A broker carries. None does another's job | Separation of concerns (Edsger Dijkstra) |
| Trust is never inherited | A backend validates every token itself, although the edge may have validated it already. It believes a forwarded header only from a proxy it was told to trust | Zero trust: *never trust, always verify* (NIST SP 800-207) |
| A bounded context owns its data | A backend never reads another backend's database. What it needs from another, it is told in a message | Eric Evans, *Domain-Driven Design*; Sam Newman, *Building Microservices* |
| Start with one deployment, split along a boundary that proved itself | A modular monolith whose modules are already separated by the compiler; a module leaves by changing the transport of its messages | Simon Brown; Sam Newman, *Monolith to Microservices* |
| A message leaves if, and only if, the change was committed | The transactional outbox, and an inbox on the other side | Chris Richardson, *Microservices Patterns*; Gregor Hohpe and Bobby Woolf, *Enterprise Integration Patterns* |
| What cannot be seen cannot be operated | Logs, traces and metrics from the first request, in one open format | OpenTelemetry; Cindy Sridharan, *Distributed Systems Observability* |

## 2. The edge

**What it is for.** One address for every caller. It ends TLS, routes a request to the backend that owns
the path, gives the request an identity, and limits what an anonymous caller may ask for.

**What MP Core does with it.**

| Concern | Behaviour | Where |
|---|---|---|
| TLS | Ends at the edge. Behind it the backends speak cleartext: HTTP/1.1 for REST, HTTP/2 for gRPC | ADR-009 |
| Two transports, one door | REST and gRPC are served on two listeners of a backend, and an endpoint is bound to its listener. A gRPC method cannot be reached on the REST port, whatever the `Host` header says | `TransportPortSeparation`, ADR-009 |
| Forwarded headers | `X-Forwarded-For`, `-Proto` and `-Host` are believed only from the addresses in `Gateway:TrustedProxies`. With an empty list they are ignored, so a caller cannot forge its address or the scheme | `UseMPCoreGatewayForwarding` |
| Identity headers | Headers a gateway might add and a caller could forge (`X-Forwarded-User`, `X-User-Id`, `X-Consumer-Username` and others) are removed before authentication | `ForwardedIdentityHeaderGuard` |
| The request's identity | The `X-Request-Id` the edge assigns is the identity the backend gives the request, and returns in its answer and in a failure | `MPCore.Transport.Http`, `MPCore.Transport.Grpc` |

**What MP Core leaves to it.** Certificates, rate limits, routing, and anything about the caller's
network. A backend has no opinion on these.

**What the edge must never be.** The only wall. A gateway that validates tokens is one more boundary,
never a replacement: a backend that trusts the edge to have checked is open to everything behind the edge
(ADR-007).

**Apache APISIX: run.** The sample puts APISIX in front of its three backends, in standalone mode, with
its routes in one file. Scenario S20 proves, through the edge: REST and gRPC over TLS to three backends;
the request's identity in the backend's answer; the public address in the API description, which the
backend knows only from the forwarded headers; a forged identity header that changes nothing; a rate limit
that answers 429 while the backend itself still answers.

**WSO2 API Manager, or another gateway: fits by standard, not run.** What a gateway has to do is listed
above and is not specific to a product: pass the `Authorization` header on, set the `X-Forwarded-*`
headers, and route gRPC as HTTP/2. A gateway is configured to do these; nothing in a backend changes
with the product. This project has not started WSO2 API Manager and run its scenarios through it, and
will say so here when it has.

## 3. Identity

**What it is for.** Signing people and systems in, and issuing tokens that say who they are and what they
may do. MP Core never does this: a backend is a bearer-only resource server and hosts no login, signup,
password or callback (ADR-007).

**What MP Core does with it.**

| Concern | Behaviour |
|---|---|
| Validation | Signature, issuer, lifetime and **audience**, on every request. A token names the backends it is for, and a backend refuses one that does not name it |
| Default | Deny. An endpoint without a policy requires a token; the only anonymous endpoints are the ones that say so |
| The actor | A user, a service, or the system itself. Business code reads `ICurrentActorAccessor`; it never reads a header, a route value or a body to learn who is calling |
| Roles | Mapped from the token by a preset for the provider, or by a plain mapping |
| Work with no caller | A queued handler runs as a named system actor (`system:ReserveStock`), so that no audit record is anonymous |

**Keycloak: run.** `UseKeycloakDefaults` reads realm roles, client roles and the service-account
convention. The sample's realm has five roles, a web client and a service client, and one audience per
backend; its scenarios ask every backend with no token, with the wrong role, and with a token issued for
another backend.

**Another OpenID Connect provider: fits by standard, unit tests only.** The plain mapping reads roles
from a flat or a nested claim, with no provider's name in the code. WSO2 Identity Server and Microsoft
Entra ID issue such tokens. This project has not run a backend against them.

## 4. The backends

**Two shapes.** A *service* is one bounded context in one deployment. A *modular monolith* is several in
one deployment, each a project of its own, so that the compiler refuses a module that reaches into
another. Both are generated by the same command and use the same packages.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../images/layers-dark.svg">
  <img alt="Four layers: the API host, the application, the domain and the infrastructure" src="../images/layers-light.svg" width="100%">
</picture>

**One request.**

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../images/request-dark.svg">
  <img alt="A request passes through transport, security and validation, reaches the handler, is committed together with its messages, and the messages are released" src="../images/request-light.svg" width="100%">
</picture>

| Step | What happens | What is guaranteed |
|---|---|---|
| Transport | REST or gRPC, on its own listener | A failure looks the same on both: a category, an error domain, a code, a message key |
| Security | The token is validated; the policy of the endpoint is checked | Deny by default |
| Validation | The shape of the request is checked | An invalid request never reaches the handler; the caller receives one violation per field |
| Handler | Your code: it changes an aggregate, which checks its rules | A broken rule is reported under its own code, and is never retried |
| One commit | The change, the messages, the audit record and the idempotency key are saved together | All, or none. A failure returned after a change rolls the change back |
| Messages | Released after the commit | A message of an attempt that failed is discarded with it |

**Wolverine** runs the handlers. MP Core configures it: which middleware runs and in what order, that a
transaction wraps every handler that declares a unit of work, that the outbox is durable, and what is
retried. A product does not configure Wolverine to be safe; it is handed a Wolverine that is.

## 5. Messaging

| Broker | Carries | Pattern | Why |
|---|---|---|---|
| Apache Kafka | What happened, in order, for any number of readers | Publish-Subscribe Channel | A reader that arrives later reads the stream from its start. Order is kept by partitioning on the aggregate's identity |
| RabbitMQ | Work for one reader, and its answer | Point-to-Point Channel | One consumer takes a message and acknowledges it |
| Durable local queues | Messages between the modules of one backend | In PostgreSQL, in the backend's own database | No broker is needed for a module to tell another |

The pattern names are Hohpe's and Woolf's. A backend may use both brokers, each for its job; the sample's
Commerce backend does.

**Three kinds of message.** A *domain event* is handled in the same backend, after the commit. A *module
message* tells another module of the same backend. An *integration event* crosses to another backend, with
a name and a version that are a contract.

**Two services share no code.** The publisher declares a message in its own code; the reader declares its
own copy, with the properties it uses. A test reads what one sends with the other's copy (Ian Robinson's
consumer-driven contracts).

## 6. Data

| Store | Used for | What MP Core adds |
|---|---|---|
| PostgreSQL | The business data of a backend, one schema per module | The unit of work; the outbox and the inbox; the audit trail; idempotency keys; stored translations. All in the backend's own database, so that they commit with the change |
| TimescaleDB | Time series | A hypertable is created in a migration, like any table |
| Redis | A cache shared by the instances of a backend | One port, `IReadThroughCache`, over memory, Redis, or memory in front of Redis |

A backend that loses its cache still works; a backend that loses its database is not ready, and says so.

## 7. Observability

Logs, traces and metrics leave a backend over OTLP, each signal to a destination of its own. A trace
crosses backends: the trace context travels in the headers of a request and of a message (W3C Trace
Context), so the request at the edge, the handlers, the SQL, the message and its consumer in another
service are spans of one trace.

| Concern | Behaviour |
|---|---|
| Secrets | Masked in log attributes and trace tags before they leave the process |
| Messages | A message that carries something personal or secret overrides `ToString`, because a failed message is printed into the log |
| Health | Two questions. *Alive* asks the process only. *Ready* asks the database. Over REST and over gRPC |
| Failure of the telemetry backend | Never fails a request |

## 8. Delivery

| Concern | Behaviour |
|---|---|
| Continuous integration | Every change is restored in locked mode, built with warnings as errors, and tested against real PostgreSQL, TimescaleDB and Redis |
| Publication | By one workflow, started by hand and approved by a maintainer. nuget.org trusts the workflow and gives each run a key that lives one hour; no key is stored (ADR-004) |
| Versions | A published version is never built again (ADR-010) |
| Deployment | Not MP Core's. It generates no image, chart or pipeline that deploys: that belongs to the platform a backend runs on |

## 9. Trust boundaries

| From | To | What is believed | What is not |
|---|---|---|---|
| A caller | The edge | Nothing, until TLS | Everything else |
| The edge | A backend | The scheme, host and client address it forwards, **if** its address is a trusted proxy | Who the caller is. The token says that, and the backend validates it |
| A backend | Another backend | A message whose contract it knows | That it arrives once, or in order across aggregates |
| A module | Another module | A message, or a read through its `Contracts` | Its tables |
| A backend | Its broker | That a message is delivered at least once | That it is delivered exactly once |

## 10. Adopting it

| Decide | The question | MP Core's default |
|---|---|---|
| The shape | Do these contexts change, release and scale together? | A modular monolith, until a context has a reason of its own to be deployed alone |
| The transport | Who calls: people through a browser, or systems? | REST for the first, gRPC for the second; both on separate ports |
| The broker | Is this a fact for anybody, or work for one? | Kafka for the first, RabbitMQ for the second |
| The edge | Who ends TLS and routes? | Any gateway that forwards the token and the `X-Forwarded-*` headers. List its address in `Gateway:TrustedProxies`, and nothing wider |
| Identity | Who signs people in? | Any OpenID Connect provider. One audience per backend |
| The cache | Can this be a few seconds old? | A read-through cache with a lifetime; evict after the commit |

Then generate a backend with those choices, and read the sample with it open: the sample makes every one
of them, and says what each costs.
