# Branch Regression Remediation — Design

**Date:** 2026-04-18
**Branch:** `improvements-and-fixes`
**Workspace note:** user requested planning on the current branch with no worktree.
**Source:** 2026-04-18 branch review findings against `master`

## Goal

Fix the confirmed correctness regressions from the branch review without widening the highest-blast-radius public interfaces unless there is no smaller safe option.

## Scope

This design covers three remediation tracks:

1. RabbitMQ transport safety and lifecycle correctness
2. Timeout claiming, lease ownership, and release/remove correctness
3. Request-reply semantic restoration and endpoint option handling

## Non-goals

- Reverting the async refactors wholesale
- General transport or persistence redesign beyond the reviewed regressions
- Performance-only work already covered by separate design docs
- Direct breaking changes to `IBus` or `ITimeoutStore`
- Unrelated cleanup, naming, or documentation sweeps

## GitNexus Risk Summary

GitNexus impact analysis on the current branch shows:

- `RabbitMqConsumerHost` — `CRITICAL`
- `Producer` — `CRITICAL`
- `RequestReplyManager` — `CRITICAL`
- `MongoDbTimeoutStore` — `MEDIUM`
- `InMemoryTimeoutStore` — `MEDIUM`
- `ITimeoutStore` — `CRITICAL` to change directly
- `IBus` — `CRITICAL` to change directly

That risk profile drives two design constraints:

1. Fix the transport track first because it has the widest runtime blast radius.
2. Keep `ITimeoutStore` and `IBus` stable; prefer narrow extension points or internal changes over direct contract churn.

## Execution Order

### 1. RabbitMQ transport safety

This resolves the highest operational risk first: poison-message loops, transient publish failure regressions, and shutdown behavior that can close the channel under active handlers.

### 2. Timeout ownership and locking

This fixes duplicate or stale timeout dispatch behavior after the transport is stable, while keeping the timeout store contract surface as small as possible.

### 3. Request-reply semantics

This restores `PublishRequestAsync` to the behavior documented by `IBus`, closes the endpoint-targeting gap in `SendRequestMultiAsync`, and aligns tests with the intended contract.

---

## Track 1 — RabbitMQ Transport Safety

### Problems being fixed

- `RabbitMqConsumerHost` permanently invalidates some messages but currently requeues them forever via `BasicNack(..., requeue: true)`.
- `Producer` no longer retries or reconnects on per-message publish/send failures.
- `RabbitMqConsumerHost.DisposeAsync` waits for in-flight work but does not first stop new deliveries, and can close the channel while handlers are still using it for retry or audit publishes.

### Design

#### 1. Permanent invalid-message path becomes terminal, not requeueing

Treat these inbound cases as permanently invalid:

- missing `TypeName` and `FullTypeName`
- body larger than the configured max inbound size
- header count above the configured hard limit
- any header `byte[]` value above the configured hard limit

For these cases, `RabbitMqConsumerHost` should bypass handler invocation and publish the original delivery directly to the configured error exchange with sanitized failure metadata, then ack the original delivery.

Implementation shape:

- Add a dedicated terminal-failure path in `MessageRetryHandler` (for example `HandleTerminalFailureAsync`) that:
  - does **not** increment `RetryCount`
  - stamps the same sanitized exception payload shape used by the max-retries error path
  - publishes to the error exchange exactly once
- `RabbitMqConsumerHost.EventAsync` should call that terminal-failure path for permanent invalid messages, then mark the delivery as processed only after the error publish succeeds.

If the error publish itself fails because the broker/channel is unavailable, the delivery should fall back to the existing transport-failure behavior rather than being acked and lost. That means the message may be redelivered while the transport is unhealthy, but it must not sit in a normal-operation infinite poison loop anymore.

#### 2. Restore per-message publish/send retry and reconnect

Keep the async producer structure, but restore the lost retry/reconnect behavior around each publish/send operation.

Implementation shape:

- Wrap `PublishWithRetryAsync` in `Retry.DoAsync(...)` using the existing `_retryCount` and `_retryTimeInSeconds` settings.
- On a failed publish or exchange declaration:
  - log the failure
  - tear down the current channel/connection
  - clear connection-scoped state such as declared exchanges
  - re-run `EnsureConnectedAsync`
  - retry the publish
- Keep the current message-size guards, async confirms, and connection semaphore behavior.

The goal is to restore the old fault tolerance without reverting to the pre-refactor implementation.

#### 3. Make consumer shutdown deterministic

`RabbitMqConsumerHost.DisposeAsync` should stop new deliveries before waiting for in-flight work.

Implementation shape:

- Store the `consumerTag` returned from `BasicConsumeAsync`.
- On dispose:
  - call `BasicCancelAsync` first if the consumer is still active
  - wait for `_messagesBeingProcessed` to drain until the configured grace deadline
  - if the grace window expires, close the channel and accept broker redelivery of unfinished work
  - only then perform queue deletion / channel close teardown

