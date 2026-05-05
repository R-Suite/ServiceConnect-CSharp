# Group B follow-ups — design

**Status:** approved, awaiting implementation plan
**Source:** `notes.md` — "Follow-ups surfaced during Group B (metrics rollout)"
**Date:** 2026-05-05

---

## Overview

Three independent follow-ups surfaced during the Group B metrics rollout reviews. Bundled into one spec because they share the same trigger (Group B Tasks 2 and 3) and would otherwise sit in `notes.md` indefinitely; landed as three sequenced commits in one PR so v8 GA isn't waiting on three separate review cycles.

- **Item 1 (Task 2 review).** `Producer.SendAsync(Type)` and `Bus.SendToManyAsync` both fail-fast on the per-endpoint fan-out loop. The `IBus.SendToManyAsync` XML doc promises *"failures on one endpoint do not abort the others"* — broken at both layers. `Bus.SendAsync<T>` of a multi-mapped type silently drops later endpoints when an early one fails. Switch both layers to continue-on-failure with `AggregateException` collection.

- **Item 2 (Task 3 deviation).** `MessageRetryHandler` tags the retry-attempt counter with `messaging.destination.name = retryQueueName` (the `.Retries` queue), not the consumer queue. The OTel-standard tag should carry the consumer queue (the operator's filter key); the retry-queue destination moves to a ServiceConnect-namespaced tag.

- **Item 3 (Task 3 implementer note).** `RabbitMqConsumerHost.cs` is 949 lines doing admission, header validation, dispatch, ack/nack, in-flight bookkeeping, and shutdown coordination. Extract three collaborators (`RabbitMqAdmissionGate`, `RabbitMqHeaderValidator`, `RabbitMqDispatchPipeline`) and migrate the corresponding tests onto them. No behavioural change.

### Cross-cutting decisions banked from brainstorm

- **One bundled PR, three commits, ordered Item 2 → Item 1 → Item 3.** Item 2 first (smallest blast radius, internal-only, no contract change). Item 1 next (touches public exception contract). Item 3 last (largest churn; Item 3's new tests assert against the corrected tag scheme from Item 2).
- **`AggregateException`, not a custom exception type.** Item 1's continue-on-failure surfaces failures via the framework type `AggregateException`. No new public exception class.
- **Cancellation is not aggregated.** Item 1's loop catches `Exception` but rethrows `OperationCanceledException` directly so cancellation propagates with its native type.
- **Ctor parameter, not method parameter.** Item 2 threads the consumer queue name into `MessageRetryHandler` via the constructor (per-instance identity). Method-parameter alternative was rejected — consumer queue is stable for the lifetime of the handler instance.
- **Test ownership migrates with the logic.** Item 3 moves logic-level tests onto the new collaborators (`RabbitMqAdmissionGateTests`, `RabbitMqHeaderValidatorTests`, `RabbitMqDispatchPipelineTests`); `RabbitMqConsumerHostTests` shrinks to wiring/integration scenarios. Hybrid "extract now, migrate tests later" was rejected — defers half the maintainability gain.
- **No new metrics.** Item 2 is a tag-set change on the existing retry-attempt counter. No new instrument is registered.

---

## Item 1 — Producer / Bus fan-out continue-on-failure

### Why

Two contract bugs. First, `IBus.SendToManyAsync<T>` documents in [IBus.cs:42-47](../../../src/ServiceConnect.Interfaces/Bus/IBus.cs#L42-L47) that *"failures on one endpoint do not abort the others"*, but `Bus.SendToManyAsync` at [Bus.cs:203-222](../../../src/ServiceConnect/Bus.cs#L203-L222) has no try/catch around the per-endpoint pipeline call — the first failure aborts the loop. Second, `Producer.SendAsync(Type)` at [Producer.cs:225-294](../../../src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs#L225-L294) lets the first endpoint's exception escape its `foreach`, and this method is reached not only from `Bus.SendToManyAsync` but also from `Bus.SendAsync<T>` whenever the message type maps to multiple queues (via [SendMessagePipeline.cs:69-72](../../../src/ServiceConnect/Services/SendMessagePipeline.cs#L69-L72)). Result: an ordinary `SendAsync<T>` of a multi-mapped type silently drops deliveries to later-listed endpoints when an early one fails.

### Behavioural change

`Producer.SendAsync(Type, body, headers, ct)`:

- Wrap each iteration of the per-endpoint `foreach` in `try`/`catch (Exception ex) when ex is not OperationCanceledException`. Collect non-cancellation exceptions in a `List<Exception>` declared above the loop.
- After the loop: if the list is non-empty, throw `new AggregateException(list)`.
- `OperationCanceledException` (from `cancellationToken`) propagates directly — never wrapped, never swallowed.
- Per-endpoint metric emit (publish.duration / client.published.messages) stays per-iteration as today.
- The publish lock (`_publishLock`) is held across the entire loop as today.

`Bus.SendToManyAsync<T>`:

- Same shape — `foreach` over the supplied endpoints with try/catch, cancellation passthrough, and `AggregateException` on partial/total failure.
- Per-iteration shallow header copy (already in place at [Bus.cs:209](../../../src/ServiceConnect/Bus.cs#L209)) stays.

Outcomes:

| Outcome | Today | After |
|---|---|---|
| All endpoints succeed | no exception | no exception |
| All endpoints fail | first exception thrown directly | `AggregateException` with N inner exceptions |
| Partial failure | first failed endpoint's exception thrown directly; later endpoints never attempted | all endpoints attempted; `AggregateException` with the failed subset |
| Cancellation mid-loop | `OperationCanceledException` thrown directly | `OperationCanceledException` thrown directly |

### Contract docs

Update XML doc on:

- `IBus.SendAsync<T>` — add a paragraph: when the message type maps to multiple queues (queue-mapping fan-out), per-endpoint failures surface as `AggregateException`; cancellation propagates as `OperationCanceledException` directly.
- `IBus.SendToManyAsync<T>` — extend existing "failures on one endpoint do not abort the others" sentence to specify `AggregateException` as the failure type.
- `IProducer.SendAsync(Type, body, headers, ct)` — same fan-out paragraph as `IBus.SendAsync<T>`.

### Tests

- New `ProducerSendAsyncFanoutTests` — matrix: all-succeed, single-endpoint-fails, multi-endpoint-fails, cancellation-mid-loop. Asserts `AggregateException.InnerExceptions` contains exactly the expected per-endpoint failures.
- `BusSendToManyAsyncFanoutTests` (or extension to existing) — same matrix, with at least one test that has middleware in the pipeline (covers the per-iteration header-copy invariant).
- Cancellation tests assert `OperationCanceledException` directly, not nested in `AggregateException`.

### Out of scope

- Parallel publish (`Task.WhenAll`). Today's loop is sequential under `_publishLock`; parallelising would defeat the lock's purpose. Sequential continue-on-failure is the change.
- New public exception type. `AggregateException.InnerExceptions` carries the same information.
- `Producer.SendAsync(string endPoint, ...)` (single-endpoint overload). Not a fan-out path; first failure is the only failure.

### Risk

Today, a multi-mapped `SendAsync<T>` throws the *first* endpoint's exception type directly (e.g. `BrokerUnreachableException`). After this change, callers see `AggregateException`. Any caller `catch`ing the specific exception type will now miss it because it's nested. Documented in XML doc and v8 release notes.

---

## Item 2 — Retry-attempt metric tag

### Why

The retry-attempt counter at [MessageRetryHandler.cs:71-75](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L71-L75) tags `messaging.destination.name` with the retry queue (`<X>.Retries`). The OTel-standard tag is the operator's filter key; for the query "how often is queue X retrying?", they want to filter on the consumer queue, not the retry queue. Today they have to filter on `<X>.Retries`, which is a ServiceConnect implementation detail.

### Tag scheme change

At [MessageRetryHandler.cs:71-75](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/MessageRetryHandler.cs#L71-L75):

```
messaging.system                       = "rabbitmq"
messaging.destination.name             = consumerQueueName    // was: retryQueueName
messaging.serviceconnect.retry.target  = retryQueueName       // new tag
```

Counter name (`AddRetryAttempt` registration in `ServiceConnectMeter`) is unchanged. No new counter is registered. The tag pair preserves both pieces of information; cardinality is unchanged because the retry queue is `<consumerQueue>.Retries`, so the new pair has identical distinct-value count to the old single tag.

### Plumbing

`MessageRetryHandler` constructor gains a `string consumerQueueName` parameter:

```csharp
internal sealed class MessageRetryHandler(
    int maxRetries,
    string errorExchange,
    string consumerQueueName,
    ILogger logger,
    TimeProvider? timeProvider = null)
```

Position: 3rd positional, before `ILogger logger`. Stored on a private field, used only to populate the new tag.

Construction at [Consumer.cs:173-174](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L173-L174) updated to pass `_queueConfiguration.QueueName`. Single prod call site.

Test construction sites (~70 across `src/ServiceConnect.UnitTests/`) updated mechanically — existing calls of the form `new MessageRetryHandler(3, "err", NullLogger.Instance)` become `new MessageRetryHandler(3, "err", "test.consumer.queue", NullLogger.Instance)` (queue name inserted in the new 3rd position, logger pushed to 4th). A scripted edit handles the bulk; the few sites that already have a queue-name fixture get that.

### Tests

- `MessageRetryHandlerMetricsTests` updated to assert the new tag set: `messaging.destination.name = consumerQueue`, `messaging.serviceconnect.retry.target = retryQueue`.
- New focused test: when a retry attempt fires, the consumer queue (not the `.Retries` queue) appears under `messaging.destination.name`.
- All other `MessageRetryHandler*Tests` updated for the new ctor signature only — no assertion changes beyond the metric ones above.

### Out of scope

- New counter. The change is a tag-set change on the existing `AddRetryAttempt` instrument.
- Renaming the counter. The instrument name stays.
- Dropping the retry-queue from the tag set. The retry queue is still useful for operators tracing a specific retried-message path; namespaced tag preserves it without contaminating the standard tag.

### Risk

Operators currently filtering retry-attempts by `messaging.destination.name = "<X>.Retries"` will get zero data after the change. Documented in v8 release notes alongside Item 1's contract change. The new scheme is operator-friendly; migration is a one-time fix to dashboards.

---

## Item 3 — RabbitMqConsumerHost split

### Why

[RabbitMqConsumerHost.cs](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs) is 949 lines. During Group B Task 3, both `EventAsync` and `ProcessAsync` tripped MA0051 (200-line method limit) once metric emit blocks were inlined; partial extractions (`BuildInFlightTags`, `TryRejectOversizedHeaderAsync`, `PublishAuditWithDropMetricAsync`) brought them back under the limit but didn't address the file-level concern. The host currently does six things — admission, header validation, dispatch, ack/nack, in-flight bookkeeping, shutdown coordination — and the boundaries between them are implicit.

### Collaborators — all `internal sealed`, in [src/ServiceConnect.Client.RabbitMQ/Consumer/](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/)

#### `RabbitMqAdmissionGate`

Owns in-flight bookkeeping, shutdown gating, and the in-flight metric tag set.

Surface:

```csharp
internal sealed class RabbitMqAdmissionGate
{
    public bool TryAdmit();                                   // false during shutdown
    public void Release();                                    // paired with TryAdmit
    public Task DrainAsync(CancellationToken ct);             // wait for in-flight → 0
    public TagList CurrentInFlightTags { get; }
    // emits in-flight gauge changes on admit/release internally
}
```

Encapsulates today's in-flight counter, shutdown signal/flag, and `BuildInFlightTags` from the host.

#### `RabbitMqHeaderValidator`

Pure header pre-dispatch validation; encapsulates today's `TryRejectOversizedHeaderAsync` and the audit-publish + drop-metric pair (`PublishAuditWithDropMetricAsync`) — these belong together because rejecting a delivery and recording why is one logical operation.

Surface:

```csharp
internal sealed class RabbitMqHeaderValidator
{
    public Task<HeaderValidationResult> ValidateAsync(
        BasicDeliverEventArgs args,
        IChannel channel,
        CancellationToken ct);
}

internal readonly record struct HeaderValidationResult(bool Accepted, string? RejectReason);
```

Takes the audit publisher and drop-metric emit as injected dependencies (constructed by `Consumer.cs`).

#### `RabbitMqDispatchPipeline`

The inner dispatch + ack/nack stage.

Surface:

```csharp
internal sealed class RabbitMqDispatchPipeline
{
    public Task DispatchAsync(
        BasicDeliverEventArgs args,
        IChannel channel,
        IInboundMessageProcessor processor,
        CancellationToken ct);
}
```

Wraps the existing inner try/catch around `processor.ProcessAsync`, the consume-duration metric, ack/nack, and the terminal-failure path that calls `_retryHandler.HandleTerminalFailureAsync`.

### `RabbitMqConsumerHost` after the split

- ~300 lines.
- Owns: AMQP consumer registration (`_consumer.ReceivedAsync += …`), connection wiring, `PrepareAsync` / `BeginConsumingAsync` / `ConsumeMessageTypeAsync` orchestration, lifecycle (`DisposeAsync`).
- `EventAsync` becomes a small orchestrator:

```csharp
private async Task EventAsync(object _, BasicDeliverEventArgs args, CancellationToken ct)
{
    if (!_admission.TryAdmit())
    {
        // shutdown — nack-requeue or drop per existing policy
        return;
    }
    try
    {
        var validation = await _validator.ValidateAsync(args, _channel, ct);
        if (!validation.Accepted) return;
        await _dispatch.DispatchAsync(args, _channel, _processor, ct);
    }
    finally
    {
        _admission.Release();
    }
}
```

- Construction in [Consumer.cs:176-183](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L176-L183) updated to instantiate the three collaborators and pass them in.

### Test reorganisation

New test classes:

- `RabbitMqAdmissionGateTests` — admit/release pairing, admission rejected during shutdown, drain semantics, in-flight gauge tag set.
- `RabbitMqHeaderValidatorTests` — oversized header rejection with audit publish + drop metric, accepted-path returns `Accepted` without side effects.
- `RabbitMqDispatchPipelineTests` — dispatch ack on success, nack/retry on handler exception, terminal failure path through `MessageRetryHandler.HandleTerminalFailureAsync`, consume-duration metric tag set.

Existing tests migrated:

- `RabbitMqConsumerHostInflightCounterTests` → `RabbitMqAdmissionGateTests` (renamed, preserved assertions).
- `RabbitMqConsumerHostHeaderSizeTests` → `RabbitMqHeaderValidatorTests`.
- `RabbitMqConsumerHostAckNackTests` → `RabbitMqDispatchPipelineTests`.
- `RabbitMqConsumerHostTests` (and related) — methods that exercise pure admission/validation/dispatch logic move out; methods that exercise wiring/integration (consumer-cancel, recovery, full ack-after-handler, prepare/begin lifecycle) stay.

Migration rule: every moved test keeps its existing assertions verbatim; only the `Arrange` (which collaborator is constructed) and `Act` (which method is called) change.

### Behaviour invariants — must hold after the refactor

- Same observable AMQP frame sequence on all paths (ack, nack, basic.reject, audit publish, retry-queue publish, error-exchange publish).
- Same metric tag sets on all four OTel-standard counters/histograms emitted from the consumer path (publish.duration, process.duration, client.published.messages, client.consumed.messages). Only the retry-attempt counter's tag set changes — and that's Item 2, not Item 3.
- Same shutdown semantics: in-flight drain before `DisposeAsync` returns; admission rejected once shutdown begins.
- No new exceptions surfaced from the host that the AMQP consumer event loop doesn't already see.

### Out of scope

- Renaming `RabbitMqConsumerHost` or moving it to a sub-folder. File stays at its current path.
- Changes to `IInboundMessageProcessor` / `MessageRetryHandler` / `MessageAuditPublisher` surfaces beyond what Item 2 already does.
- Public API changes. All collaborators are `internal sealed`.

### Risk

Behaviour-preserving by intent, but the test-migration surface is non-trivial. Mitigations:

- Migration rule above (assertions verbatim) limits drift.
- Pre/post diff of metric emit counts on the integration tests (`src/ServiceConnect.IntegrationTests` Testcontainers RabbitMQ) gives extra confidence beyond unit-test parity.

---

## Rollout

One PR, three commits, ordered:

1. **Commit 1 — Item 2.** `MessageRetryHandler` ctor parameter + tag rename. Smallest blast radius; lands cleanly without depending on the others.
2. **Commit 2 — Item 1.** Producer + Bus fan-out continue-on-failure with `AggregateException`. Independent of Items 2 and 3.
3. **Commit 3 — Item 3.** Consumer host split + test migration. Lands on top of Item 2's polished tag scheme so the new `RabbitMqDispatchPipeline` tests assert against the corrected tags from day one.

## Verification gates

Per item, before merging the bundled PR:

- `dotnet test` against `src/ServiceConnect.Client.RabbitMQ.Tests` (or wherever `MessageRetryHandlerTests` / `RabbitMqConsumerHostTests` currently live) and `src/ServiceConnect.UnitTests` passes.
- Integration tests under `src/ServiceConnect.IntegrationTests` (Testcontainers RabbitMQ) pass — guards against AMQP frame-sequence regressions in Item 3.
- Manual diff of metric tag sets emitted by the consumer path: only the retry-attempt counter's tag set should differ between the pre- and post-PR runs.
- No new analyzer warnings; MA0051 (200-line method limit) no longer flags `RabbitMqConsumerHost`.

## Out of scope (entire spec)

- Other `notes.md` follow-ups: tracing refactor, `ServiceCollectionExtensions` reorganisation, code-review pass, history rewrite, removing co-author trailers. Each gets its own spec.
- New metrics or counters.
- Changes to retry policy, error-exchange routing, or shutdown semantics.
- Any change to `Consumer.cs` beyond the construction-site updates required by Items 2 and 3.
