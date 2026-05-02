# Phase 06 — Connection lifecycle (spec)

**Phase doc:** [`consolidated-issues/phases/phase-06-connection-lifecycle.md`](../../../consolidated-issues/phases/phase-06-connection-lifecycle.md)

**Branch:** `v7-clean-architecture`.

## Goal

Fix the `Connection.DisposeAsync` semaphore-after-release race (H2) and align connection-lifecycle hygiene across the package: `Volatile.Read` discipline, log-level alignment with the carefully-engineered Producer/ConsumerHost siblings, parallel host disposal, and stable connection-event subscribe/unsubscribe pairing (the M10 finding deferred from Phase 5).

> **Note on file-line anchors:** All line numbers below reference the *current* tree (post-Phase-5). Sequential commits will shift them. The plan re-grounds anchors per task; readers should `git grep` symbol names rather than chase line numbers across commits.

---

## Findings in scope

| ID | One-line | Site |
| --- | --- | --- |
| **H2** | Don't dispose `_connectionLock`; mirror Producer's "rely on GC" pattern | [Connection.cs:107-150](../../../src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs#L107-L150) |
| **Smaller — `IsConnected`** | Use `Volatile.Read(ref _connection)?.IsOpen ?? false` | [Connection.cs:60](../../../src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs#L60) |
| **Smaller — log level** | Promote teardown-failure log from Debug → Warning | [Connection.cs:144-147](../../../src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs#L144-L147) |
| **Smaller — Consumer parallel dispose** | `Task.WhenAll(_clients.Select(c => c.DisposeAsync().AsTask()))` | [Consumer.cs:194-208](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L194-L208) |
| **Smaller — M10 (deferred from P5)** | RabbitMqConsumerHost captures `IConnection` reference at subscribe; unsubscribes against the captured reference. Also addresses the "stale `UnderlyingConnection`" finding (same root cause). | [RabbitMqConsumerHost.cs:174-180, :506-512](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L174-L180) |

The phase doc lists "stale `UnderlyingConnection`" as a separate smaller item, but it is the same bug as M10 — the unsubscribe path re-fetches via `_connection.UnderlyingConnection` and skips when null, leaking handlers on the original `IConnection`. Capturing the reference at subscribe time fixes both. Treated as one finding.

## Out of scope (routed elsewhere)

- **H22** (`Producer.PublishWithTimeoutAsync` reconnect under lock) — landed in Phase 4.
- **H23** (`DisposeConnectionAsync` unbounded wait) — landed in Phase 3.
- **R7** (`ProducerConnection.EnsureExchangeDeclaredAsync` race with channel teardown) — flag-only in plan; not load-bearing today (single-threaded behind `_publishLock`), and Phase 4's H22 fix already shipped without breaking it. Note in plan, no fix.

---

## Decision

### Q1 — Scope: include `Consumer.DisposeAsync` parallel-dispose? → A (yes)

The phase doc lists "`Consumer.DisposeAsync` disposes hosts sequentially" as a Smaller item. The fix is mechanical (`foreach + await` → `Task.WhenAll`) and thematically belongs with the lifecycle work. Single phase, single PR. Option B (defer to a future cleanup phase) was rejected — the fix is a one-liner and waiting is overhead.

---

## Fixes — behaviour spec

### H2 — Don't dispose `_connectionLock`

In [`Connection.cs:99-151`](../../../src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs#L99-L151), the existing `DisposeAsync` calls `_connectionLock.WaitAsync(_disposeLockTimeout)` at line 107. If the timeout fires (because a concurrent `ConnectAsync` is holding the lock and `CreateConnectionCoreAsync` is slow or hung), `acquired = false`. Line 121 sets `_disposed = true` regardless. Line 150 currently disposes the lock unconditionally. The slow `ConnectAsync` then enters its `finally` at line 40 and calls `Release()` on a disposed semaphore → `ObjectDisposedException`.

**Fix:** delete the `_connectionLock.Dispose()` call at line 150. Add a comment matching the Producer's wording (the Producer made the same decision deliberately):

```csharp
// _connectionLock is intentionally NOT Disposed:
// SemaphoreSlim.Dispose only releases the lazily-allocated WaitHandle, and
// we never call AvailableWaitHandle, so disposal is a functional no-op. A
// concurrent ConnectAsync's `finally { Release(); }` running on a disposed
// semaphore would throw ObjectDisposedException out of the unwind path,
// which we cannot prevent without holding GC references to every caller.
// The field is GC'd with this Connection instance.
```

After the fix, `Release()` succeeds and `_connectionLock` is GC'd along with the `Connection` instance.

### Smaller — `IsConnected` Volatile.Read alignment

[`Connection.cs:58-61`](../../../src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs#L58-L61):

```csharp
// before
public bool IsConnected()
{
    return _connection?.IsOpen ?? false;
}

// after
public bool IsConnected()
{
    return Volatile.Read(ref _connection)?.IsOpen ?? false;
}
```

The other reads in the class (lines 24, 33, 66, 85, 89) all use `Volatile.Read`. Asymmetry was unintentional.

### Smaller — Log level alignment

[`Connection.cs:144-147`](../../../src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs#L144-L147):

```csharp
// before
catch (Exception ex)
{
    logger.LogDebug(ex, "Error closing connection during async dispose");
}

// after
catch (Exception ex)
{
    logger.LogWarning(ex, "Error closing connection during async dispose");
}
```

`Producer.DisposeAsync` (line 386) and `ProducerConnection.CloseAsync` (line 171) both log at Warning for the same kind of failure.

### Smaller — Consumer parallel dispose

In [`Consumer.cs:194-208`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs#L194-L208):

```csharp
// before
foreach (IAsyncDisposable consumer in _clients)
{
    try
    {
        await consumer.DisposeAsync().ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Error disposing consumer");
    }
}

// after
// Each host's DisposeAsync is independently bounded by its own gracefulShutdownTimeout.
// Sequential disposal made aggregate latency O(N * timeout); parallel makes it
// O(timeout). Per-host failures stay isolated via the inner try/catch — without it,
// Task.WhenAll's aggregate-exception path would short-circuit other hosts' awaits.
var tasks = _clients
    .Select(async consumer =>
    {
        try
        {
            await consumer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing consumer");
        }
    })
    .ToArray();

await Task.WhenAll(tasks).ConfigureAwait(false);
```

The inner try/catch is preserved so a single host's dispose failure doesn't cancel the others' awaits via `Task.WhenAll`'s aggregate-exception path.

### Smaller — Capture `IConnection` for subscribe/unsubscribe pairing (M10 deferred from Phase 5)

The host's connection-event handlers must be unsubscribed against the SAME `IConnection` reference that was used for subscription. Today the unsubscribe re-fetches via `_connection.UnderlyingConnection`, which returns `null` after the parent `Connection.DisposeAsync` has nulled the field — leaking the handlers.

**Step 1.** Add a private field to `RabbitMqConsumerHost`:

```csharp
// Captured at subscribe time so DisposeAsync can unsubscribe against the SAME IConnection
// reference. Re-fetching _connection.UnderlyingConnection at unsubscribe time would return
// null after the parent Connection's DisposeAsync nulls _connection, leaking these handlers
// on the original IConnection until GC reclaims it.
private global::RabbitMQ.Client.IConnection? _subscribedUnderlyingConnection;
```

**Step 2.** Subscribe block in `PrepareAsync` ([RabbitMqConsumerHost.cs:174-180](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L174-L180)):

```csharp
// before
var underlying = _connection.UnderlyingConnection;
if (underlying is not null)
{
    underlying.ConnectionShutdownAsync += OnConnectionShutdownAsync;
    underlying.ConnectionBlockedAsync += OnConnectionBlockedAsync;
    underlying.ConnectionUnblockedAsync += OnConnectionUnblockedAsync;
}

// after
_subscribedUnderlyingConnection = _connection.UnderlyingConnection;
if (_subscribedUnderlyingConnection is not null)
{
    _subscribedUnderlyingConnection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
    _subscribedUnderlyingConnection.ConnectionBlockedAsync += OnConnectionBlockedAsync;
    _subscribedUnderlyingConnection.ConnectionUnblockedAsync += OnConnectionUnblockedAsync;
}
```

**Step 3.** The Phase 5 Task 12 `ConsumerTagChangeAfterRecoveryAsync` subscription (lives in `BeginConsumingAsync`) currently uses `_connection.UnderlyingConnection` to subscribe. Verify the Phase 5 implementation: if it captured a local reference there, switch to use `_subscribedUnderlyingConnection`. If it re-fetched, it has the same bug — fix it to use the shared captured field.

**Step 4.** Unsubscribe block in `DisposeAsync` ([RabbitMqConsumerHost.cs:506-512](../../../src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs#L506-L512)):

```csharp
// before
var underlyingConn = _connection.UnderlyingConnection;
if (underlyingConn is not null)
{
    underlyingConn.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
    underlyingConn.ConnectionBlockedAsync -= OnConnectionBlockedAsync;
    underlyingConn.ConnectionUnblockedAsync -= OnConnectionUnblockedAsync;
}

// after
var subscribedConn = _subscribedUnderlyingConnection;
if (subscribedConn is not null)
{
    subscribedConn.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
    subscribedConn.ConnectionBlockedAsync -= OnConnectionBlockedAsync;
    subscribedConn.ConnectionUnblockedAsync -= OnConnectionUnblockedAsync;
    subscribedConn.ConsumerTagChangeAfterRecoveryAsync -= OnConsumerTagChangeAfterRecoveryAsync;  // if Phase 5 Task 12 subscribed here
    _subscribedUnderlyingConnection = null;
}
```

If the Phase 5 Task 12 unsubscribe lives in a separate block (e.g. via a separate field), align it to use `_subscribedUnderlyingConnection` too.

---

## Tests

Default location: `src/ServiceConnect.UnitTests/RabbitMQ/`. Reuse the canonical Mock<IChannel>/Mock<IConnection> harness pattern from Phase 5.

### H2 — Concurrent ConnectAsync + DisposeAsync race (unit)

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionDisposeAsyncRaceTests.cs` (new).

The test depends on a fake-connection-creator seam — `Connection.cs` doesn't have one today. Add an internal test seam mirroring `ProducerConnection`'s shape:

```csharp
internal Func<ConnectionFactory, string[], string, CancellationToken, Task<IConnection>>? CreateConnectionForTests;
```

`CreateConnectionCoreAsync` consults `CreateConnectionForTests` if set, else falls back to `factory.CreateConnectionAsync`.

The test:

```csharp
[Fact]
public async Task ConcurrentConnectAndDispose_NoObjectDisposedExceptionEscapes()
{
    var transport = BuildTransport();
    var connection = new Connection(transport, "test-queue", NullLogger.Instance);

    // Tighten the dispose-lock timeout via reflection so DisposeAsync gives up
    // waiting for a slow ConnectAsync.
    var timeoutField = typeof(Connection).GetField("_disposeLockTimeout",
        BindingFlags.Instance | BindingFlags.NonPublic);
    timeoutField!.SetValue(connection, TimeSpan.FromMilliseconds(50));

    // Inject a fake connection-creator that hangs.
    var hangGate = new TaskCompletionSource();
    connection.CreateConnectionForTests = async (_, _, _, ct) =>
    {
        await hangGate.Task.WaitAsync(ct).ConfigureAwait(false);
        return Mock.Of<IConnection>(c => c.IsOpen == true);
    };

    var connectExceptions = new ConcurrentBag<Exception>();
    var disposeExceptions = new ConcurrentBag<Exception>();

    // 8 concurrent producers + 8 concurrent disposers.
    var connectTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await connection.CreateChannelAsync(cts.Token);
        }
        catch (ObjectDisposedException ex) when (ex.ObjectName == nameof(Connection))
        {
            // Connection was disposed before this caller's CreateChannelAsync ran;
            // that's a legitimate ObjectDisposedException from the explicit ThrowIf check.
        }
        catch (Exception ex)
        {
            connectExceptions.Add(ex);
        }
    }));

    var disposeTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
    {
        try { await connection.DisposeAsync(); }
        catch (Exception ex) { disposeExceptions.Add(ex); }
    }));

    await Task.WhenAll(connectTasks.Concat(disposeTasks));
    hangGate.TrySetCanceled();

    // The post-fix invariant: no ObjectDisposedException from SemaphoreSlim escapes.
    Assert.Empty(connectExceptions.Where(e => e is ObjectDisposedException ode && ode.ObjectName?.Contains(nameof(SemaphoreSlim), StringComparison.Ordinal) == true));
    Assert.Empty(disposeExceptions);
}
```

**Pre-fix verification:** revert the `_connectionLock.Dispose()` removal, run 5 times, observe at least one `ObjectDisposedException` from `SemaphoreSlim` in `connectExceptions`. Re-apply; test passes 5/5.

### Smaller — `IsConnected` Volatile.Read alignment

No new test. The behaviour is unchanged on observable hardware (any modern x86/ARM has cache coherency strong enough that the missing `Volatile.Read` won't manifest a real race). Contract alignment with the rest of the class. Document in the commit message.

### Smaller — Log level alignment

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionDisposeLogLevelTests.cs` (new).

```csharp
[Fact]
public async Task DisposeAsync_TeardownThrows_LogsAtWarning()
{
    var captured = new List<(LogLevel Level, string Message)>();
    var logger = BuildCapturingLogger(captured);  // canonical IsEnabled + DynamicInvoke pattern

    var transport = BuildTransport();
    var connection = new Connection(transport, "test-queue", logger);

    // Inject an IConnection mock whose CloseAsync throws.
    var mockConn = new Mock<IConnection>();
    mockConn.SetupGet(c => c.IsOpen).Returns(true);
    mockConn.Setup(c => c.CloseAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("test"));
    connection.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(mockConn.Object);

    await connection.CreateChannelAsync(default);
    await connection.DisposeAsync();

    Assert.Contains(captured, l => l.Level == LogLevel.Warning && l.Message.Contains("Error closing connection"));
}
```

Reuses the test seam from H2.

### Smaller — Consumer parallel dispose

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerParallelDisposeTests.cs` (new).

Approach: build a `Consumer` with N (3-5) `IAsyncDisposable` hosts whose `DisposeAsync` each blocks for a known duration. Assert wall-clock latency is `~1×` host duration, not `N×`.

```csharp
[Fact]
public async Task DisposeAsync_DisposesHostsInParallel_NotSequentially()
{
    var hostDelay = TimeSpan.FromMilliseconds(200);
    var hostCount = 5;

    // Build a Consumer with hostCount fake hosts. _clients is private; either expose
    // an internal test seam or use reflection to seed it.
    var consumer = BuildConsumerWithFakeHosts(hostCount, hostDelay);

    var sw = Stopwatch.StartNew();
    await consumer.DisposeAsync();
    sw.Stop();

    // Sequential dispose would be ~hostCount * hostDelay = 1000ms.
    // Parallel dispose should be ~hostDelay + small overhead = ~250ms.
    Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500),
        $"Dispose took {sw.ElapsedMilliseconds}ms; expected < 500ms (parallel). Sequential would be ~{hostCount * hostDelay.TotalMilliseconds}ms.");
}
```

The arrange step needs a way to inject fake `IAsyncDisposable` hosts into `Consumer._clients`. If `_clients` is private and there's no seam, add one — or use reflection. The plan picks the smallest-surface option (likely an `internal` constructor overload that accepts pre-built clients, OR reflection on `_clients` since the bag accepts any `IAsyncDisposable`).

### Smaller — Subscribe/unsubscribe against captured `IConnection`

**File:** `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostConnectionEventCaptureTests.cs` (new).

The contract: subscribe at `PrepareAsync` time captures the `IConnection` reference into `_subscribedUnderlyingConnection`. After the parent `Connection.DisposeAsync` nulls `_connection.UnderlyingConnection`, the host's `DisposeAsync` still unsubscribes against the captured reference.

```csharp
[Fact]
public async Task DisposeAsync_AfterParentConnectionUnderlyingNulled_UnsubscribesAgainstCapturedReference()
{
    int shutdownSubscribers = 0;
    var underlyingConn = new Mock<IConnection>();
    underlyingConn.SetupAdd(c => c.ConnectionShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>())
        .Callback(() => Interlocked.Increment(ref shutdownSubscribers));
    underlyingConn.SetupRemove(c => c.ConnectionShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>())
        .Callback(() => Interlocked.Decrement(ref shutdownSubscribers));

    var serviceConn = new Mock<IServiceConnectConnection>();
    serviceConn.SetupGet(c => c.UnderlyingConnection).Returns(underlyingConn.Object);

    var host = BuildHost(serviceConn.Object);  // canonical harness from Phase 5
    await host.StartConsumingAsync(handler, "q");

    Assert.Equal(1, shutdownSubscribers);

    // Simulate parent Connection's DisposeAsync running first — UnderlyingConnection now null.
    serviceConn.SetupGet(c => c.UnderlyingConnection).Returns((IConnection?)null);

    // Pre-fix: unsubscribe re-fetches null, skips → subscribers stays at 1 (LEAKED).
    // Post-fix: uses _subscribedUnderlyingConnection (captured at subscribe), unsubscribes → reaches 0.
    await host.DisposeAsync();

    Assert.Equal(0, shutdownSubscribers);
}
```

Mirror tests for `ConnectionBlockedAsync`, `ConnectionUnblockedAsync`, and `ConsumerTagChangeAfterRecoveryAsync` (the Phase 5 Task 12 subscription).

### Build / test discipline

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~Connection|FullyQualifiedName~Consumer|FullyQualifiedName~RabbitMqConsumerHost" \
    -m:1
```

E2E budget: zero. All tests are unit-level.

---

## Rollout

### Commit ordering

| # | Commit | Findings | Why this position |
| --- | --- | --- | --- |
| 1 | `docs(spec): phase 06 connection lifecycle` | — | Spec lands first. |
| 2 | `docs(plan): phase 06 implementation plan` | — | Plan after spec. |
| 3 | `fix(connection): use Volatile.Read in IsConnected` | smaller | Trivial alignment. Confirms pipeline. |
| 4 | `fix(connection): log teardown exceptions at Warning` | smaller | Local to `Connection.DisposeAsync`. |
| 5 | `fix(consumer): dispose hosts in parallel` | smaller | Local to `Consumer.DisposeAsync`. |
| 6 | `fix(consumer-host): capture IConnection at subscribe; unsubscribe against captured reference` | smaller (M10) | Lands before H2 so the test seam introduced by H2 doesn't get conflated with this change. |
| 7 | `fix(connection): keep _connectionLock under GC; mirror Producer pattern` | **H2** | Riskiest. Lands alone for clean blame. Includes the test seam (`CreateConnectionForTests`) needed by H2 and the log-level test. |
| 8 | `docs(website): phase 06 release notes` | — | Releases page entry only — no other website surface changes. |
| 9 | `test(connection): final regression gate` | — | Verification commit if any cross-cutting tests were added during review. May be empty. |

Each `fix(...)` commit lands TDD-style: failing test → minimal fix → passing test → commit.

**Ordering note on the test seam:** the H2 test (Task 7) needs `CreateConnectionForTests` on `Connection`. The log-level test (Task 4) ALSO needs the seam to inject a throwing `IConnection` mock. Two options:
- (a) Land the seam in Task 4's commit (the first commit that needs it), reuse in Task 7. Cleaner attribution.
- (b) Add the seam in Task 7's commit, defer the log-level test until after Task 7 ships. Larger Task 7 commit.

Recommended: **(a)** — add the seam in Task 4. Task 7 reuses it. Task 4's commit message mentions the seam in the body.

### Documentation updates

**Website code reference / extension-points / learn:** verified no change required. `Connection` is internal lifecycle plumbing, not on the public surface. The consumer-host's connection-event subscriptions are also internal. No reference page mentions `_connectionLock`, `DisposeAsync` semaphore semantics, or `UnderlyingConnection` nulling.

**Releases page (`releases.mdx`):** add a Phase 6 section sibling to Phase 5's "RabbitMQ consumer-host hardening". Use this content:

```mdx
### Connection-lifecycle hygiene

- **Parallel host disposal.** `Consumer.DisposeAsync` now disposes its hosts in parallel rather than sequentially. Aggregate dispose latency drops from `O(N × gracefulShutdownTimeout)` to `O(gracefulShutdownTimeout)` when `ConsumerCount > 1`.
- **`Connection.DisposeAsync` no longer disposes the internal semaphore (H2).** A concurrent `ConnectAsync` whose acquisition raced past the dispose-time semaphore-release window could observe `ObjectDisposedException` from its own `finally { Release(); }`. The semaphore is now left to GC, mirroring `ProducerConnection`. No public-API change.
- **Connection-event subscriptions captured at subscribe time.** `RabbitMqConsumerHost` now captures the `IConnection` reference when subscribing to `ConnectionShutdownAsync` / `ConnectionBlockedAsync` / `ConnectionUnblockedAsync` / `ConsumerTagChangeAfterRecoveryAsync`, and unsubscribes against the captured reference. Pre-v7.x the unsubscribe re-fetched via the parent `Connection`, which could return `null` if the parent's dispose ran first, leaking handlers on the original `IConnection`.
- **Internal log/`Volatile.Read` alignment.** `Connection.IsConnected` now reads `_connection` via `Volatile.Read`, matching the rest of the class. `Connection.DisposeAsync` now logs teardown failures at `Warning` (was `Debug`), matching `Producer.DisposeAsync` and `RabbitMqConsumerHost.DisposeAsync`.
```

### Examples / READMEs

Verified no change required:

```bash
grep -rn "ConnectAsync\|DisposeAsync\|_connectionLock" examples/ README.md 2>/dev/null
```

No matches in user-facing docs.

### Verification gate before final code review

1. Per-csproj build of `ServiceConnect.Client.RabbitMQ` clean.
2. `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Connection|FullyQualifiedName~Consumer|FullyQualifiedName~RabbitMqConsumerHost" -m:1` — all pass.
3. Astro build clean (release-notes page touched).
4. Grep verification:
   - `grep -n "_connectionLock.Dispose" src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs` — zero hits (H2).
   - `grep -n "_subscribedUnderlyingConnection" src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` — at least 4 hits (declaration, subscribe, unsubscribe, post-unsubscribe nulling).
   - `grep -n "Task.WhenAll" src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` — at least 1 hit (parallel dispose).
   - `grep -n "Volatile.Read" src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs` — at least 5 hits (now including `IsConnected`).
5. Final code review via `superpowers:code-reviewer` (opus) across all phase 06 commits.

### Branch

Stays on `v7-clean-architecture`. Single branch, sequential commits, mirrors Phases 1-5.
