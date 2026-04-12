# Async/Threading Critical Fixes — Design Spec

**Date:** 2026-04-12
**Scope:** C-01, C-02, C-03, C-04, C-05, C-06 from code review findings
**Breaking changes:** Yes — IDisposable removed from public interfaces (IBus, IProducer, IConsumer)

## Problem

The RabbitMQ transport layer has six async/threading issues verified against source code:

1. **C-01/C-02:** `Producer.Dispose()` and `Connection.Dispose()` use `_ = Task.Run(...)` to fire-and-forget async cleanup. Resources may leak if the process exits before the discarded task completes.
2. **C-04:** `Client.CloseChannel()` uses `.GetAwaiter().GetResult()` on `CloseAsync()`, risking deadlocks under synchronization contexts.
3. **C-03:** `Client.cs` line 122 invokes `_consumerEventHandler!()` with the null-forgiving operator and no null guard. If called before `StartConsumingAsync()` completes, this throws a `NullReferenceException`.
4. **C-05:** 11 `await` calls in `Client.cs` are missing `ConfigureAwait(false)`. As library code, all awaits should use it to avoid capturing unnecessary synchronization context.
5. **C-06:** `Retry.cs` uses a static `new Random()` instance which is not thread-safe. Concurrent retry calculations can produce corrupted results.

## Approach

Fix in two phases delivered as four commits:

1. **Disposal chain refactor** (C-01, C-02, C-04) — single commit, since removing IDisposable from one type cascades through the chain.
2. **Mechanical fixes** (C-03, C-05, C-06) — one commit each, independent of each other and the disposal chain.

## Design

### Commit 1: Async Disposal Chain

Replace all `IDisposable` with `IAsyncDisposable` across the entire disposal chain. Remove synchronous `Dispose()` methods entirely.

**Current chain:**
```
Bus.Dispose()  [sync]
├── _producer?.Dispose()           [fire-and-forget: _ = Task.Run(...)]
└── _consumer?.Dispose()           [sync]
    └── Consumer.Dispose()         [sync]
        ├── client.Dispose()       [sync, CloseChannel uses .GetAwaiter().GetResult()]
        └── _connection?.Dispose() [fire-and-forget: _ = Task.Run(...)]
```

**Target chain:**
```
Bus.DisposeAsync()  [async]
├── _producer?.DisposeAsync()           [awaits channel/connection close]
└── _consumer?.DisposeAsync()           [async]
    └── Consumer.DisposeAsync()         [async]
        ├── client.DisposeAsync()       [awaits CloseChannelAsync()]
        └── _connection?.DisposeAsync() [awaits connection close]
```

**Interface changes:**

| Interface | Change |
|-----------|--------|
| `IProducer` | Remove `IDisposable` (keep `IAsyncDisposable`, already declared) |
| `IServiceConnectConnection` | Add `IAsyncDisposable`, remove `void Dispose()` method |
| `IConsumer` | Replace `IDisposable` with `IAsyncDisposable`, add `ValueTask DisposeAsync()` |
| `IBus` | Replace `IDisposable` with `IAsyncDisposable`, add `ValueTask DisposeAsync()`. Convert `StopConsuming()` to `Task StopConsumingAsync()`. |

**Implementation changes:**

| File | Changes |
|------|---------|
| `Producer.cs` | Remove `Dispose()`. Fix `DisposeAsync()` to properly await all cleanup — no fire-and-forget. |
| `Connection.cs` | Remove `Dispose()`. `DisposeAsync()` already exists and is mostly correct. |
| `Client.cs` | Remove `Dispose()` and sync `CloseChannel()`. Add `DisposeAsync()` that calls `CloseChannelAsync()`. |
| `Consumer.cs` | Remove `Dispose()`. Add `DisposeAsync()` that awaits each client's `DisposeAsync()` then the connection's `DisposeAsync()`. |
| `Bus.cs` | Remove `Dispose()`. Add `DisposeAsync()` that calls a new `StopConsumingAsync()` then awaits `_producer?.DisposeAsync()`. Convert `StopConsuming()` to `StopConsumingAsync()`. |

**Test impact:** E2E tests that create a Bus need `await using` instead of manual `Dispose()` calls. All modified test files are already listed in git status as modified.

### Commit 2: Null Handler Guard (C-03)

In `Client.cs` around line 122, add a null check before invoking `_consumerEventHandler`:

- If null: log an error, nack the message (so it returns to the queue), return early.
- Remove the null-forgiving `!` operator.

### Commit 3: ConfigureAwait(false) (C-05)

Add `.ConfigureAwait(false)` to all 11 `await` calls in `Client.cs` that are missing it. Applied after commit 1 since the disposal refactor may change some of these lines.

Affected lines (pre-refactor numbers — exact lines will shift after commit 1):
- `ProcessMessage`, `BasicAckAsync`, `BasicNackAsync`, `_consumerEventHandler`, `BasicPublishAsync` (x3), `CreateChannelAsync`, `BasicQosAsync`, `BasicConsumeAsync`, `QueueBindAsync`

### Commit 4: Random.Shared (C-06)

In `Retry.cs`:
- Remove `private static readonly Random Jitter = new();` (line 5)
- Replace `Jitter.Next(...)` with `Random.Shared.Next(...)` at the call site (line 71)

`Random.Shared` is thread-safe by design in .NET 6+.

## Commit Sequence

| # | Message | Scope |
|---|---------|-------|
| 1 | `fix: replace IDisposable with IAsyncDisposable across disposal chain` | C-01, C-02, C-04 |
| 2 | `fix: add null guard for consumer event handler in Client` | C-03 |
| 3 | `chore: add ConfigureAwait(false) to all async calls in Client` | C-05 |
| 4 | `fix: use Random.Shared for thread-safe jitter in Retry` | C-06 |

## Out of Scope

- CancellationToken support (B-01/R-016) — breaking API change, separate effort
- Mutable dictionary exposure (B-02) — medium priority, deferred
- SSL certificate revocation (T-01) — deferred
- Exception type consistency (T-02) — deferred
- All other deferred issues documented in `docs/remaining-issues.md`
