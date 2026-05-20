# Publish-timeout retry — design spec

> **For agentic workers:** Design document. Implementation plan lives in `docs/superpowers/plans/2026-05-20-publish-timeout-retry.md` (produced by `superpowers:writing-plans` after this spec is approved).

## Goal

Bring the framework's publish-side retry behaviour in line with its documented at-least-once contract by treating `TimeoutException` (the publish-confirm ack timeout) as a transient, retriable signal instead of a terminal failure. Cap the retry loop's wall-clock budget so a permanently-dead broker can't hold a publisher for ~30 minutes.

## Context

The chaos-message-ledger investigation (`docs/superpowers/specs/2026-05-20-chaos-message-ledger-design.md` + the 5-minute chaos soak under commit `9e24985e`) classified all 18 lost flows as **`Failed-and-lost`** with zero **`Acked-but-lost`**:

- The broker is NOT silently dropping persisted publishes (hypothesis H1 ruled out).
- The 18 publishes' awaited tasks all threw `TimeoutException` — the publish-confirm ack timer in `Producer.PublishWithTimeoutAsync` fired before the framework's auto-recovery rebuilt the channel.
- `IsRetriablePublishException` (`src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs:270-288`) deliberately excludes `TimeoutException` from the retry path, propagating it unchanged to the caller.

The framework's stated reasoning (`Producer.cs:267-269`):

> TimeoutException comes from `PublishWithTimeoutAsync` when the broker ack doesn't arrive within `_publishTimeout`. A reconnect won't help — the connection is considered stalled/dead; propagate immediately so callers can decide whether to retry at a higher level.

This contradicts the framework's own at-least-once contract in `IBus.cs:9-27`, which states that delivery is at-least-once, duplicates are allowed, and consumers must either be idempotent or use a `BeforeConsuming` + `OnConsumedSuccessfully` dedup filter. The publisher refusing to retry to avoid the very duplicate the consumer is required to tolerate is asymmetric: every caller (the harness, application code, anyone) ends up reinventing the same retry layer because the producer pushes the responsibility upward against its own delivery promise.

## Non-goals

- Writing a reference dedup filter pair (`BeforeConsuming` + `OnConsumedSuccessfully`). The xmldoc on `IBus.cs:24-27` describes the pattern as guidance; no reference implementation exists today. That's a separate enhancement; this spec keeps the existing contract surface and only fixes the publisher's adherence to it.
- Adding exactly-once delivery semantics. The framework remains at-least-once; the change only restores symmetry between the producer's behaviour and the documented contract.
- Touching the `PublishException` (broker nack) or `OperationCanceledException` (caller cancel) cases. Both legitimately do not benefit from retry and stay non-retriable.
- Changing the default `PublishTimeout` (30s). The retry change makes the 30s value tolerable inside a chaos window without lengthening it — long timeouts hide stalled brokers, short timeouts hide nothing because retries cover the failure.
- Migrating any other framework retry / connection layer (`ProducerConnection.EnsureConnectedAsync`, `Retry.DoAsync`).

## Architecture

### Change 1: `TimeoutException` becomes retriable

`src/ServiceConnect.Client.RabbitMQ/Producer/Producer.cs:270-288`:

```csharp
// Broker-side nacks (PublishException) are usually poison messages — rejected by a
// policy (e.g. max-length, unroutable, access denied). Retrying them burns the entire
// retry budget against a condition that will not heal, and worse, triggers a reconnect
// loop that tears down the connection for a publish-layer error. Only transport-level
// failures should flow into the reconnect-retry path.
//
// OperationCanceledException is caller-driven cancellation — retrying it would
// violate the caller's intent.
//
// TimeoutException is now retriable. The publish-confirm ack timer fires when the
// broker has not acknowledged the publish within _publishTimeout. The framework's
// at-least-once delivery contract (IBus.cs:9-27) allows duplicate delivery; a retry
// on a fresh channel is the correct response to a stalled confirm and aligns the
// producer with its own contract.
private static bool IsRetriablePublishException(Exception ex)
{
    if (ex is global::RabbitMQ.Client.Exceptions.PublishException) return false;
    if (ex is OperationCanceledException) return false;
    return true;
}
```

