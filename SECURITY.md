# Security policy

## Supported versions

Security fixes are made for the latest published minor version.

| Version | Supported |
|---|---|
| 0.9.x | yes |
| earlier prereleases | no |

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting for this repository
(the *Security* tab, *Report a vulnerability*). Say what is affected, how to reproduce it, and what an
attacker gains.

You will receive an acknowledgement, and then either a fix with a release and an advisory that credits
you, if you wish, or an explanation of why the report is not a vulnerability.

## What MP Core takes responsibility for

Validation of bearer tokens, protection of endpoints by default, the identity of the actor in audit
records, redaction of sensitive fields in logs and traces, and the transport of failures without leaking
internals. What a product builds on top, including its own secrets and its identity provider, is the
product's.
