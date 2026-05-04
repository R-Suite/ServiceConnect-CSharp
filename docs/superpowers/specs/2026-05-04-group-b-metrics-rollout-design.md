# Group B — Metrics rollout — design

**Status:** approved, awaiting implementation plan
**Source review:** [architecture-review-deep.md](../../../architecture-review-deep.md)
**Roadmap:** [architecture-fix-plan.md](../../../architecture-fix-plan.md) — Group B
**Date:** 2026-05-04

---

## Overview

Group B closes the largest production gap surfaced by the deep review: **no metrics**. The project has good tracing and structured logs, but operators have no out-of-the-box visibility into "how many messages did we process per second", "what's the publish-confirm timeout rate", "is the in-flight handler queue stalling". Six metrics + four connection-lifecycle Info logs + two enriched ack/nack failure logs land in this group.

The strategic call — OTel semantic conventions vs ServiceConnect-namespaced — is decided up front: the project's tracing already commits strictly to OTel semantic conventions (`messaging.message.id`, `messaging.system`, `messaging.destination.name` constants in `MessagingAttributes`). Metrics follow the same alignment: OTel-standard names where OTel defines them; `messaging.serviceconnect.*` extensions where it doesn't. Result: OTel-aware Grafana dashboards pick up the standard four out of the box; ServiceConnect-specific metrics live adjacent in the same `messaging.*` namespace.

### Cross-cutting decisions banked from brainstorm

