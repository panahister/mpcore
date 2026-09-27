---
name: mpcore-apply-business-audit
description: Record entity changes and business actions in the audit trail, within the audit choice already recorded for this project. For MPCORE_ORGANIZATION.MPCORE_COMPONENT backends generated from MP Core MPCORE_VERSION.
---

# Apply business audit

Use when a capability changes something a business must be able to account for later: who did
what, to which record, with which outcome.

## First, read the manifest

`.mpcore/template-manifest.json` records `businessAudit`. If it is `none`, say so and stop: do not
build an ad-hoc log table, and do not treat operational logging as an audit trail. Adding the
capability is a documented manual change (docs/getting-started.md), decided by the owner.

## Audit invariants

- Default deny. An entity is audited only when declared in
  `src/*.Infrastructure/Audit/AuditPolicyConfiguration.cs`, and only its included properties are
  captured. Ask the owner which properties matter; do not include everything.
- Credential-like properties (password, secret, token, API key, PIN, OTP, CVV) can never be
  included; the policy builder refuses them. Banking and identity identifiers (IBAN, card, national
  id, phone, email) are always masked — prefer `KeepLastFour` for account-style identifiers.
- Entity changes are captured by the persistence layer in the same transaction as the change.
  Do not write audit rows by hand for created, updated or deleted entities.
- Business actions are recorded from the handler through `IBusinessAuditRecorder`:
  `RecordAsync` for a success (commits with the unit of work), `RecordAttemptAsync` for a rejected
  or failed attempt (written detached, so it survives the rollback). Pass the failure domain and
  code from the failure descriptor; keep `reason` short and free of sensitive data.
- The actor comes from the validated token; never accept it from the request.
- Nothing updates or deletes an audit row from application code. Retention is a database decision.

## Steps

1. Name the entity and the business actions involved; get the property list approved.
2. Declare the entity policy; use `Mask` for anything identifying.
3. Record the actions and their attempts in the handler at the point where the outcome is known.
4. Read back through `IAuditQuery` in a test: assert what is recorded and, just as important, what
   is not (excluded properties, masked values, no success row after a rollback).
5. If the owner wants the trail visible, design the read authorization explicitly; none is
   generated.
