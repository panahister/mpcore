# ADR-018 — A sensitive value that never prints

- Status: Proposed; pending owner acceptance
- Date: 2026-10-08
- Extends: ADR-005 (OpenTelemetry and logging), ADR-007 (reusable security and current actor)

## Context

One-time codes and user tokens pass through services built on MP Core. They must reach no log, trace,
metric, exception or error report. MP Core masked them by attribute name only, and in OpenTelemetry only:
`SensitiveLogRecordProcessor` and `SensitiveActivityProcessor` replace the value of an attribute whose name
is in `SensitiveDataPolicy.DefaultSensitiveFields`. A value still leaked in four ways:

1. an attribute named otherwise, or named inside a dotted name (`http.request.header.authorization`) or a
   nested collection of named values;
2. a protobuf message logged whole: Google.Protobuf prints every field, even one marked `debug_redact`;
3. an exception message built from the value;
4. any logging provider that is not OpenTelemetry, such as the console provider ASP.NET Core adds.

The OWASP Logging Cheat Sheet lists access tokens, passwords and other authentication secrets under "data to
exclude" from logs. Masking by name protects only the names someone thought of.

## Decision

### 1. `SensitiveValue`, a value that masks itself (`MPCore.Application`)

| Rule | Why |
|---|---|
| `ToString()` returns `***`; so do interpolation, `string.Format`, `StringBuilder`, a record's printed members and an exception message built from it | they all call `ToString()`. This covers every sink, OpenTelemetry or not: the value never becomes its text |
| System.Text.Json writes `***`; it reads a string into the value, `null` as no value, and refuses any other token with a `JsonException` that does not carry the value | a response or a serialized command never carries it; a request can. A body that holds a number or an object where the code should be is the caller's mistake and is answered with 400; any other exception type would be answered with 500, as a fault of the host |
| The debugger shows `***`; its field is hidden from the debugger | `DebuggerDisplay` and `DebuggerBrowsable(Never)` |
| The value leaves only through `Reveal()` | one method to search for in a review; there is no conversion to `string` |
| Equality, `==` and `FixedTimeEquals(string)` compare in constant time | `CryptographicOperations.FixedTimeEquals`: comparing a submitted code with a stored one does not disclose how much of it matched. Values of different lengths are unequal without comparing content |

It lives in `MPCore.Application`, the package every product Application project references, and needs no
dependency beyond the base class library.

### 2. The name processors mask nested attributes

The name list is unchanged. A name is sensitive when it is in the list, or when any segment of a dotted or
colon-separated name is (`http.request.header.authorization`, `user.password`). A value that is a collection
of named values is masked inside, to a depth of four, and a masked string is scrubbed from the formatted
message. A `SensitiveValue` attribute or tag is exported as `***` whatever its name.

### 3. The messages of a named gRPC service never print whole (`MPCore.Transport.Grpc`)