- **Naming approach:** OTel-extension. OTel-standard names for OTel-defined metrics; `messaging.serviceconnect.*` for ServiceConnect-specific events.
- **Scope:** six metrics + two logging items in one group. Logging items (#9 connection lifecycle, #10 ack/nack `MessageId` enrichment) bundled because they share the operability theme and ship the same release narrative.
- **Always-on metrics, not opt-in via middleware.** `Meter` is module-static like `ActivitySource`; instruments are zero-cost when no listener subscribes. BCL pattern (`HttpClient`, `EFCore`, `Sockets`). Tracing remains opt-in via `AddTelemetry()` because Activity creation has real allocation cost; metric emission does not.
- **Meter lives in `ServiceConnect` core**, not `ServiceConnect.Telemetry`. Reference graph is `Telemetry → ServiceConnect`, so a Meter in Telemetry can't be called from the bus or RabbitMQ client. Telemetry package gets a thin `AddServiceConnectInstrumentation()` extension method for OTel users.

---

## In scope

| # | Item | Type |
|---|---|---|
| 1 | `messaging.publish.duration` (histogram, seconds) | OTel-standard |
| 2 | `messaging.process.duration` (histogram, seconds — handler dispatch) | OTel-standard |
| 3 | `messaging.client.published.messages` (counter) | OTel-standard |
| 4 | `messaging.client.consumed.messages` (counter, with `messaging.outcome` tag) | OTel-standard |
| 5 | `messaging.serviceconnect.retry.attempts` (counter) | ServiceConnect extension |
| 6 | `messaging.serviceconnect.retry.drops` (counter — retry-publish failure) | ServiceConnect extension |
| 7 | `messaging.serviceconnect.publish.confirm_timeouts` (counter) | ServiceConnect extension |
| 8 | `messaging.serviceconnect.audit.drops` (counter) | ServiceConnect extension |
| 9 | `messaging.serviceconnect.process.messages.inflight` (UpDownCounter — surfaces `_messagesBeingProcessed`) | ServiceConnect extension |
| 10 | `ConnectionOpened` / `ProducerConnectionOpened` / `ConnectionRecovered` / `ConnectionLost` Info logs | Logging |
| 11 | Ack/nack failure logs enriched with `MessageId` (`AckFailed` / `NackFailed`) | Logging |

## Out of scope (deliberately)

- **Retry-queue depth gauge.** Requires broker-side queue introspection; RabbitMQ.Client API doesn't expose this cheaply enough for a hot-path metric.
- **Per-handler-type metrics.** `messaging.destination.name` carries queue-level granularity; per-handler-type would explode cardinality.
- **Grafana dashboards / alerting templates.** Operators build those for their stack.
- **OpenTelemetry collector configuration examples** beyond the one-liner `AddMeter("ServiceConnect.Bus")`. Out of scope for this group.
- **Backwards-compat shims.** Metrics are a net-new feature; nothing to deprecate.
- **A separate `ServiceConnect.Metrics` package.** Meter lives in `ServiceConnect` core (architectural decision above).
- **Renaming `AddTelemetry()`** to e.g. `AddTracing()` for clarity now that metrics are a separate concern. Breaking name change without proportional benefit; documentation explains the distinction.
- **Channel open/close lifecycle logs.** Channels open/close per publish or consumer host setup — high-volume noise.
- **Reconnect-attempt counter** as a metric. Already covered by `ConnectionLost` log; if operators want a count, they scrape the log.

---

## Architecture

### Meter ownership

A new module-static class `ServiceConnectMeter` in `src/ServiceConnect/Diagnostics/ServiceConnectMeter.cs` (new directory). Holds:

- A `Meter` instance with name `"ServiceConnect.Bus"` (mirrors the existing `ActivitySource.Name`).
- Nine instruments (4 OTel-standard counters/histograms + 5 ServiceConnect extensions including the UpDownCounter).
- Static recorder methods that wrap the instrument-record-with-tags shape. Zero-cost when no listener.

Reference graph: `Telemetry → ServiceConnect → Interfaces`. The Meter can't live in `ServiceConnect.Telemetry` because both `ServiceConnect` core (where consume metrics emit) and `ServiceConnect.Client.RabbitMQ` (where publish/confirm-timeout/audit metrics emit) are upstream of it. Putting the Meter in `ServiceConnect` core puts it where the operations happen.

### Always-on emit

Every emit site calls `ServiceConnectMeter.<Recorder>(...)` unconditionally. When no listener has subscribed (`MeterProvider.AddMeter("ServiceConnect.Bus")`), the BCL aggregator is a no-op. This matches modern .NET BCL libraries (`HttpClient`, `EFCore`, `Sockets`).

`AddTelemetry()` is unchanged — it still adds tracing middleware only. Metrics are always-on regardless of whether `AddTelemetry()` is called. Documented in the observability page.

### Telemetry-package integration

`ServiceConnect.Telemetry` gains a single new extension method:

```csharp
namespace ServiceConnect.Telemetry;

public static class TelemetryMeterExtensions
{
    /// <summary>
    /// Subscribes the OpenTelemetry MeterProvider to ServiceConnect's Meter.
    /// Equivalent to <c>builder.AddMeter("ServiceConnect.Bus")</c>.
    /// </summary>
    public static MeterProviderBuilder AddServiceConnectInstrumentation(this MeterProviderBuilder builder)
        => builder.AddMeter(ServiceConnectMeter.MeterName);
}
```

Note: this method takes a dependency on `OpenTelemetry.Api`'s `MeterProviderBuilder`. The `ServiceConnect.Telemetry` package is OTel-flavoured already (the existing `AddTelemetry` builder helper, `MessagingAttributes` constants from OTel semantic conventions). No additional package dependency needed if `OpenTelemetry.Api` is already referenced — verify at implementation. If not, this single helper either:
1. Adds the OTel.Api package reference (small, runtime-stable), or
2. Falls back to documentation-only (users call `AddMeter("ServiceConnect.Bus")` directly).

The plan instructs the implementer to verify at Step-1 of Task 1.

### Constants module

A new `MetricNames` static class in `src/ServiceConnect/Diagnostics/MetricNames.cs` exposing the metric names as `public const string` for consumers (Grafana templates, custom MeterListeners, alert rules). Same precedent as `MessagingAttributes` (which lives in `ServiceConnect.Telemetry`).

Lives in `ServiceConnect` core rather than `ServiceConnect.Telemetry` because `ServiceConnectMeter` (also in core) consumes them; the reference graph is `Telemetry → ServiceConnect`, so Telemetry can re-export but core can't reach into Telemetry. Downstream consumers reference `ServiceConnect.Diagnostics.MetricNames` directly; the type is intentionally `public` for that reason.

```csharp
public static class MetricNames
{
    public const string PublishDuration = "messaging.publish.duration";
    public const string ProcessDuration = "messaging.process.duration";
    public const string PublishedMessages = "messaging.client.published.messages";
    public const string ConsumedMessages = "messaging.client.consumed.messages";
    public const string RetryAttempts = "messaging.serviceconnect.retry.attempts";
    public const string RetryDrops = "messaging.serviceconnect.retry.drops";
    public const string PublishConfirmTimeouts = "messaging.serviceconnect.publish.confirm_timeouts";
    public const string AuditDrops = "messaging.serviceconnect.audit.drops";
    public const string InFlightMessages = "messaging.serviceconnect.process.messages.inflight";
}
```

### Emit site map

| Instrument | Emit site |
|---|---|
| `messaging.publish.duration` | `Producer.PublishAsync` / `SendAsync` / `SendBytesAsync` — Stopwatch around the call to `PublishWithTimeoutAsync` |
| `messaging.process.duration` | `RabbitMqConsumerHost.OnMessageReceivedAsync` — Stopwatch around `await _processor.ProcessAsync(...)` |
| `messaging.client.published.messages` | Same call site as publish duration; increments on success |
| `messaging.client.consumed.messages` | `RabbitMqConsumerHost.OnMessageReceivedAsync` — increments after handler completes; tag `messaging.outcome=success\|error\|retry\|drop` |
| `messaging.serviceconnect.retry.attempts` | `MessageRetryHandler.cs` — at the retry-counter increment point |
| `messaging.serviceconnect.retry.drops` | `InboundMessageProcessor.cs:150-158` — retry-publish-failure catch |
| `messaging.serviceconnect.publish.confirm_timeouts` | `Producer.cs:469-472` — TimeoutException catch in `PublishWithTimeoutAsync` |
| `messaging.serviceconnect.audit.drops` | `InboundMessageProcessor.cs:243-246` — audit catch |
| `messaging.serviceconnect.process.messages.inflight` | `RabbitMqConsumerHost` — increments at handler dispatch entry, decrements in `finally` after dispatch |

---

## Tag schema

OTel-aligned, bounded cardinality.

### Required base tag (every metric)

| Tag | Source | Example |
|---|---|---|
| `messaging.system` | `IMessagingSystemAttributes.MessagingSystem` (already in the project) | `"rabbitmq"` |

### Per-metric required tags

| Metric | Additional required tags |
|---|---|
| `messaging.publish.duration` | `messaging.destination.name`, `messaging.operation=publish` |
| `messaging.process.duration` | `messaging.destination.name=<queue>`, `messaging.operation=process` |
| `messaging.client.published.messages` | `messaging.destination.name`, `messaging.operation=publish` |
| `messaging.client.consumed.messages` | `messaging.destination.name=<queue>`, `messaging.operation=process`, `messaging.outcome` |
| `messaging.serviceconnect.retry.attempts` | `messaging.destination.name=<queue>` |
| `messaging.serviceconnect.retry.drops` | `messaging.destination.name=<queue>` |
| `messaging.serviceconnect.publish.confirm_timeouts` | `messaging.destination.name=<exchange>` |
| `messaging.serviceconnect.audit.drops` | (none — audit queue is global) |
| `messaging.serviceconnect.process.messages.inflight` | `messaging.destination.name=<queue>` |

### Conditional tag — `error.type`

Added only on emit sites reporting an error path. For the OTel counters this is the OTel-standard attribute. Bounded by:

- Mapped to a stable short string for common .NET exception types (`OperationCanceledException → "cancelled"`, `TimeoutException → "timeout"`).
- Fallback: `exception.GetType().Name` (the type's short name, not `FullName`).
- **Never** the exception message — unbounded cardinality.

Implementation: a small static `ExceptionTypeMapper` helper in `ServiceConnect/Diagnostics/`.

### Bounded `messaging.outcome` values

For `messaging.client.consumed.messages`:

| Value | Meaning |
|---|---|
| `success` | Handler returned without throwing; ack dispatched. |
| `error` | Handler threw; routed to error queue (terminal failure). |
| `retry` | Handler threw; routed to retry queue (will redeliver). |
| `drop` | Handler threw or framework rejected; message neither requeued nor sent to error (e.g. retry-publish drop). |

Closed set, four values; safe as a tag dimension.

### Cardinality budget

Typical deployments — 5-20 queues, 5-20 exchanges, ~5 outcome/error categories — produce ~400 active series per metric upper bound. Operators with very high queue counts (1000+) should be aware; documented in the observability page.

### Tags NOT included (deliberately)

- `messaging.message.id` — per-message; explosive cardinality. Already on traces.
- `messaging.message.conversation_id` — same reason.
- `messaging.rabbitmq.destination.routing_key` — high cardinality (often per-message in pub-sub).
- Hostname / instance ID — OTel resource attributes, not metric tags. Included automatically by the resource provider, not by us.

---

## Logging items

### Item 9 — Connection-lifecycle Info logs

Four new source-generated entries on the existing `RabbitMqClientLog` partial class:

| EventId | Event name | When |
|---|---|---|
| 2 | `ConnectionOpened` | `Connection.cs` after `CreateConnectionAsync` succeeds |
| 3 | `ProducerConnectionOpened` | `ProducerConnection.cs` after `CreateConnectionAsync` succeeds |
| 4 | `ConnectionRecovered` | RabbitMQ.Client `RecoverySucceeded` event handler |
| 5 | `ConnectionLost` | RabbitMQ.Client `ConnectionShutdown` event handler |

Level: `Information`. The "lost" event stays Info (broker-initiated shutdowns happen for normal reasons — rolling restarts, cluster maintenance — and operators don't want pages on every one). Connection failures that fail to recover already surface as `Error`-level logs from RabbitMQ.Client itself.

Each log carries: hostname, port, virtual host, connection name (if set). The "lost" event also carries `ShutdownEventArgs.ReplyText` and `ShutdownInitiator`.

Hooks: subscribe to `connection.RecoverySucceeded` and `connection.ConnectionShutdown` in both `Connection.cs` and `ProducerConnection.cs`. Unsubscribe on disposal. Implementation must add the unsubscribe to the existing `DisposeAsync` paths.

### Item 10 — Ack/nack failure log enrichment

Two new source-generated entries on `RabbitMqClientLog`:

| EventId | Event name |
|---|---|
| 6 | `AckFailed` |
| 7 | `NackFailed` |

Level: `Warning` (matches today's level — enriching, not changing severity).

Replaces the existing `_logger.LogWarning(ex, "Failed to ack message ...")`-style calls in `RabbitMqConsumerHost.cs` (around lines 395-408 — exact lines verified at implementation). Each carries `MessageId` (sourced from `BasicDeliverEventArgs.BasicProperties.MessageId` if present, else `DeliveryTag.ToString()` so there's always something useful) plus the existing fields (queue name, exception).

---

## Testing strategy

| Item | Verification |
|---|---|
| Each metric (1-9) | One `MeterListener`-based unit test per emit site. Subscribes to `ServiceConnectMeter.MeterName`, exercises the operation (mocked `IChannel`/`IConnection` for the producer side; a fake handler for the consumer side), asserts: instrument matches, value matches (counters increment by N, histograms see one record with non-negative duration, UpDownCounter pre/post deltas balance), tags match the expected set. ~9 new unit tests in `src/ServiceConnect.UnitTests/Diagnostics/` (new directory) and `src/ServiceConnect.UnitTests/RabbitMQ/`. |
| `messaging.client.consumed.messages` outcome | Four parameterised cases: success / error / retry / drop. Each exercises the consumer host through the corresponding code path. |
| `error.type` mapping | One test for the small allow-list (`OperationCanceledException → "cancelled"`, `TimeoutException → "timeout"`, fallback `MyCustomException → "MyCustomException"`). |
| `MetricNames` constants | Compile-time only. |
| Logging items 10-11 (events 2-7) | `FakeLogger<>` tests as in Groups C/D. One test per new event ID. 6 new tests. |
| `AddServiceConnectInstrumentation` extension | One test confirms a call adds the meter name to a `MeterProviderBuilder`. |

Total: ~17 new unit tests. No new test infrastructure beyond what Groups C/D already added.

---

## Rollout

Six atomic commits on `v7-clean-architecture` (mirrors Group D structure):

1. **`feat(diagnostics): introduce ServiceConnectMeter`** — the static class, the nine instruments, the `MetricNames` constants, the `AddServiceConnectInstrumentation` helper. No call sites yet. Tests for the helper and the constants. Lands the foundation.
2. **`feat(transport): emit publish/consume duration + count metrics`** — wires the four OTel-standard instruments into Producer + RabbitMqConsumerHost. Tests for each.
3. **`feat(transport): emit ServiceConnect-specific operability metrics`** — retry/drop/timeout/audit/inflight. Tests for each.
4. **`feat(transport): emit connection-lifecycle Info logs and enrich ack/nack failure logs`** — items 9-10. Bundles cleanly.
5. **`docs(website): document ServiceConnect metrics + lifecycle logs`** — extends `learn/operations/observability.mdx` with a metrics catalogue (one row per metric — name, type, unit, tags), and adds the connection-lifecycle log table. New v8 highlight: "feat(telemetry): metrics + connection-lifecycle logs" (additive — not breaking).
6. **`docs(architecture): mark Group B done in the fix plan`**.

Each commit independently passes build + tests. Reverting any one commit cleanly removes that increment without affecting the others (modulo: reverting commit 1 forces 2-3 to revert with it, since they consume the foundation).

---

## Risks

- **Cardinality blow-up.** Mitigated by: closed-set `messaging.outcome` (4 values), `error.type` allow-list mapping unknown exceptions to type's short name (bounded by user's exception hierarchy depth, typically <100), no per-message tags (`message.id`, `routing_key`, `conversation_id` deliberately excluded). Documented in the observability page with the back-of-envelope upper bound.
- **Hot-path overhead.** `MeterListener` zero-cost when no listener subscribed is a BCL guarantee. Verifiable: a benchmark on the publish path with no listener attached vs the same path on `master` should show <1% delta. Not gating; flag if benchmark numbers regress.
- **Always-on default vs project's opt-in tracing.** Justified architecturally (zero-cost when not subscribed) but is a deviation from the existing `AddTelemetry()` opt-in story. Documented explicitly in the observability page: "Tracing is opt-in via `AddTelemetry()` because span allocation has a per-message cost. Metrics are always-on because instrument emission is free without a listener."
- **`ConnectionShutdown` event handler lifetime.** If we subscribe in `Connection.cs` / `ProducerConnection.cs` and forget to unsubscribe at disposal, we leak the handler until GC. Mitigated by explicit unsubscribe in the existing `DisposeAsync` paths.
- **`error.type=cancelled` spike on graceful shutdown.** A graceful bus stop produces `OperationCanceledException` on in-flight handlers. The `messaging.client.consumed.messages{outcome=error,error.type=cancelled}` series will spike at every restart — operators may misread as a problem. Documented in the observability page. **Implementation note:** if cancellation is cleanly distinguishable from other errors in the consumer-host code paths, prefer a separate `outcome=cancelled` value over `outcome=error` — verify at implementation Step 3 of Task 3.
- **`OpenTelemetry.Api` package reference.** The `AddServiceConnectInstrumentation` extension takes a `MeterProviderBuilder` argument, requiring `OpenTelemetry.Api`. Verify at implementation Step 1 of Task 1 whether this package is already a transitive dependency; if not, add it (small, runtime-stable). Fallback: drop the helper and document `AddMeter("ServiceConnect.Bus")` directly.
- **Rollback** — each of the six commits is independently revertible. No data migrations, no API breaks.

---

## Decisions banked from the brainstorm

- **OTel-extension naming (option A).** OTel-standard names where OTel defines them; `messaging.serviceconnect.*` for ServiceConnect-specific events.
- **Six metrics + two logging items in one group (option a).** Both items in the same operability theme.
- **Always-on metrics, not opt-in via middleware.** Modern BCL pattern; tracing remains opt-in for its real allocation cost.
- **Meter in `ServiceConnect` core**, not `ServiceConnect.Telemetry`. Reference graph forced this.
- **`messaging.outcome` is a closed set of four values** — cardinality-safe.
- **`error.type` uses an allow-list mapper** with type-short-name fallback. No exception-message tagging.
- **Connection-lifecycle logs at Information level**, not Warning, even for "lost" — broker-initiated shutdowns happen for normal reasons.
- **No `AddMetrics()` rename** of `AddTelemetry()`. Documentation explains the distinction.