This prevents new work from racing in while shutdown is trying to drain the old work.

### Acceptance criteria

- Invalid messages are not requeued forever under normal operation.
- Invalid messages are visible in the error path with enough metadata to explain the rejection reason.
- A transient `BasicPublishAsync` / exchange-declare failure can recover by reconnecting and retrying.
- Consumer shutdown either drains cleanly before channel close or times out in a deterministic, test-covered way.

### Tests

- Update `RabbitMqConsumerHostTests` so oversized body/header cases assert `error publish + ack`, not `nack + requeue`.
- Add a host test for missing type headers taking the same terminal error path.
- Add producer unit tests where the first publish attempt fails, the connection is rebuilt, and the retry succeeds.
- Add consumer-host tests for:
  - `DisposeAsync` cancelling the consumer before waiting
  - graceful completion when a handler finishes inside the grace window
  - deterministic timeout when a handler never finishes

---

## Track 2 — Timeout Ownership And Locking

### Problems being fixed

- `InMemoryTimeoutStore.GetTimeoutsBatchAsync` returns due timeouts without claiming them, so repeated polls can return the same due timeout more than once before dispatch finishes.
- `MongoDbTimeoutStore` claims due timeouts with `LockedBy`, but `RemoveDispatchedTimeoutAsync` and `ReleaseDispatchedTimeoutAsync` ignore the owner token and can mutate a timeout that has already been reclaimed by another worker.
- `ProcessManagerTimeoutService` only passes `Id` back to the store today, so strict owner checks cannot be added by changing implementation only.

### Design

#### 1. Keep `ITimeoutStore` stable

Do **not** change `ITimeoutStore` directly. GitNexus marks it as `CRITICAL` blast radius, and the existing `TimeoutData` payload already carries the fields needed for owner-aware cleanup.

Instead, add a narrow secondary interface for stores that support owner-aware cleanup, for example:

```csharp
public interface ILeaseAwareTimeoutStore
{
    Task RemoveDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default);
    Task ReleaseDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default);
}
```

`ProcessManagerTimeoutService` will prefer the lease-aware interface when it is available and fall back to the legacy `ITimeoutStore` methods otherwise.

#### 2. Claim due timeouts in `InMemoryTimeoutStore`

Bring the in-memory store in line with the intended semantics already present in `TimeoutData`.

Implementation shape:

- Add a lock lease duration constant in `InMemoryTimeoutStore` matching the Mongo implementation.
- Change `GetTimeoutsBatchAsync` to take the write lock, not the read lock, and claim due eligible entries before returning them.
- A due timeout is eligible when:
  - `Locked == false`, or
  - `LockExpiresAt <= utcNow`
- Claimed entries get:
  - `Locked = true`
  - `LockedBy = batchOwner`
  - `LockExpiresAt = utcNow + leaseDuration`
- Future entries still determine `NextQueryTime`.

This keeps the existing sorted-set optimization while preventing duplicate in-memory dispatch under overlapping polls.

#### 3. Enforce owner checks in `MongoDbTimeoutStore`

Keep the current `LockedBy` claim model, but make release/remove owner-aware when the caller provides a lease token.

Implementation shape:

- Implement `ILeaseAwareTimeoutStore` on `MongoDbTimeoutStore`.
- Owner-aware remove filter: `Id && Locked && LockedBy == lockOwner`
- Owner-aware release filter: `Id && Locked && LockedBy == lockOwner`
- If the owner-aware mutation matches zero rows, treat that as a benign stale-owner no-op and optionally log at debug level.

The existing `ITimeoutStore` methods remain for compatibility. `ProcessManagerTimeoutService` should stop using them when the store also implements the lease-aware interface.

#### 4. Teach `ProcessManagerTimeoutService` to use owner-aware cleanup

`GetTimeoutsBatchAsync` already returns `TimeoutData`, which now carries the active `LockedBy` token from the claim step.

Implementation shape:

- During dispatch, read `timeout.LockedBy` from the claimed timeout.
- On success:
  - if `_finder is ILeaseAwareTimeoutStore` and `timeout.LockedBy != Guid.Empty`, call the owner-aware remove method
  - otherwise fall back to `ITimeoutStore.RemoveDispatchedTimeoutAsync(id)`
- On failure:
  - use the same owner-aware preference for release

This keeps the hosted service compatible with any third-party `ITimeoutStore` while enabling correct behavior for the built-in stores.

### Acceptance criteria

- A due in-memory timeout is not returned twice before it is released or removed.
- A reclaimed Mongo timeout cannot be released or deleted by a stale worker from an expired lease.
- `ProcessManagerTimeoutService` uses owner-aware cleanup when available and keeps the legacy fallback when it is not.

### Tests