The dedicated `catch (TimeoutException)` arm in `ExecuteRetryingPublishAsync` (lines 180-186) is removed. `TimeoutException` falls through to the existing `catch (Exception ex) when (IsRetriablePublishException(ex))` arm at line 219, which:

1. Records `lastException`.
2. Calls `_producerConnection.MarkResetRequired()`. `PublishWithTimeoutAsync` already calls this on line 776 before throwing the timeout — the second call is idempotent (a single boolean flag) and safe.
3. Sleeps `JitteredRetryDelay()` (10s mean ± 50% jitter).
4. Loops; the next attempt's `EnsureConnectedAsync` prologue sees the reset flag and rebuilds the channel before the next `BasicPublishAsync`.

### Change 2: Wall-clock cap on the retry loop

Today the retry budget is `RetryCount × RetrySeconds` plus per-attempt connection backoff. Default values give 60 × 10s + (each EnsureConnectedAsync may itself take minutes) ≈ 10-30 minutes per publish before the loop gives up. With `TimeoutException` retries layered on, a permanently-dead broker could hold a publisher even longer. Operators want a predictable upper bound.

Introduce `RabbitMqOptions.MaxPublishWaitTime` (`TimeSpan?`, default `120s` when null) and check it at the top of each retry iteration in `ExecuteRetryingPublishAsync`:

```csharp
var publishStartedAt = Stopwatch.GetTimestamp();
for (int attempt = 0; attempt <= _retryCount; attempt++)
{
    if (Stopwatch.GetElapsedTime(publishStartedAt) >= _maxPublishWaitTime)
    {
        throw new TimeoutException(
            $"Publish wall-clock budget {_maxPublishWaitTime.TotalSeconds:0.###}s exhausted " +
            $"after {attempt} attempt(s); last error: {lastException?.Message ?? "<none>"}.",
            lastException);
    }
    ...
}
```

Wire `_maxPublishWaitTime` like the existing `_publishTimeout`:

- `RabbitMQSettingKeys.MaxPublishWaitTime = "MaxPublishWaitTime"` constant.
- `RabbitMqOptions.MaxPublishWaitTime { get; set; }` nullable property with xmldoc.
- `RabbitMQExtensions` applies it to transport settings (mirrors `PublishTimeout` at line 98).
- `Producer` constructor reads it via `GetSetting(settings, RabbitMQSettingKeys.MaxPublishWaitTime, TimeSpan.FromSeconds(120), v => (TimeSpan)v)`.
- Validation rejects values ≤ `TimeSpan.Zero` (matches the existing `PublishTimeout` validator at line 123).
- A value of `Timeout.InfiniteTimeSpan` disables the cap (operators who want the old 30-minute behaviour can opt back in).

### Change 3: Preserve `MessageId` across retries

The retry loop calls `lockedAction` (a delegate that issues the actual `BasicPublishAsync`) on each attempt. Both callers (`SendAsync` and `PublishAsync`) build the `BasicProperties` ONCE outside the retry loop and pass the captured instance into the delegate; `OutboundHeaderBuilder` is invoked once. The `MessageId` is therefore stable across retries by construction.

This invariant is load-bearing for at-least-once dedup: a consumer-side `BeforeConsuming` filter that records completed `MessageId`s must see the SAME id on the retry as on the original. If a future refactor moves `OutboundHeaderBuilder` inside the retry loop (e.g. for "let's regenerate headers on each attempt") the dedup pattern silently breaks. We add a test that pins the invariant by capturing `BasicProperties.MessageId` on each call to a fake `IChannel.BasicPublishAsync` and asserting equality across the two captures.

