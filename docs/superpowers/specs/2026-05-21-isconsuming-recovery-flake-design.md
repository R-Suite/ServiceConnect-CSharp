# `IBus.IsConsuming` post-recovery flake — design spec

> **For agentic workers:** Design document. Implementation plan lives in `docs/superpowers/plans/2026-05-21-isconsuming-recovery-flake.md` (produced by `superpowers:writing-plans` after this spec is approved).

## Goal

Stop `IBus.IsConsuming` from returning `false` after the chaos scheduler's last kill cycle when the bus is actually consuming normally. After this work, the harness's `RecoveryAssertion` should not fire spuriously even across many chaos runs, and `BusConsumingHealthCheck` should report Healthy whenever delivery is in fact resuming.

## Context

The second of two consecutive 5-minute chaos soaks (4 kills × 21–22 s downtime each) produced a process-level assertion failure:

```
- **process** — chaos: recovery budget exceeded: alpha=NOT consuming, beta=NOT consuming
```

Concurrent evidence shows the bus IS functioning:

- `Flows: 38,052 / 38,052 passed` — handlers fired, every flow's expected dispatch arrived.
- Message ledger clean: `Acked-but-lost: 0`, `Failed-and-lost: 0`, `Per-message redeliveries: 3`.
- All 4 chaos events show clean 21-22 s downtimes; auto-recovery completed each time.

So delivery resumed; the symptom is purely that `IBus.IsConsuming` reports `false` after recovery completes.

## Diagnosis

`Bus.IsConsuming` (`src/ServiceConnect/Bus.cs:108-111`):

```csharp
public bool IsConsuming =>
    _consuming
    && Volatile.Read(ref _disposed) == 0
    && !(_consumer?.IsCancelledByBroker ?? false);
```

- `_consuming` flips false only on `StopConsumingAsync` / `DisposeAsync` — neither runs during chaos. ✓
- `_disposed` flips only on dispose — same. ✓
- `_consumer?.IsCancelledByBroker` is the smoking gun: `false` would let `IsConsuming` return `true`.

`Consumer.IsCancelledByBroker` (`src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs:97`):

```csharp
public bool IsCancelledByBroker => _clients.OfType<RabbitMqConsumerHost>().Any(c => c.IsCancelledByBroker);
```

Returns true if ANY host has its flag set. Each host's flag lives at `RabbitMqChannelHost._consumerCancelledByBroker` (`int` accessed via `Interlocked.Exchange`):

- **Set** in `RabbitMqConsumerHost.OnConsumerUnregisteredAsync` (line 441) when RabbitMQ.Client raises `AsyncEventingBasicConsumer.UnregisteredAsync` (broker-initiated `basic.cancel`, also fired during channel close).
- **Cleared** in `RabbitMqConsumerHost.OnConnectionRecoverySucceededAsync` (line 504) when RabbitMQ.Client raises `IConnection.RecoverySucceededAsync` after topology recovery completes.

### The race

RabbitMQ.Client dispatches `UnregisteredAsync` and `RecoverySucceededAsync` through independent async event queues. Across multiple kill cycles, the events for an EARLIER cycle can arrive **after** the events for a later cycle:

```
Kill 4 timeline (broker dies at T=0, restarts at T=20):
T-N    Channel for kill 4 already starting to close, UnregisteredAsync queued.
T=0    Broker SIGTERM. Channel close. UnregisteredAsync queued for the kill-4 consumer tag.
T=20   Broker restart. Auto-recovery reconnects, topology recovers, BasicConsumeAsync re-issued, new tag.
T=22   RecoverySucceededAsync fires → NotifyRecoverySucceeded sets _consumerCancelledByBroker = 0.
T=22+ε The queued UnregisteredAsync from T=0 finally executes → NotifyBrokerCancelled sets _consumerCancelledByBroker = 1.
```

Result: flag latched at 1 even though recovery completed. The harness's `RecoveryAssertion` polls `IsConsuming`, observes `false` for 30 s, and reports the spurious failure.