`services.AddGrpc().AddMPCoreSensitiveMessages("package.Service")` adds an opt-in startup filter. When the
host starts, with every endpoint mapped and before the server listens, it reads the method descriptor of each
endpoint of a named service (`Method<TRequest, TResponse>`, from the endpoint's `GrpcMethodMetadata`) and adds
the request and response types to the host's `SensitiveMessageTypes` (`MPCore.Application`). The log and trace
processors mask whole any attribute or tag that holds an object of such a type, and scrub its text from the
formatted message. A message is therefore masked from host start: from the application-started callback and
from every call, whether a handler or a startup check logs it, and no call is needed first.

Not covered: a request object of a named service that is logged before the registry is filled. The fill runs
when the web host starts, in the web host's own hosted service; a hosted service added with `AddHostedService`
starts before it, and code between `Build` and `Run` runs before any of them. Such a log prints the object
whole, on the console and in the log export. This was seen in one host with all three logs of the same request:
the hosted service's and the one between `Build` and `StartAsync` printed `{ "phone": ..., "code": ... }`, and
the application-started callback printed `***`. The mechanism is unchanged. A type that must be masked in that
window is registered in the service collection, with `services.AddMPCoreSensitiveMessageTypes(typeof(...))`,
because the registry holds such a type from the moment the container creates it; with the request type
registered that way, the same two logs printed `***`. A host can add any other type the same way, for example
the messages of a client.

The registry is a service of the host's container, one per host, and the processors take it from there. The
first version of this decision kept it in a static, filled by an interceptor at the first call of a method. That
had three faults: a message logged before the first call printed whole; what one host named reached every host
of the process, so a test passed or failed by the order of its neighbours; and nothing could be removed or
replaced. The startup filter is the moment the resource-key check of ADR-007 already uses to see every
endpoint. The host fails to start when a name is that of no mapped service, because a mistyped name would
leave the service unmasked without a sign of it.

`MPCore.Observability` references `MPCore.Application` for this, the registry both packages share. A
transport package may not reference a security package (ADR-007), so the registry is not in
`MPCore.Security.Abstractions`.

### 4. A generated host prints through the pipeline only (`MPCore.Observability`, the template)

The masking runs in MP Core's OpenTelemetry processors, and `WebApplication.CreateBuilder` adds three logging
providers beside them: console, debug and event source. Each prints a log argument as the argument prints,
so a protobuf request of a named service reached the console whole, a one-time code and a token included.
The template now closes this:

- `builder.Logging.ClearProviders()` comes before `AddMPCoreFoundation`. The order matters: `ClearProviders`
  removes every provider registered so far, MP Core's own included.
- `MPCoreObservabilityOptions.EnableConsoleLogExporter` (off by default for a library; the template turns it
  on with `Observability:EnableConsoleLogExporter`, `true`) adds a console sink to the log pipeline, after the
  redaction processor. A host with no provider still shows its logs, and what it shows has been masked. The
  sink prints the time, the level, the category, the event id, the formatted message and the exception; it
  does not print attributes, which are already in the message, or scopes, which are not redacted.
- The convention is Microsoft's own: the default host builder adds the console, debug and event-source
  providers (and the event-log provider on Windows), and `ILoggingBuilder.ClearProviders()` is how a host
  that wants a different set starts from none (Microsoft Learn, "Logging in .NET and ASP.NET Core"). It was
  chosen over wrapping every provider in a redacting logger because one pipeline masks once, and a provider
  added later cannot skip it.

A product generated before this change keeps the providers it has; it moves by adding the two lines.

## What was proved

| Test | What it shows |
|---|---|
| `SensitiveValueTests`, 6 | every rendering shows the mask; JSON writes it and reads a value; the debugger shows the mask; the value leaves only through `Reveal()`; equality by value in constant time; null is refused |
| `SensitiveDataTests.A_sensitive_value_reaches_no_sink` | a known value, logged as a structured attribute, inside a record, in an interpolated message and in an exception's message, and set as a trace tag, appears in none of: the OpenTelemetry log and trace export, the JSON console provider, a plain provider |
| `SensitiveDataTests.The_name_processors_also_mask_nested_attributes` | `http.request.header.authorization` and a password inside a nested collection are masked in logs and traces; a neighbouring order id is kept |
| `SensitiveDataTests.The_name_list_is_unchanged` | the 22 names |
| `SensitiveDataTests.An_object_of_a_sensitive_message_type_is_masked_whole` | an object of an added type is masked, and its text is scrubbed from the message |
| `SensitiveMessageTests`, 5 | a gRPC handler of a named service logs its request and its response: both are masked, and neither the code (a `debug_redact` field), the phone nor the session appears in the message; the handler of a service that is not named is logged as it is. A message logged after the host started and before any call is masked (run alone against the static registry it printed `{ "phone": "+1-555-0100", "code": "552-118" }`; in the suite it passed only because another test had filled the static first). A message logged from the application-started callback is masked (seen failing when the filling adds the wrong type: `Expected: ***`, `Actual: { "phone": "+1-555-0100", "code": "552-118" }`). What one host names does not reach a second host of the process (seen failing when one registry is shared). A name that no mapped service has fails the start (seen failing: no exception) |
| `ConsoleLogTests`, 4 | the console sink is off unless a host turns it on; an entry is written after masking (a sensitive value, a sensitive name and a message type are `***`); scopes are not printed, an exception is; moving the sink before the redaction processor makes two of them fail |
| `TemplateContractTests.The_host_clears_the_default_logging_providers_before_the_foundation_registers_its_pipeline` | `ClearProviders` is in `Program.cs` and before `AddMPCoreFoundation`; no template file adds a console, debug, event-source or event-log provider; seen failing when `ClearProviders` is removed, when it comes after the foundation, and when `AddConsole()` returns |
| `GeneratedBackendTests.A_generated_host_prints_no_sensitive_request_to_its_console_and_the_default_providers_would` | a host generated from the template, with a gRPC service that logs its request, is built and run, and called once over HTTP/2: its console shows `Verify ***`, its other logs, and neither the phone, the code nor the field names. With `ClearProviders` removed the same call prints `Verify { "phone": "+1-555-0100", "code": "552-118" }`, and the test, run against the template as it was before this change, fails with that line |

Nine of the eleven were seen failing against stubs of the API; the two that passed, the members of the type
and the unchanged name list, are guards that a stub cannot fail.

## Alternatives that were not taken

| Alternative | Why not |
|---|---|
| Data classification with `Microsoft.Extensions.Compliance.Redaction` | a new dependency and a classification scheme for every product. It stays open: an adapter from `SensitiveValue` to a data classification can be added later without changing the type |
| A Roslyn analyzer that refuses a logging call with a message of a named service | it sees only direct calls, and adds a tool to build and ship. The interceptor works at run time on whatever path the object takes into a log |
| More names in the list | it would mask more fields named innocently, and still miss a value named otherwise |

## Consequences

- A product wraps a code or a token in `SensitiveValue` where it enters, and calls `Reveal()` only where it is
  consumed. Every sink then sees `***`.
- The name processors mask more, never less: a nested or dotted name that contains a sensitive one.
- The masking of names and of message types runs where MP Core's processors run: the OpenTelemetry logs and
  traces of `AddMPCoreObservability`. A generated host has no other provider. A host that adds one, the
  console provider included, prints a protobuf message it is given whole; a `SensitiveValue` masks itself
  everywhere.
- An exception message built from a message's text, and log scopes, are not scrubbed.