### Change 4: `PublishWithTimeoutAsync` xmldoc reflects the new role

The method's xmldoc currently says `TimeoutException` is propagated for the caller to decide. Update it to:

```csharp
/// <summary>
/// Publishes via <see cref="IChannel.BasicPublishAsync"/> under a linked
/// <see cref="CancellationTokenSource"/> that fires after <c>_publishTimeout</c>.
/// </summary>
/// <remarks>
/// <para>
/// If the caller's <paramref name="cancellationToken"/> fires, an
/// <see cref="OperationCanceledException"/> propagates unchanged.
/// </para>
/// <para>
/// If the broker ack does not arrive within <c>_publishTimeout</c>, the
/// linked CTS fires and the method throws <see cref="TimeoutException"/>.
/// <see cref="ProducerConnection.MarkResetRequired"/> is also called so the
/// next attempt's <see cref="EnsureConnectedAsync"/> rebuilds the channel
/// before re-publishing. The exception is retriable in
/// <see cref="ExecuteRetryingPublishAsync"/>: a fresh channel is rebuilt
/// and the same <c>BasicProperties</c> (including <c>MessageId</c>) is
/// re-published. The framework's at-least-once contract (<see cref="IBus"/>
/// xmldoc) permits the broker to deliver both the original and the
/// retry — consumers must be idempotent or use the
/// <c>BeforeConsuming</c> + <c>OnConsumedSuccessfully</c> dedup filter
/// pair to short-circuit duplicates.
/// </para>
/// </remarks>
```

`IBus.SendAsync` / `PublishAsync` xmldoc is updated with a one-paragraph addition noting that transient publish-confirm timeouts are now retried under the at-least-once contract. No behavioural change at the `IBus` surface — the contract was always at-least-once; this change makes the producer adhere to it.

## Testing

### New unit tests in `src/ServiceConnect.Client.RabbitMQ.Tests/Producer/ProducerTests.cs` (or a sibling)

1. **`TimeoutException_triggers_retry_and_succeeds_on_second_attempt`** — a hand-rolled fake `IChannel` (or fake producer connection) returns `TimeoutException` on the first `BasicPublishAsync`, then `Task.CompletedTask` on the second. Assert the publish completes successfully and the fake was called twice. Use `Producer.RetryDelayForTests` to short-circuit the inter-attempt delay (the framework already exposes this for testability — see `Producer.cs:237`).

2. **`TimeoutException_retry_preserves_MessageId`** — fake records the `BasicProperties.MessageId` on every call. Make the first call throw `TimeoutException`; let the second succeed. Assert both recorded `MessageId` values are equal and non-empty.