This is consistent with the variance run: only the most recent recoveries are affected (4th kill's queue depth is highest by then), all flows pass (recovery WORKED), but the flag is stale.

A second contributing factor: `Consumer.IsCancelledByBroker` returns true if ANY host's flag is set. Across two buses × `ConsumerCount` hosts each, any one of them latching produces the failure. Cross-bus alignment isn't required.

## Non-goals

- Rewriting the `_consuming` / `_disposed` semantics. Both are correct.
- Changing `RabbitMQ.Client` event ordering. We can't.
- Removing `BusConsumingHealthCheck`. Its semantics ("Unhealthy on broker-side cancel") are still desired — the issue is that we're reporting Unhealthy when the broker isn't actually cancelling anything.
- Raising the recovery budget (option C from earlier discussion). Masks the race without fixing it; would also paper over a real broker-side cancel that legitimately takes longer.

## Architecture

### Change — gate `OnConsumerUnregisteredAsync` on the live consumer tag

`RabbitMqConsumerHost` already tracks the live consumer tag via `_consumerTag` (string), reset on each `ConsumerTagChangeAfterRecoveryAsync` event so it always reflects the broker's CURRENT tag for our consumer.

`ConsumerEventArgs.ConsumerTags` (RabbitMQ.Client) carries the tags the unregistered event applies to. A late event from a previous cycle has the OLD tag; the current tag is whatever topology recovery's BasicConsumeAsync returned.

The fix: in `OnConsumerUnregisteredAsync`, compare the event's tag(s) against the live `_consumerTag`. If none match, the event is stale (it refers to a consumer that has already been superseded by post-recovery `BasicConsumeAsync`) and we **do not** mark the host as cancelled.

```csharp
private Task OnConsumerUnregisteredAsync(object? sender, ConsumerEventArgs args)
{
    // Late-firing UnregisteredAsync from an earlier kill cycle can land AFTER
    // RecoverySucceededAsync has reset the cancellation flag. Distinguish a stale
    // event (refers to a consumer tag that has already been replaced by topology
    // recovery's BasicConsumeAsync) from a fresh one by comparing the event's
    // tags against the live _consumerTag. Stale events are observational only —
    // they do not represent a broker-initiated cancellation of the live consumer.
    var liveTag = Volatile.Read(ref _consumerTag);
    if (!string.IsNullOrEmpty(liveTag)
        && args.ConsumerTags is { Length: > 0 } tags
        && !tags.Contains(liveTag, StringComparer.Ordinal))
    {
        _logger.LogDebug(
            "Ignoring stale AMQP consumer unregistered event on queue '{Queue}' for tags [{StaleTags}]; live tag is '{LiveTag}'",
            _queueName,
            string.Join(", ", tags),
            liveTag);
        return Task.CompletedTask;
    }

    _channelHost.NotifyBrokerCancelled();
    _logger.LogWarning(
        "AMQP consumer '{ConsumerTag}' unregistered by broker (broker-initiated shutdown) on queue '{Queue}'; reporting unhealthy via BusConsumingHealthCheck",
        _consumerTag, _queueName);
    return Task.CompletedTask;
}
```

The fast path (single kill, no race) still works: the broker cancels the live consumer, the event's tag matches `_consumerTag`, the flag is set. The slow path (late event from a previous kill) is correctly ignored.

The semantics align with the framework's at-least-once contract: the flag now reflects "the broker has cancelled our CURRENT live consumer", not "a consumer we used to have was cancelled at some point". The latter is meaningless for operational health.

### Edge case — first-ever delivery

Before `BasicConsumeAsync` returns the first time, `_consumerTag` is `null`. If `UnregisteredAsync` somehow fires before then (unlikely but possible during start-up failure), the `string.IsNullOrEmpty(liveTag)` check fails and the late-detection branch is skipped — the flag is set as before. This preserves the original behaviour for the start-up edge case.

### Edge case — empty `ConsumerTags`

If `args.ConsumerTags` is empty, we cannot identify which consumer the event refers to. The check falls through to `NotifyBrokerCancelled` — conservative behaviour (assume the event is real). Empty-tags events are not expected from RabbitMQ.Client in practice but the guard is cheap.

### Edge case — multiple consumer tags

`ConsumerEventArgs.ConsumerTags` is an array because a single channel can host multiple consumers. The check `tags.Contains(liveTag)` correctly handles the multi-consumer case: if our live tag is in the unregistered set, we ARE being cancelled; if it's not, the event is for someone else (also stale from our perspective).

## Non-changes

- `RabbitMqChannelHost.NotifyBrokerCancelled` / `NotifyRecoverySucceeded` are unchanged. They remain symmetric `Interlocked.Exchange` toggles.
- `OnConnectionRecoverySucceededAsync` is unchanged. It still resets the flag on every recovery — that's the right behaviour.
- The 30 s recovery-budget in `RecoveryAssertion` is unchanged. Stop using budget as a workaround.

## Tests

### Unit tests

`src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostStaleUnregisteredTests.cs` (new) — 4 tests:

1. **`OnConsumerUnregisteredAsync_with_live_tag_marks_cancelled`** — set `_consumerTag = "live"`, fire `UnregisteredAsync` with `ConsumerTags = ["live"]`. Assert `_channelHost.IsCancelledByBroker` is true.

2. **`OnConsumerUnregisteredAsync_with_stale_tag_does_not_mark_cancelled`** — set `_consumerTag = "live"`, fire `UnregisteredAsync` with `ConsumerTags = ["stale-from-prior-kill"]`. Assert `_channelHost.IsCancelledByBroker` is false.

3. **`OnConsumerUnregisteredAsync_with_null_live_tag_marks_cancelled_fallback`** — leave `_consumerTag` unset, fire `UnregisteredAsync` with any tags. Assert flag is set (start-up edge case).

4. **`OnConsumerUnregisteredAsync_with_empty_tags_marks_cancelled_fallback`** — set `_consumerTag = "live"`, fire with `ConsumerTags = []`. Assert flag is set (defensive default).

The unit tests instantiate `RabbitMqConsumerHost` with a minimal mock-channel + connection (the existing `ProducerInternals` / `ConsumerInternals` reflection helpers used by other RabbitMQ unit tests).

### Integration verification

After the fix, `./verify-all.sh` should run end-to-end without a recovery-assertion failure over **three consecutive chaos runs**. The chaos timing has natural jitter; the goal is to eliminate the race condition entirely, not reduce its frequency.

## Definition of done

- `OnConsumerUnregisteredAsync` gates on live-tag comparison; stale events log at Debug and do not set the flag.
- 4 unit tests pinning the gate's behaviour pass.
- Three consecutive 5-minute chaos soaks in `./verify-all.sh` pass with no `RecoveryAssertion` failures.
- Existing health-check / `IsConsuming` unit tests still pass (no regression in the legitimate-cancel path).

## Risks

| Risk | Mitigation |
|---|---|
| `ConsumerEventArgs.ConsumerTags` is named differently in the project's RabbitMQ.Client version | Verify by reading the package's source / docs at plan-time. If the property name differs, adapt the gate; the shape (event carries the tags it applies to) is what matters. |
| A genuine broker-side cancel of the live consumer is mis-classified as stale | The gate compares against `Volatile.Read(ref _consumerTag)`, which is the broker's CURRENT tag for our consumer. A genuine cancel has the matching tag (broker cancels what it just delivered to). Stale events have the prior tag (the broker has already issued a new tag through topology recovery). |
| `_consumerTag` is updated AFTER `UnregisteredAsync` fires, racing the gate | `_consumerTag` is updated via `Volatile.Write` in `OnConsumerTagChangedAfterRecoveryAsync` (line 524). Recovery's BasicConsumeAsync return assigns the new tag at line 272 via `Volatile.Write`. Both writes happen-before any subsequent `UnregisteredAsync` read because RabbitMQ.Client serialises consumer-side dispatch through its async event queue. |
| New tests need to invoke private/internal events on `RabbitMqConsumerHost` | The existing `ProducerInternals` reflection helper has a sibling `ConsumerInternals` pattern (if it exists) or can be created. Alternative: refactor `OnConsumerUnregisteredAsync` to call an `internal` method that takes the event args directly; the test calls the internal method without going through the event delegate. |
