# Rejected findings — 2026-05-17 architecture review

Findings from the 2026-05-17 audit pass that first-hand validation confirmed
were already implemented at the time of review. Recorded here so the same
ground is not re-covered in future review cycles.

---

## InboundMessageProcessor audit-publish silent drop (review §Findings)

**Claim:** Audit-publish failure swallowed without metric observability;
log should be promoted to LogError.

**Verdict:** Already implemented. `ServiceConnectMeter.AddRetryDrop` at
InboundMessageProcessor.cs:357 emits a counter on retry/fallback drop with
tags `messaging.system`, `messaging.destination.name`, `error.type`. Audit
drops are metered inside `MessageAuditPublisher` via
`messaging.serviceconnect.audit.drops`. Error-level logging is already in
place (lines 318, 354). The audit pass misread the file.

**Detail:**

- `InboundMessageProcessor.cs:357` — `ServiceConnectMeter.AddRetryDrop` with
  tags `messaging.system=rabbitmq`, `messaging.destination.name=<queue>`,
  `error.type=<mapped>`. Reached only after both the retry path and the
  error-exchange fallback have failed.
- `MessageAuditPublisher.cs:87,103` — `ServiceConnectMeter.AddAuditDrop` with
  tags `messaging.system=rabbitmq` and `error.type` (either `"unroutable"` for
  `PublishException` or the mapped exception type for transport/IO failures).
  Backed by `MetricNames.AuditDrops = "messaging.serviceconnect.audit.drops"`.
- `ServiceConnectMeter.cs:94,100` — `AddRetryDrop` and `AddAuditDrop` are
  distinct named helpers backed by separate `Counter<long>` instruments.
- Log levels: `LogError` at InboundMessageProcessor.cs:318 (retry-publish
  failure) and :354 (error-exchange fallback failure); `LogWarning` at
  MessageAuditPublisher.cs:84,95 — audit failures are correctly Warning because
  audit is a best-effort observability side-effect, not part of the business
  transaction. Promoting audit failures to `LogError` would conflate business
  failures with observability side-channel failures.
