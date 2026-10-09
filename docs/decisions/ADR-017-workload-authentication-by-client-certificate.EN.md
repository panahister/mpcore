# ADR-017 — Workload authentication by client certificate

- Status: Proposed; pending owner acceptance
- Date: 2026-10-08
- Extends: ADR-007 (reusable security and current actor), ADR-014 (the identity of a service), ADR-009 (the
  generated host's listeners)

## Context

Two internal services may have to talk over mutual TLS, and the called service may have to check which
workload is calling, by the name in its certificate. MP Core had no certificate code, and the template binds
its gRPC listener in cleartext. The principal of a call between services is already settled: it is the
service's own bearer token (ADR-014, section 3), also for a command that carries no user's token. What was
missing is the transport rule and the name check beside it.

## Decision

**The certificate is a transport rule and a name check, never the principal.** Kestrel requires and
validates it in process; the bearer token stays the caller.

### The called service: `AddMPCoreMutualTls` (`MPCore.Security.AspNetCore`)

| Rule | Why |
|---|---|
| Every TLS listener requires a client certificate; a cleartext listener is not affected | Kestrel's `ClientCertificateMode.RequireCertificate`, set as the HTTPS default, so listeners declared in `Kestrel:Endpoints` get it without code |
| The chain must end at one of the configured certificate authorities, never at the system store | `X509ChainTrustMode.CustomRootTrust`. A public authority would let any of its customers in |
| The certificate must be valid now and for client authentication | the extended key usage `1.3.6.1.5.5.7.3.2`; a server certificate presented as a client's is refused |
| Revocation is checked as configured, `NoCheck` by default | workload certificates of a private authority are usually short-lived, replaced rather than revoked, and publish no revocation list; `Online` or `Offline` when the authority publishes one |
| A subject alternative name must be listed: a URI, compared exactly, or a DNS name, compared without case | RFC 5280, section 4.2.1.6. A URI carries a SPIFFE ID (`spiffe://<trust domain>/<path>`). Wildcards are not expanded: a workload name names one workload |
| Every one of these is checked at the handshake; a failure ends the handshake | a refused workload never reaches HTTP |
| The certificate never becomes `CurrentActor`; the authenticated fallback policy of ADR-007 still requires a token | the certificate says which workload connects, the token says who calls |
| `RequireWorkloadCertificate()` on an endpoint requires the certificate as well, as a policy beside the token; `UseMPCoreMutualTls()` refuses such a request without a valid certificate with `401` (gRPC `Unauthenticated`) | for an endpoint that a proxy also reaches, where the handshake did not check the client |
| A proxy that terminates TLS may forward the client's certificate in `X-Client-Cert` (base64 DER, or URL-encoded PEM). It is read only when the request comes from `TrustedProxies`, as `KnownProxies` is for `X-Forwarded-*`, and the header is removed from every request. For a request from a trusted proxy the client certificate is the forwarded one or none: the certificate of the proxy's own connection is the proxy's, never the client's, and a header sent twice gives none, whatever it holds | `UseMPCoreCertificateForwarding()`, placed first, before gateway forwarding replaces the proxy's address. A forwarded certificate is held to the same rules. Without this, a proxy enrolled as a workload that forwards nothing would present its own listed certificate for every client behind it |
| No log record carries a certificate or key material; a refusal logs one fixed reason | |
| Startup fails without an authority or a workload name | |

### The calling service: `AddMPCoreClientCertificate` (`MPCore.Resilience.Http`)

On a client's builder, it presents a certificate, from two PEM files as a secret store mounts them or from
one the host loads, and trusts only the configured authorities for the called service's certificate, whose
name must match. Files are read again whenever the factory builds a new connection handler, so a renewed
certificate is used without a restart. It works on any client the factory builds, gRPC clients included. It
sits beside `AddMPCoreServiceIdentity`, which still gives the call its token: who calls is the token; which
workload connects is the certificate.

The two sides live in two packages on purpose: the called side needs ASP.NET Core and Kestrel; the calling
side needs neither, and joins the service identity in the package of outbound calls (ADR-014, section 4).

### The template

The generated host reads `Security:MutualTls` and calls `AddMPCoreMutualTls` only when `Enabled` is true;
it is false by default, so a generated host behaves as before. `UseMPCoreCertificateForwarding()` and
`UseMPCoreMutualTls()` are in its pipeline and do nothing while mutual TLS is off. A listener becomes TLS in
`Kestrel:Endpoints`, with its own certificate.

## Sources and why they apply

| Source | What it gives |
|---|---|
| Microsoft Learn, "Configure certificate authentication in ASP.NET Core" | Kestrel's `ClientCertificateMode` and `ClientCertificateValidation`, the in-process mechanism used here, and certificate forwarding behind a proxy |
| RFC 8446 (TLS 1.3), section 4.3.2, and RFC 5246 (TLS 1.2), section 7.4.4 | the server's request for a client certificate during the handshake |
| RFC 5280, sections 4.2.1.6 and 4.2.1.12 | subject alternative names, and the extended key usage for client authentication |
| SPIFFE, "The X.509 SPIFFE Verifiable Identity Document" | a workload's name, its SPIFFE ID, as the URI subject alternative name of its certificate |

## What was proved

`MutualTlsTests` in `MPCore.Security.Tests`, against real TLS listeners on the loopback interface, with
certificate authorities and certificates created by the test. 19 of the first 22 were seen failing against
stubs of the API; the three that passed are the admitted call, the call without a token, and the log check,
which a stub cannot fail.

| Test | What it shows |
|---|---|
| a listed workload certificate and the service's token | admitted; the actor is the service of the token |
| a listed certificate without a token | `401`: the fallback policy stays |
| no certificate, another authority, expired, a name not listed, a server certificate presented as a client's | the handshake fails, each for its own logged reason |
| the client extension | presents its certificate from PEM files and is admitted; configured with another server authority, it refuses the server |
| a client without its certificate or its server authority | fails when the client is built |
| a forwarded certificate | admitted from a listed proxy; refused with `401` from another address, of another authority, expired, with a name not listed, unreadable, absent, or sent twice with a listed certificate in each. The header never reaches an endpoint |
| revocation (`MutualTlsRevocationTests`, 2) | a locally generated authority issues two client certificates for one listed name and publishes a revocation list that names one; the revocation mode is bound from configuration. With `Online`, on Linux and Windows the revoked certificate is refused at the handshake (`Chain:Revoked`), the other is admitted, and the host asked for the list. On macOS .NET does not download the list of a private authority: the status of every certificate is unknown and the host refuses them all (`Chain:RevocationStatusUnknown`), so it fails closed. With revocation off, the default, the revoked certificate is admitted. Seen failing, on macOS and on Linux (in a container): a validator that ignores the mode admits the revoked certificate |
| a forwarded certificate, once the request ends (`A_certificate_a_trusted_proxy_forwards_is_disposed_at_the_end_of_the_request_and_a_connections_own_is_not`) | the certificate created from the header for a request is disposed with the response; the certificate of a connection, which the server owns, is not. Seen failing: it was never disposed before, and a middleware that disposed the connection's own failed the second half |
| the proxy's own connection certificate (`A_forwarded_certificate_is_read_only_from_a_listed_proxy_and_held_to_the_same_rules`, 15 cases) | from a listed proxy that forwards none, refused with `401` even when its own certificate is a listed workload; replaced by the forwarded one when there is one; from an address that is not a listed proxy, kept as it is. The case of a listed proxy that forwards none, with its own listed certificate, was seen failing (`OK` for `Unauthorized`) before the correction. The case of a listed proxy that sends the header twice, a listed certificate in each, was seen failing (`OK` for `Unauthorized`) when the line that refuses a repeated header was made to read its first value |
| a host without an authority or a workload name, or with an authority file that does not exist | startup fails |
| the options | bind from the configuration a generated host ships |
| the log | no certificate and no private key |

`TemplateContractTests` holds the template's wiring; it was seen failing against the template before the
change.

## Alternatives that were not taken

| Alternative | Why not |
|---|---|
| ASP.NET Core's certificate authentication handler (`Microsoft.AspNetCore.Authentication.Certificate`) as a scheme | it produces a principal from the certificate, which is what this decision forbids: a policy that names its scheme merges that principal into the request's user. It is also another package. Kestrel's validation and a policy requirement give the transport rule without a second principal |
| Trusting a service mesh's sidecar for all of it | it depends on the sidecar, and the host cannot check what it was told. A mesh remains possible: its proxy is a trusted proxy that forwards the certificate |
| The system's trusted root store | any public authority would issue a certificate this host accepts |
| A name check on the subject's common name | a subject alternative name is where RFC 5280 puts a name, and RFC 9525, which replaces RFC 6125, no longer lets a common name identify a service |

## Consequences

- A product can require mutual TLS between its services with configuration and certificates, and no code of
  its own.
- When mutual TLS is on, every TLS listener of the host requires a client certificate. A listener for callers
  without one, such as a gateway that presents none, stays cleartext behind the platform's network, or the
  gateway presents a workload certificate.
- `AddMPCoreMutualTls` sets Kestrel's HTTPS defaults; a host that sets its own replaces one or the other.
- Nothing about who the caller is changes: tokens, policies, the current actor and the audit trail are as
  before.