- Add in-memory store tests proving a due timeout is hidden from the next poll until it is released or removed.
- Add Mongo store tests for:
  - owner-aware remove succeeds for the current owner
  - owner-aware release succeeds for the current owner
  - stale-owner remove/release become no-ops
- Add hosted-service tests proving it prefers the lease-aware path and falls back to legacy `ITimeoutStore` methods when the extension interface is absent.
- Re-run the existing process-manager timeout tests for both in-memory and Mongo persistence.

---

## Track 3 — Request-Reply Semantics

### Problems being fixed

- `SendRequestMultiAsync` ignores `RequestOptions.EndPoint` and only honors `EndPoints`.
- `Bus.PublishRequestAsync` currently delegates to `SendRequestMultiAsync`, so it behaves like buffered multi-send, not publish fan-out with callback-per-reply.
- `PublishRequestAsync` currently accepts endpoint-targeting options even though those conflict with publish semantics.

### Design

#### 1. Fix `SendRequestMultiAsync` endpoint handling

Targeting rules become:

- `EndPoints` set: send once to each endpoint in the list
- else `EndPoint` set: send once to that endpoint
- else: use the existing default send behavior via queue mapping / producer send pipeline

This aligns the multi-reply send API with the existing single-reply send API.

#### 2. Restore true publish semantics for `PublishRequestAsync`

`PublishRequestAsync` should publish the request once and invoke the callback as each reply arrives.

Keep `IBus` unchanged. The smallest contained change is to extend `IRequestReplyManager` with a publish-oriented method, for example:

```csharp
Task PublishRequestAsync<TRequest, TReply>(
    byte[] messageBytes,
    Dictionary<string, string> headers,
    RequestOptions options,
    Action<TReply> onReply,
    CancellationToken cancellationToken = default)
    where TRequest : Message
    where TReply : Message;
```

Implementation shape:

- `Bus.PublishRequestAsync` continues to build serialized bytes and filtered headers the same way as the other request APIs.
- It then delegates to the new request-reply manager method instead of routing through `SendRequestMultiAsync`.
- `RequestReplyManager` uses `_sendPipeline.ExecutePublishMessagePipelineAsync(...)` for the outbound request.
- Pending request state remains tracked in the same central dictionary used by `ProcessReply`.
- `ProcessReply` invokes `onReply` immediately as replies arrive.

Completion rules:

- `ExpectedReplyCount > 0`: complete when that many replies arrive or when timeout elapses, whichever happens first.
- `ExpectedReplyCount <= 0` or `null`: keep accepting replies until timeout elapses, while still invoking callbacks as they arrive.

This restores the documented `IBus.PublishRequestAsync` contract without touching `IBus` itself.

#### 3. Reject endpoint-targeting options on publish requests

If `RequestOptions.EndPoint` or `EndPoints` is supplied to `PublishRequestAsync`, reject the call with a clear argument exception that directs the caller to `SendRequestAsync` / `SendRequestMultiAsync`.

Publish semantics and endpoint targeting are mutually exclusive and should not silently degrade into send semantics.

### Acceptance criteria

- `SendRequestMultiAsync` honors `EndPoint` when `EndPoints` is absent.
- `PublishRequestAsync` publishes rather than sending.
- `PublishRequestAsync` invokes callbacks as replies arrive, not only after buffering a full result set.
- `PublishRequestAsync` rejects endpoint-targeting options with a clear error.

### Tests

- Add unit tests for `SendRequestMultiAsync` with a single `EndPoint`.
- Add unit tests for `PublishRequestAsync` option validation.
- Add request-reply manager tests proving callback invocation happens before overall completion when replies arrive early.
- Update `PublishRequestAsyncTests` so they verify true publish fan-out rather than queue-mapping-assisted send behavior.
- Add a two-responder publish-request E2E test that expects two callbacks when `ExpectedReplyCount = 2`.

---

## Cross-Track Verification

Before implementation is considered complete:

- unit tests covering each reviewed regression must exist and fail before the fix
- all touched RabbitMQ transport tests must pass
- all timeout service/store tests must pass
- request-reply unit and E2E tests must pass
- GitNexus change detection should confirm the touched scope matches these three tracks only

## Implementation Handoff

This spec is intentionally an umbrella design. The implementation phase should produce three separate plans, one per track, in the execution order above:

1. transport safety
2. timeout ownership and locking
3. request-reply semantics

Each plan should stay narrowly scoped to its track so that verification and rollback remain straightforward.

## Open Questions

None. The remaining design assumptions were resolved as follows:

- no worktree; remain on the current branch
- invalid inbound messages go to the error path rather than being dropped or made configurable
- `PublishRequestAsync` returns to true publish semantics
- endpoint-targeting options are rejected on publish requests
- timeout ownership uses a narrow secondary interface instead of changing `ITimeoutStore`
