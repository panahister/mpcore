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
| System.Text.Json writes `***`; it reads a string into the value | a response or a serialized command never carries it; a request can |
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

`services.AddGrpc().AddMPCoreSensitiveMessages("package.Service")` adds an opt-in interceptor. Before each
call of a method of a named service, it adds the method's request and response types to
`SensitiveMessageTypes` (`MPCore.Application`). The log and trace processors mask whole any attribute or tag
that holds an object of such a type, and scrub its text from the formatted message. The types are added
before the handler runs, so a message a handler logs, request or response, is masked. A host can add any
other type itself, for example the messages of a client.

`MPCore.Observability` references `MPCore.Application` for this, the registry both packages share. A
transport package may not reference a security package (ADR-007), so the registry is not in
`MPCore.Security.Abstractions`.

## What was proved

| Test | What it shows |
|---|---|
| `SensitiveValueTests`, 6 | every rendering shows the mask; JSON writes it and reads a value; the debugger shows the mask; the value leaves only through `Reveal()`; equality by value in constant time; null is refused |
| `SensitiveDataTests.A_sensitive_value_reaches_no_sink` | a known value, logged as a structured attribute, inside a record, in an interpolated message and in an exception's message, and set as a trace tag, appears in none of: the OpenTelemetry log and trace export, the JSON console provider, a plain provider |
| `SensitiveDataTests.The_name_processors_also_mask_nested_attributes` | `http.request.header.authorization` and a password inside a nested collection are masked in logs and traces; a neighbouring order id is kept |
| `SensitiveDataTests.The_name_list_is_unchanged` | the 22 names |
| `SensitiveDataTests.An_object_of_a_sensitive_message_type_is_masked_whole` | an object of an added type is masked, and its text is scrubbed from the message |
| `SensitiveMessageTests` | a gRPC handler of a named service logs its request and its response: both are masked, and neither the code (a `debug_redact` field), the phone nor the session appears in the message; the handler of a service that is not named is logged as it is |

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
  traces of `AddMPCoreObservability`. Another logging provider, the console provider ASP.NET Core adds
  included, prints a protobuf message it is given whole; a `SensitiveValue` masks itself everywhere.
- An exception message built from a message's text, and log scopes, are not scrubbed.