3. **`MaxPublishWaitTime_caps_retry_loop_at_wall_clock_budget`** — configure `MaxPublishWaitTime = 200ms`, `RetrySeconds = 50ms`. Inject a fake that always throws `TimeoutException`. Assert the publish throws a wrapping `TimeoutException` whose message mentions "wall-clock budget" within ~250-400ms (the cap + one final attempt's overhead). Assert it does NOT take the full `RetryCount × RetrySeconds = 60 × 50ms = 3000ms`.

4. **`MaxPublishWaitTime_InfiniteTimeSpan_disables_cap`** — set the option to `Timeout.InfiniteTimeSpan`, configure a small `RetryCount = 2`, fake that always throws. Assert the loop exhausts the retry count (not the wall-clock cap) and the final exception is the underlying retriable one, not a wall-clock-budget message.

5. **`PublishException_still_propagates_without_retry`** — fake throws `PublishException` (broker nack). Assert the publish throws after exactly one attempt; the fake is called exactly once.

6. **`MaxPublishWaitTime_validation_rejects_zero_or_negative`** — set `RabbitMqOptions.MaxPublishWaitTime = TimeSpan.Zero`, call `Validate()`, assert the returned error list contains the expected message.

### Existing tests to update

Any existing test in `src/ServiceConnect.Client.RabbitMQ.Tests/` that asserts `TimeoutException` propagates without retry. Search for the symbol and inspect; update the test's expectations or replace it with a regression test that the retry now kicks in.

### Integration verification

After the change, re-run the 5-minute chaos soak (`--mode soak --duration 00:05:00 --chaos docker --chaos-interval 00:00:50 --chaos-downtime 00:00:20`). Expected:

- `Failed-and-lost` count in the message-ledger section drops from 18 to 0 (or near-0; the cap could fire if a kill happens to align with the wall-clock budget across consecutive cycles, but that's rare).
- `Per-message redeliveries` count rises modestly. Any retry that the broker accepted on both attempts produces a duplicate that the broker delivers twice. The harness's `MessageLedger` counts these and the rise validates the retry is doing its job.
- `FlowAccounting.MissingFlows` count drops in proportion to the `Failed-and-lost` count.
- Per-pattern direction failures (currently 19 across streaming/routing-slip/aggregator/etc.) drop sharply.

## Configuration surface added

| Setting | Type | Default | Validator |
|---|---|---|---|
| `RabbitMqOptions.MaxPublishWaitTime` | `TimeSpan?` | 120s (when null) | `> TimeSpan.Zero` or `Timeout.InfiniteTimeSpan` |

The harness's `HarnessHost.BuildServices` does not need to set this — the default suffices for the chaos cycle's downtime (20s) plus container restart (~5s) plus framework reconnect (~5s) plus a few retry attempts. Operators with longer downtimes can set it explicitly via `RabbitMqOptions`.

## Definition of done

- `IsRetriablePublishException` returns `true` for `TimeoutException`.
- The dedicated `catch (TimeoutException)` arm in `ExecuteRetryingPublishAsync` is removed.
- `RabbitMqOptions.MaxPublishWaitTime` exists, validates, and bounds the retry loop.
- Six new unit tests pass.
- Any existing test asserting "TimeoutException not retried" is updated to the new contract.
- xmldoc on `PublishWithTimeoutAsync`, `IsRetriablePublishException`, and `RabbitMqOptions.MaxPublishWaitTime` reflects the new behaviour.
- 5-minute chaos soak reports `Failed-and-lost ≈ 0`, with the per-pattern failures dropping commensurately.

## Risks

| Risk | Mitigation |
|---|---|
| Retry storm against a permanently-dead broker | The new `MaxPublishWaitTime` cap (default 120s) bounds the total wait. Operators tune up only for known-long downtimes. |
| Duplicate delivery surfaces consumer-side bugs that were previously hidden | At-least-once is the documented contract — duplicates were always possible (broker redelivery is a separate source). Any consumer not idempotent today has a latent bug; this change makes it more likely to surface but does not introduce a new contract violation. The recommendation to use the dedup filter pair (xmldoc `IBus.cs:24-27`) is the canonical remediation. |
| The retry preserves `MessageId` only because the publish path happens to construct `BasicProperties` once outside the loop | Add the explicit pin-test (test 2 above) so any future refactor that moves header construction inside the loop fails CI. |
| `MarkResetRequired` is now called twice on the `TimeoutException` path (once in `PublishWithTimeoutAsync`, once in the retry catch) | The method is idempotent — it sets a boolean flag. Verified by reading `ProducerConnection.MarkResetRequired`; no behavioural concern. |
| Operator surprise — code that previously caught `TimeoutException` and retried at a higher level now no longer needs to, and may have its retry path silently masked | Release-notes / xmldoc on `IBus.SendAsync` documents the change. Caller-side retries become benign double-work but not incorrect. |
| Ordering-sensitive streams (streaming chunks, routing-slip hops) see a chunk arrive AFTER its successor | The chunk-reassembly path uses `PacketNumber` for in-order reconstruction; the routing-slip header sequences hops. Neither pattern is timestamp-ordered at the delivery layer. Verified by reading the streaming reassembler. |
