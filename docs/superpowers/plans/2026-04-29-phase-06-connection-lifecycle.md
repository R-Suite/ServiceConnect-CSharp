# Phase 06 — Connection lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the `Connection.DisposeAsync` semaphore-after-release race (H2) and align connection-lifecycle hygiene across the package: `Volatile.Read` discipline on `IsConnected`, log-level alignment with Producer/ConsumerHost siblings, parallel host disposal in `Consumer.DisposeAsync`, and stable connection-event subscribe/unsubscribe pairing (M10 deferred from Phase 5).

**Architecture:** All fixes live in `ServiceConnect.Client.RabbitMQ.Connection` and the consumer-host's connection-event subscription path. H2 mirrors the `ProducerConnection` pattern of leaving `SemaphoreSlim` to GC. The captured-`IConnection` pattern stores the reference at subscribe time so unsubscribe survives the parent `Connection.DisposeAsync` nulling `UnderlyingConnection`. Consumer parallel disposal turns `O(N × timeout)` aggregate latency into `O(timeout)`.

**Tech Stack:** .NET multi-target net8.0/net10.0, xUnit + Moq, `RabbitMQ.Client` for the `IConnection` event surface. No E2E tests; all unit-level.

**Spec:** [`docs/superpowers/specs/2026-04-29-phase-06-connection-lifecycle.md`](../specs/2026-04-29-phase-06-connection-lifecycle.md).

---

## Build/test safety

This machine has crashed when running unconstrained whole-solution `dotnet build` / `dotnet test` against this codebase (CLAUDE.md has the full incident analysis). The wrapper at `~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup with `CPUQuota=800%`, `MemoryMax=8G`, `MemorySwapMax=0`, `TasksMax=200` and is the safety net. **Even with the wrapper, every command in this plan is per-csproj.** Add `-m:1` to `dotnet test` invocations to serialize MSBuild and stay within `TasksMax`. Never run whole-solution `dotnet build` / `dotnet test` / `dotnet format`.

---

## File structure

### Modified — production code

- `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs` — Volatile.Read on IsConnected (Task 3), log-level alignment (Task 4), test seam for `CreateConnectionForTests` (added in Task 4 for testability), drop `_connectionLock.Dispose()` for H2 (Task 7).
- `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs` — parallel host disposal (Task 5).
- `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` — capture `IConnection` at subscribe; unsubscribe against captured reference (Task 6 — also covers Phase 5 Task 12's recovery subscription which has the same M10 bug).

### Created — tests

- `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionDisposeLogLevelTests.cs` — Task 4.
- `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerParallelDisposeTests.cs` — Task 5.
- `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostConnectionEventCaptureTests.cs` — Task 6.
- `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionDisposeAsyncRaceTests.cs` — Task 7.

### Modified — website

- `website/src/content/docs/releases.mdx` — Phase 6 release-notes entry (Task 8).

### No change — verified

- Other website pages (`learn/operations/`, `reference/extension-points/`, `reference/healthchecks/`) — `Connection` is internal lifecycle plumbing; no public-surface change.
- `examples/` and top-level `README.md` — no references to `ConnectAsync` / `DisposeAsync` / `_connectionLock`.

---

## Task 1: Spec

**Already shipped at commit `ae64e5a5`** (`docs(spec): phase 06 connection lifecycle`). Skip.

---

## Task 2: This plan

**This is the plan commit.** After writing this file, the implementer should:

```bash
git add docs/superpowers/plans/2026-04-29-phase-06-connection-lifecycle.md
git commit -m "docs(plan): phase 06 connection lifecycle implementation plan"
```

---

## Task 3: Smaller — `IsConnected` Volatile.Read alignment

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs:58-61`

This is a contract-alignment fix; the missing `Volatile.Read` won't manifest a race on commodity hardware (modern x86/ARM cache coherency is strong enough). No regression test — documented in the commit message. This task lands first to confirm the per-csproj build/test pipeline works before substantive changes.

- [ ] **Step 1: Read the current `IsConnected` shape**

```bash
sed -n '55,65p' src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs
```

Expected current code:

```csharp
public bool IsConnected()
{
    return _connection?.IsOpen ?? false;
}
```

- [ ] **Step 2: Apply the alignment**

Edit `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs`:

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

- [ ] **Step 3: Build + run existing Connection tests**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Connection" -m:1
```

Expected: build clean, all Connection tests pass (no behaviour change on observable hardware).

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs
git commit -m "fix(connection): use Volatile.Read in IsConnected"
```

---

## Task 4: Smaller — Log level alignment + test seam

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs` — log level + new internal test seam.
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionDisposeLogLevelTests.cs`.

**Background.** `Connection.DisposeAsync` logs teardown failures at Debug. `Producer.DisposeAsync` and `ProducerConnection.CloseAsync` log the same kind of failure at Warning. Align to Warning.

This task also introduces the `CreateConnectionForTests` seam needed by both this test and Task 7's H2 race test. The seam matches the shape used by `ProducerConnection`.

- [ ] **Step 1: Add the test seam to `Connection.cs`**

In `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs`, add a private/internal field near the existing private fields (around line 18):

```csharp
// Test seam: when set, replaces the call to ConnectionFactory.CreateConnectionAsync
// with the supplied factory. Mirrors ProducerConnection.CreateConnectionForTests.
internal Func<ConnectionFactory, string[], string, CancellationToken, Task<IConnection>>? CreateConnectionForTests;
```

Modify `CreateConnectionCoreAsync` (currently around lines 44-49) to consult the seam:

```csharp
// before
private async Task CreateConnectionCoreAsync(CancellationToken cancellationToken)
{
    logger.LogDebug("Creating connection to queue {QueueName}", queueName);
    var connectionFactory = BuildConnectionFactory();
    _connection = await connectionFactory.CreateConnectionAsync(_hosts, queueName, cancellationToken).ConfigureAwait(false);
}

// after
private async Task CreateConnectionCoreAsync(CancellationToken cancellationToken)
{
    logger.LogDebug("Creating connection to queue {QueueName}", queueName);
    var connectionFactory = BuildConnectionFactory();
    var connector = CreateConnectionForTests ?? ((f, h, n, ct) => f.CreateConnectionAsync(h, n, ct));
    _connection = await connector(connectionFactory, _hosts, queueName, cancellationToken).ConfigureAwait(false);
}
```

The `internal` visibility relies on the existing `InternalsVisibleTo("ServiceConnect.UnitTests")` for `ServiceConnect.Client.RabbitMQ` (verified by Task 6 in Phase 5). If for some reason the attribute is missing, surface as a finding — but it should be there.

- [ ] **Step 2: Apply the log-level fix**

In the same file, find the catch block in `DisposeAsync` (around line 144-147):

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

- [ ] **Step 3: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionDisposeLogLevelTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ConnectionDisposeLogLevelTests
{
    private static (Connection connection, List<(LogLevel Level, string Message)> logs) Build(Func<IConnection> connectionFactory)
    {
        var captured = new List<(LogLevel, string)>();
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger.Setup(l => l.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var formatter = (Delegate)invocation.Arguments[4];
                var message = (string)formatter.DynamicInvoke(invocation.Arguments[2], invocation.Arguments[3])!;
                captured.Add((level, message));
            }));

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var connection = new Connection(transport.Object, "test-queue", logger.Object);
        connection.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(connectionFactory());

        return (connection, captured);
    }

    [Fact]
    public async Task DisposeAsync_TeardownThrows_LogsAtWarning()
    {
        var mockConn = new Mock<IConnection>();
        mockConn.SetupGet(c => c.IsOpen).Returns(true);
        mockConn.Setup(c => c.CloseAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated teardown failure"));

        var (connection, logs) = Build(() => mockConn.Object);

        // Establish the connection by creating a channel that will fail at the createChannel step.
        // Workaround: directly trigger CreateChannelAsync to populate _connection. Since the mock
        // doesn't set up CreateChannelAsync, the call will throw — we catch and proceed to Dispose.
        try
        {
            await connection.CreateChannelAsync(default);
        }
        catch
        {
            // Channel creation may fail because the mock doesn't set up CreateChannelAsync;
            // _connection is populated by then.
        }

        await connection.DisposeAsync();

        // Pre-fix: log entry at Debug.
        // Post-fix: log entry at Warning containing "Error closing connection".
        Assert.Contains(logs, l => l.Level == LogLevel.Warning && l.Message.Contains("Error closing connection"));
        Assert.DoesNotContain(logs, l => l.Level == LogLevel.Debug && l.Message.Contains("Error closing connection"));
    }
}
```

- [ ] **Step 4: Run the test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConnectionDisposeLogLevelTests" -m:1
```

Expected: PASS post-fix. (To verify this test fails pre-fix, one can temporarily revert the `LogDebug → LogWarning` change and re-run; the test should then fail with the Warning assertion.)

If the test fails because `_connection` doesn't get populated by the failing `CreateChannelAsync` (depends on mock setup), adapt: use reflection to assign `_connection = mockConn.Object` directly before calling `DisposeAsync`. The exact mechanism is less important than verifying the post-fix log level.

- [ ] **Step 5: Run the broader Connection filter to confirm no regression**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Connection" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/ConnectionDisposeLogLevelTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs
git commit -m "$(cat <<'EOF'
fix(connection): log teardown exceptions at Warning

Aligns with Producer.DisposeAsync and ProducerConnection.CloseAsync, which both
log the same kind of failure at Warning. Also introduces the
CreateConnectionForTests seam (mirrors ProducerConnection's pattern) so the
log-level test and the upcoming H2 race test can inject controllable IConnection
instances without standing up a real broker.
EOF
)"
```

---

## Task 5: Smaller — Consumer parallel dispose

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs:194-208`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerParallelDisposeTests.cs`

**Background.** `Consumer.DisposeAsync` disposes hosts sequentially. With N hosts each bounded by `gracefulShutdownTimeout`, aggregate latency is `O(N × timeout)`. Switch to `Task.WhenAll` for `O(timeout)`.

- [ ] **Step 1: Locate the existing Consumer.DisposeAsync block**

```bash
grep -n "foreach.*_clients\|consumer.DisposeAsync" src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs
```

Read the current shape:

```bash
sed -n '188,212p' src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs
```

Expected: a `foreach (IAsyncDisposable consumer in _clients) { try { await consumer.DisposeAsync(); } catch { _logger.LogWarning(...); } }`.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerParallelDisposeTests.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ConsumerParallelDisposeTests
{
    [Fact]
    public async Task DisposeAsync_DisposesHostsInParallel_NotSequentially()
    {
        const int hostCount = 5;
        var hostDelay = TimeSpan.FromMilliseconds(200);

        // Build a Consumer with no real broker plumbing; we'll seed _clients via reflection.
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("q");

        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.ConsumerCount).Returns(hostCount);

        var connection = Mock.Of<IServiceConnectConnection>();
        var consumer = new Consumer(connection, transport.Object, queueConfig.Object, busConfig.Object, NullLogger<Consumer>.Instance);

        // Build hostCount fake hosts whose DisposeAsync delays hostDelay.
        var fakeHosts = Enumerable.Range(0, hostCount)
            .Select(_ => new DelayingDisposable(hostDelay))
            .ToList();

        // Seed _clients via reflection. Field is internal/private; the field type may be
        // ConcurrentBag<IAsyncDisposable> or similar. Read its type, then add each fake host.
        var clientsField = typeof(Consumer).GetField("_clients",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var clientsValue = clientsField.GetValue(consumer)!;
        // ConcurrentBag<IAsyncDisposable> exposes Add(T) on its instance; invoke via dynamic.
        foreach (var host in fakeHosts)
        {
            ((dynamic)clientsValue).Add((IAsyncDisposable)host);
        }

        var sw = Stopwatch.StartNew();
        await consumer.DisposeAsync();
        sw.Stop();

        // Sequential dispose would be ~hostCount * hostDelay = 1000ms.
        // Parallel dispose should be ~hostDelay + small overhead = ~250ms.
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Dispose took {sw.ElapsedMilliseconds}ms; expected < 500ms (parallel). Sequential would be ~{hostCount * hostDelay.TotalMilliseconds}ms.");

        // All hosts were actually disposed.
        Assert.All(fakeHosts, h => Assert.True(h.WasDisposed));
    }

    [Fact]
    public async Task DisposeAsync_OneHostThrows_OtherHostsStillDisposed()
    {
        const int hostCount = 5;

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());
        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("q");
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.ConsumerCount).Returns(hostCount);

        var connection = Mock.Of<IServiceConnectConnection>();
        var consumer = new Consumer(connection, transport.Object, queueConfig.Object, busConfig.Object, NullLogger<Consumer>.Instance);

        var goodHosts = Enumerable.Range(0, hostCount - 1)
            .Select(_ => new DelayingDisposable(TimeSpan.FromMilliseconds(50)))
            .ToList();
        var badHost = new ThrowingDisposable();

        var clientsField = typeof(Consumer).GetField("_clients",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var clientsValue = clientsField.GetValue(consumer)!;
        foreach (var host in goodHosts)
        {
            ((dynamic)clientsValue).Add((IAsyncDisposable)host);
        }
        ((dynamic)clientsValue).Add((IAsyncDisposable)badHost);

        // The throwing host shouldn't propagate (Consumer's inner try/catch swallows).
        await consumer.DisposeAsync();

        Assert.All(goodHosts, h => Assert.True(h.WasDisposed));
        Assert.True(badHost.DisposeAttempted);
    }

    private sealed class DelayingDisposable : IAsyncDisposable
    {
        private readonly TimeSpan _delay;
        public bool WasDisposed { get; private set; }
        public DelayingDisposable(TimeSpan delay) { _delay = delay; }
        public async ValueTask DisposeAsync()
        {
            await Task.Delay(_delay).ConfigureAwait(false);
            WasDisposed = true;
        }
    }

    private sealed class ThrowingDisposable : IAsyncDisposable
    {
        public bool DisposeAttempted { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposeAttempted = true;
            throw new InvalidOperationException("simulated dispose failure");
        }
    }
}
```

If `_clients` field type is not `ConcurrentBag<IAsyncDisposable>` exactly (e.g., it's `List<RabbitMqConsumerHost>` after Phase 5's M9 split), adapt: read the actual type and construct fakes that satisfy it. The fakes implement `IAsyncDisposable` either way.

**Note on `DisposeAsync_DisposesHostsInParallel_NotSequentially` timing:** the 500ms threshold has comfortable headroom between the parallel target (~250ms) and the sequential failure mode (~1000ms). On a heavily-loaded CI this might still flake; consider relaxing to `< 700ms` if observed.

- [ ] **Step 3: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConsumerParallelDisposeTests" -m:1
```

Expected: `DisposeAsync_DisposesHostsInParallel_NotSequentially` FAILS — sequential disposal takes ~1000ms which exceeds the 500ms threshold. The throwing-host test should already pass (the existing inner try/catch matches the post-fix shape).

- [ ] **Step 4: Apply the parallel-dispose fix**

In `src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs`, replace the existing foreach in `DisposeAsync` (around lines 194-208):

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
// Sequential disposal (the previous shape) made aggregate latency O(N * timeout); parallel
// makes it O(timeout). Per-host failures stay isolated via the inner try/catch — without
// it, Task.WhenAll's aggregate-exception path would short-circuit other hosts' awaits.
var disposeTasks = _clients
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

await Task.WhenAll(disposeTasks).ConfigureAwait(false);
```

- [ ] **Step 5: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Consumer" -m:1
```

Expected: all pass — both new tests + any pre-existing Consumer tests.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/ConsumerParallelDisposeTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs
git commit -m "fix(consumer): dispose hosts in parallel"
```

---

## Task 6: Smaller — Capture `IConnection` for subscribe/unsubscribe pairing

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs` — new field, subscribe/unsubscribe rewrites at all 4 event sites.
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostConnectionEventCaptureTests.cs`.

**Background.** Phase 5 Task 12 added a `ConsumerTagChangeAfterRecoveryAsync` subscription via `_connection.UnderlyingConnection`, mirroring the pattern used by the other 3 event handlers (`ConnectionShutdownAsync` / `ConnectionBlockedAsync` / `ConnectionUnblockedAsync`). All four re-fetch `UnderlyingConnection` at unsubscribe time. After the parent `Connection.DisposeAsync` runs, `UnderlyingConnection` returns `null` and the unsubscribe is skipped — leaking the handlers. Fix: capture once, unsubscribe against the captured reference.

- [ ] **Step 1: Locate all 4 subscribe/unsubscribe sites**

```bash
grep -n "_connection.UnderlyingConnection" src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
```

Expected matches:
- Subscribe block in `PrepareAsync` (around line 194) — subscribes Shutdown/Blocked/Unblocked.
- `SubscribeToConsumerTagRecovery()` (around line 533) — subscribes ConsumerTagChange.
- Unsubscribe block in `DisposeAsync` (around line 634) — unsubscribes all 4.

- [ ] **Step 2: Add the captured-reference field**

In `RabbitMqConsumerHost.cs`, add the field with the other private fields (near the other connection-related fields):

```csharp
// Captured at subscribe time so DisposeAsync can unsubscribe against the SAME IConnection
// reference. Re-fetching _connection.UnderlyingConnection at unsubscribe time would return
// null after the parent Connection's DisposeAsync nulls _connection, leaking these handlers
// on the original IConnection until GC reclaims it.
private global::RabbitMQ.Client.IConnection? _subscribedUnderlyingConnection;
```

- [ ] **Step 3: Update the 3-event subscribe block in `PrepareAsync`**

Find the block currently around line 194:

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

- [ ] **Step 4: Update `SubscribeToConsumerTagRecovery`**

Find the method currently around line 531:

```csharp
// before
private void SubscribeToConsumerTagRecovery()
{
    var underlying = _connection.UnderlyingConnection;
    if (underlying is not null)
    {
        underlying.ConsumerTagChangeAfterRecoveryAsync += OnConsumerTagChangedAfterRecoveryAsync;
    }
}

// after
private void SubscribeToConsumerTagRecovery()
{
    // _subscribedUnderlyingConnection was populated by PrepareAsync; reuse the same captured
    // reference here so the matching unsubscribe in DisposeAsync targets the right instance.
    if (_subscribedUnderlyingConnection is not null)
    {
        _subscribedUnderlyingConnection.ConsumerTagChangeAfterRecoveryAsync += OnConsumerTagChangedAfterRecoveryAsync;
    }
}
```

`SubscribeToConsumerTagRecovery` runs from `BeginConsumingAsync`, which always runs AFTER `PrepareAsync`. The captured field is therefore already populated by the time this method runs.

- [ ] **Step 5: Update the unsubscribe block in `DisposeAsync`**

Find the block currently around line 634:

```csharp
// before
var underlyingConn = _connection.UnderlyingConnection;
if (underlyingConn is not null)
{
    underlyingConn.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
    underlyingConn.ConnectionBlockedAsync -= OnConnectionBlockedAsync;
    underlyingConn.ConnectionUnblockedAsync -= OnConnectionUnblockedAsync;
    underlyingConn.ConsumerTagChangeAfterRecoveryAsync -= OnConsumerTagChangedAfterRecoveryAsync;
}

// after
// Unsubscribe against the SAME IConnection reference we subscribed to. Re-fetching
// _connection.UnderlyingConnection here would return null after the parent Connection's
// DisposeAsync has already nulled the field, leaking these handlers on the original
// IConnection until GC reclaims it.
var subscribedConn = _subscribedUnderlyingConnection;
if (subscribedConn is not null)
{
    subscribedConn.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
    subscribedConn.ConnectionBlockedAsync -= OnConnectionBlockedAsync;
    subscribedConn.ConnectionUnblockedAsync -= OnConnectionUnblockedAsync;
    subscribedConn.ConsumerTagChangeAfterRecoveryAsync -= OnConsumerTagChangedAfterRecoveryAsync;
    _subscribedUnderlyingConnection = null;
}
```

- [ ] **Step 6: Write the failing test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostConnectionEventCaptureTests.cs`. Reuse the harness pattern from Phase 5's `RabbitMqConsumerHostHeaderSizeTests.cs` (Task 6 of Phase 5 added the canonical test seam `RaiseDeliveryForTests` and the `BuildHostAsync` helper):

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class RabbitMqConsumerHostConnectionEventCaptureTests
{
    [Fact]
    public async Task DisposeAsync_AfterParentConnectionUnderlyingNulled_UnsubscribesAgainstCapturedReference()
    {
        // Track event subscribe/unsubscribe counts on a Mock<IConnection>.
        int shutdownSubs = 0;
        int blockedSubs = 0;
        int unblockedSubs = 0;
        int tagChangeSubs = 0;

        var underlyingConn = new Mock<IConnection>();
        underlyingConn.SetupAdd(c => c.ConnectionShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>())
            .Callback(() => Interlocked.Increment(ref shutdownSubs));
        underlyingConn.SetupRemove(c => c.ConnectionShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>())
            .Callback(() => Interlocked.Decrement(ref shutdownSubs));
        underlyingConn.SetupAdd(c => c.ConnectionBlockedAsync += It.IsAny<AsyncEventHandler<ConnectionBlockedEventArgs>>())
            .Callback(() => Interlocked.Increment(ref blockedSubs));
        underlyingConn.SetupRemove(c => c.ConnectionBlockedAsync -= It.IsAny<AsyncEventHandler<ConnectionBlockedEventArgs>>())
            .Callback(() => Interlocked.Decrement(ref blockedSubs));
        underlyingConn.SetupAdd(c => c.ConnectionUnblockedAsync += It.IsAny<AsyncEventHandler<AsyncEventArgs>>())
            .Callback(() => Interlocked.Increment(ref unblockedSubs));
        underlyingConn.SetupRemove(c => c.ConnectionUnblockedAsync -= It.IsAny<AsyncEventHandler<AsyncEventArgs>>())
            .Callback(() => Interlocked.Decrement(ref unblockedSubs));
        underlyingConn.SetupAdd(c => c.ConsumerTagChangeAfterRecoveryAsync += It.IsAny<AsyncEventHandler<ConsumerTagChangedAfterRecoveryEventArgs>>())
            .Callback(() => Interlocked.Increment(ref tagChangeSubs));
        underlyingConn.SetupRemove(c => c.ConsumerTagChangeAfterRecoveryAsync -= It.IsAny<AsyncEventHandler<ConsumerTagChangedAfterRecoveryEventArgs>>())
            .Callback(() => Interlocked.Decrement(ref tagChangeSubs));

        // Build the host with a Mock<IServiceConnectConnection> whose UnderlyingConnection
        // returns the underlyingConn mock during PrepareAsync, then null afterwards (simulates
        // the parent Connection's DisposeAsync running first).
        var serviceConn = new Mock<IServiceConnectConnection>();
        serviceConn.SetupGet(c => c.UnderlyingConnection).Returns(underlyingConn.Object);

        // ... (use the canonical RabbitMqConsumerHost harness from
        // RabbitMqConsumerHostHeaderSizeTests.cs / ...AckNackTests.cs to build a host with
        // mocked consumer + publish channels and start consuming).
        // The harness builds a Mock<IChannel> for both the consumer channel and the publish
        // channel; the consumer mock needs Setup for BasicConsumeAsync, BasicQosAsync, etc.
        // See the Phase 5 test files for the canonical Build helper.
        var host = await BuildHostAsync(serviceConn.Object);

        // Verify all 4 subscriptions happened during StartConsumingAsync.
        Assert.Equal(1, shutdownSubs);
        Assert.Equal(1, blockedSubs);
        Assert.Equal(1, unblockedSubs);
        Assert.Equal(1, tagChangeSubs);

        // Simulate parent Connection's DisposeAsync running first — UnderlyingConnection now null.
        serviceConn.SetupGet(c => c.UnderlyingConnection).Returns((IConnection?)null);

        // Pre-fix: unsubscribe re-fetches null, skips → counters stay at 1 (LEAKED).
        // Post-fix: uses _subscribedUnderlyingConnection (captured at subscribe time) → all reach 0.
        await host.DisposeAsync();

        Assert.Equal(0, shutdownSubs);
        Assert.Equal(0, blockedSubs);
        Assert.Equal(0, unblockedSubs);
        Assert.Equal(0, tagChangeSubs);
    }

    // BuildHostAsync — copy from RabbitMqConsumerHostHeaderSizeTests.cs (Phase 5 Task 6)
    // and adapt the IServiceConnectConnection mock to use the test-supplied serviceConn.
    private static async Task<RabbitMqConsumerHost> BuildHostAsync(IServiceConnectConnection serviceConn)
    {
        // [Reuse the canonical Build helper — it sets up Mock<IChannel> for consumer +
        //  publish channels, wires the IServiceConnectConnection.CreateChannelAsync to
        //  return them, calls host.StartConsumingAsync, returns the host.]
        throw new NotImplementedException("Implementer copies the Build helper from RabbitMqConsumerHostHeaderSizeTests.cs");
    }
}
```

The implementer copies the `BuildHostAsync` helper from one of the Phase 5 test files (`RabbitMqConsumerHostHeaderSizeTests.cs`, `RabbitMqConsumerHostAckNackTests.cs`, etc.) and adapts it to take an `IServiceConnectConnection` parameter rather than building one internally. This is a routine adaptation; the helper is already inlined into multiple Phase 5 test files.

- [ ] **Step 7: Run the test pre-fix**

To verify the test exhibits proper red→green: temporarily revert the captured-field changes in `RabbitMqConsumerHost.cs` (just for the verification run), confirm the test fails because `serviceConn.UnderlyingConnection` returns null at unsubscribe time. Then re-apply the captured-field fix.

OR if reverting feels heavyweight: trust that the post-fix code passes (you wrote it for that contract) and document the pre-fix failure mode in the commit message.

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHostConnectionEventCaptureTests" -m:1
```

Expected post-fix: PASS.

- [ ] **Step 8: Run the broader RabbitMqConsumerHost filter**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~RabbitMqConsumerHost" -m:1
```

Expected: all pass (Phase 5 Task 12's recovery test should still pass — the only change is which field stores the IConnection reference, the subscribe/unsubscribe behaviour is preserved).

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostConnectionEventCaptureTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
git commit -m "fix(consumer-host): capture IConnection at subscribe; unsubscribe against captured reference"
```

---

## Task 7: H2 — Don't dispose `_connectionLock`

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs:150` — remove `_connectionLock.Dispose()`.
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionDisposeAsyncRaceTests.cs`.

**Background.** Today's `DisposeAsync` calls `_connectionLock.WaitAsync(_disposeLockTimeout)` at line 107. If the timeout fires (because a concurrent `ConnectAsync` is holding the lock and `CreateConnectionCoreAsync` is slow or hung), `acquired = false`. Line 121 sets `_disposed = true` regardless. Line 150 currently disposes the lock unconditionally. The slow `ConnectAsync` then enters its `finally` at line 40 and calls `Release()` on a disposed semaphore → `ObjectDisposedException`.

This is the riskiest commit. The fix is one line removed, but the H2 race test stresses concurrency.

- [ ] **Step 1: Write the failing race test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ConnectionDisposeAsyncRaceTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ConnectionDisposeAsyncRaceTests
{
    [Fact]
    public async Task ConcurrentConnectAndDispose_NoSemaphoreObjectDisposedExceptionEscapes()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var connection = new Connection(transport.Object, "test-queue", NullLogger.Instance);

        // Tighten the dispose-lock timeout via reflection so DisposeAsync gives up
        // waiting for a slow ConnectAsync rather than waiting the full 30 seconds.
        var timeoutField = typeof(Connection).GetField("_disposeLockTimeout",
            BindingFlags.Instance | BindingFlags.NonPublic);
        timeoutField!.SetValue(connection, TimeSpan.FromMilliseconds(50));

        // Inject a fake connection-creator that hangs until the test cancels it.
        var hangGate = new TaskCompletionSource();
        connection.CreateConnectionForTests = async (_, _, _, ct) =>
        {
            await hangGate.Task.WaitAsync(ct).ConfigureAwait(false);
            return Mock.Of<IConnection>(c => c.IsOpen == true);
        };

        var connectExceptions = new ConcurrentBag<Exception>();
        var disposeExceptions = new ConcurrentBag<Exception>();

        // 8 concurrent connect attempts + 8 concurrent dispose calls.
        var connectTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await connection.CreateChannelAsync(cts.Token);
            }
            catch (ObjectDisposedException ex) when (ex.ObjectName == nameof(Connection))
            {
                // Connection was disposed before this caller got past the explicit ThrowIf check;
                // legitimate ODE on the Connection itself, NOT on the SemaphoreSlim.
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the expected outcome when the hangGate never resolves.
            }
            catch (Exception ex)
            {
                // Anything else — especially ObjectDisposedException with ObjectName "SemaphoreSlim" —
                // indicates the H2 race fired.
                connectExceptions.Add(ex);
            }
        })).ToArray();

        var disposeTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try { await connection.DisposeAsync(); }
            catch (Exception ex) { disposeExceptions.Add(ex); }
        })).ToArray();

        // Let the dispose tasks run a moment before cancelling the hang gate, so timeouts can fire.
        await Task.Delay(200).ConfigureAwait(false);
        hangGate.TrySetCanceled();

        await Task.WhenAll(connectTasks.Concat(disposeTasks));

        // Post-fix invariant: no ObjectDisposedException from SemaphoreSlim escapes.
        var semaphoreOdes = connectExceptions
            .Where(e => e is ObjectDisposedException ode &&
                        (ode.ObjectName?.Contains("Semaphore", StringComparison.Ordinal) == true ||
                         ode.Message.Contains("Semaphore", StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(semaphoreOdes);
        Assert.Empty(disposeExceptions);
    }
}
```

The test design:
1. Tighten `_disposeLockTimeout` to 50ms via reflection.
2. Inject a connection-creator that hangs on `hangGate.Task.WaitAsync(ct)` until the test cancels it.
3. Spawn 8 concurrent `CreateChannelAsync` calls (which trigger `ConnectAsync`, acquire the lock, await the hang gate).
4. Spawn 8 concurrent `DisposeAsync` calls (which call `WaitAsync(50ms)`, time out, then continue to dispose the lock under the pre-fix code).
5. After 200ms, cancel the hang gate. The connect calls now wake up, exit `CreateConnectionCoreAsync`, hit the `finally { Release(); }` — pre-fix the lock has already been disposed → `ObjectDisposedException` from `SemaphoreSlim`. Post-fix the lock is alive → `Release()` succeeds.

- [ ] **Step 2: Run the test pre-fix to verify it fails**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConnectionDisposeAsyncRaceTests" -m:1
```

Expected: FAIL — at least one `ObjectDisposedException` from `SemaphoreSlim` lands in `connectExceptions`.

If the race doesn't reproduce on this machine reliably (the timing window is small), run 3-5 times and confirm at least one run fails. The test will be more deterministic post-fix.

- [ ] **Step 3: Apply the H2 fix**

In `src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs`, find the `_connectionLock.Dispose()` call at the end of `DisposeAsync` (around line 150) and DELETE it. Add a comment block in its place explaining the rationale (mirrors the Producer's wording at `Producer.cs:393-399`):

```csharp
// before (the line right before the closing `}` of DisposeAsync)
_connectionLock.Dispose();

// after — replace with this comment, no Dispose call
// _connectionLock is intentionally NOT Disposed:
// SemaphoreSlim.Dispose only releases the lazily-allocated WaitHandle, and
// we never call AvailableWaitHandle, so disposal is a functional no-op. A
// concurrent ConnectAsync's `finally { Release(); }` running on a disposed
// semaphore would throw ObjectDisposedException out of the unwind path,
// which we cannot prevent without holding GC references to every caller.
// The field is GC'd with this Connection instance.
```

- [ ] **Step 4: Run the test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConnectionDisposeAsyncRaceTests" -m:1
```

Expected: PASS. Run 5 times to verify determinism:

```bash
for i in 1 2 3 4 5; do
    dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConnectionDisposeAsyncRaceTests" -m:1 || break
done
```

Expected: 5/5 pass.

- [ ] **Step 5: Run the broader Connection filter to confirm no regression**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Connection" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/RabbitMQ/ConnectionDisposeAsyncRaceTests.cs \
        src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs
git commit -m "fix(connection): keep _connectionLock under GC; mirror Producer pattern"
```

---

## Task 8: Documentation — Phase 6 release notes

**Files:**
- Modify: `website/src/content/docs/releases.mdx`

**Background.** The only website surface change is a new release-notes section. All other website pages (`learn/operations/`, `reference/extension-points/`, `reference/healthchecks/`) were verified during planning to require no changes — `Connection` is internal lifecycle plumbing.

- [ ] **Step 1: Locate the existing v7 release-notes structure**

```bash
grep -n "Phase\|RabbitMQ\|consumer-host hardening\|producer.*topology" website/src/content/docs/releases.mdx | head -10
```

Expected: a "RabbitMQ consumer-host hardening" section (Phase 5) and a "RabbitMQ producer and topology hardening" section (Phase 4). The new Phase 6 section should sit as a sibling to these.

- [ ] **Step 2: Add the Phase 6 release-notes section**

Insert this section sibling to "RabbitMQ consumer-host hardening". The exact placement (before or after the consumer-host section) depends on the file's existing ordering — match the existing pattern (newest first or chronological).

```mdx
### Connection-lifecycle hygiene

- **Parallel host disposal.** `Consumer.DisposeAsync` now disposes its hosts in parallel rather than sequentially. Aggregate dispose latency drops from `O(N × gracefulShutdownTimeout)` to `O(gracefulShutdownTimeout)` when `ConsumerCount > 1`.
- **`Connection.DisposeAsync` no longer disposes the internal semaphore.** A concurrent `ConnectAsync` whose acquisition raced past the dispose-time semaphore-release window could observe `ObjectDisposedException` from its own `finally { Release(); }`. The semaphore is now left to GC, mirroring `ProducerConnection`. No public-API change.
- **Connection-event subscriptions captured at subscribe time.** `RabbitMqConsumerHost` now captures the `IConnection` reference when subscribing to `ConnectionShutdownAsync` / `ConnectionBlockedAsync` / `ConnectionUnblockedAsync` / `ConsumerTagChangeAfterRecoveryAsync`, and unsubscribes against the captured reference. Pre-v7.x the unsubscribe re-fetched via the parent `Connection`, which could return `null` if the parent's dispose ran first, leaking handlers on the original `IConnection`.
- **Internal log/`Volatile.Read` alignment.** `Connection.IsConnected` now reads `_connection` via `Volatile.Read`, matching the rest of the class. `Connection.DisposeAsync` now logs teardown failures at `Warning` (was `Debug`), matching `Producer.DisposeAsync` and `RabbitMqConsumerHost.DisposeAsync`.
```

- [ ] **Step 3: Build the website**

```bash
npm --prefix website run build
```

Expected: succeeds. 66 pages, no warnings.

- [ ] **Step 4: Examples / READMEs verification**

```bash
grep -rn "ConnectAsync\|DisposeAsync\|_connectionLock" examples/ README.md 2>/dev/null
```

Expected: no source-file matches (only build artifacts under `bin/`/`obj/`). No edits required.

- [ ] **Step 5: Commit**

```bash
git add website/src/content/docs/releases.mdx
git commit -m "docs(website): phase 06 release notes — connection-lifecycle hygiene"
```

---

## Task 9: Final verification gate

This task is mechanical. It runs all the verification commands the spec calls out before final code review. No code changes — purely a verification commit (or no commit if everything is already green).

- [ ] **Step 1: Per-csproj build clean**

```bash
dotnet build src/ServiceConnect.Client.RabbitMQ/ServiceConnect.Client.RabbitMQ.csproj -m:1
```

Expected: succeeds, 0 errors, 0 warnings.

- [ ] **Step 2: Unit-test pass — Connection / Consumer / RabbitMqConsumerHost filters**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~Connection|FullyQualifiedName~Consumer|FullyQualifiedName~RabbitMqConsumerHost" \
    -m:1
```

Expected: all pass.

- [ ] **Step 3: Astro build clean**

```bash
npm --prefix website run build
```

Expected: succeeds, 66 pages, no warnings.

- [ ] **Step 4: Grep verifications**

```bash
# H2: the Dispose call is gone.
grep -n "_connectionLock\.Dispose" src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs
# Expected: zero hits.

# Captured-reference field present at all 4 expected sites (declaration + 3 use sites).
grep -n "_subscribedUnderlyingConnection" src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
# Expected: at least 4 hits (declaration, subscribe in PrepareAsync, subscribe in SubscribeToConsumerTagRecovery, unsubscribe in DisposeAsync).

# Parallel dispose pattern present.
grep -n "Task\.WhenAll" src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs
# Expected: at least 1 hit (the new disposeTasks await).

# Volatile.Read on IsConnected (now 5+ uses across the class).
grep -n "Volatile\.Read" src/ServiceConnect.Client.RabbitMQ/Connection/Connection.cs
# Expected: at least 5 hits (existing 4 + new IsConnected).
```

If any grep returns unexpected hits (or missing hits), fix the underlying code in a small follow-up commit and re-run.

- [ ] **Step 5: Final code review**

Dispatch `superpowers:code-reviewer` (model: opus) over all Phase 6 commits (from `ae64e5a5` — the spec commit — through HEAD). Use the briefing pattern from previous phases:

```
Review Phase 6 commits (ae64e5a5..HEAD) against
docs/superpowers/specs/2026-04-29-phase-06-connection-lifecycle.md.

Focus on:
- H2: ObjectDisposedException no longer escapes from concurrent ConnectAsync's finally.
  Trace what happens to a Release() call on the live (un-disposed) semaphore after
  DisposeAsync's WaitAsync timeout fires.
- M10 capture-and-unsubscribe: confirm all four IConnection events (Shutdown, Blocked,
  Unblocked, ConsumerTagChange) use the captured _subscribedUnderlyingConnection at
  unsubscribe; no remaining re-fetch via _connection.UnderlyingConnection.
- Consumer parallel dispose: per-host failures stay isolated (inner try/catch).
- IsConnected Volatile.Read alignment + log-level alignment: check pre/post code shape.

Flag any test that locks in pre-fix behaviour, any race condition the spec did not
anticipate, and any missing comment that would help the next reviewer trace
the captured-reference invariant.
```

Capture review findings; address Critical / Important issues in a cleanup task; Minor issues may roll forward.

- [ ] **Step 6: Cleanup commit (only if review surfaced issues)**

If Steps 4 or 5 found anything, fix in a follow-up commit:

```bash
git add <only-cleanup-files>
git commit -m "cleanup(phase-06): address final-review findings"
```

If nothing needed, skip — Phase 6 is done.

---

## Phase 6 done

All 6 findings closed. The phase ships:
- H2 race fix (`_connectionLock` left to GC).
- M10 (deferred from Phase 5) — captured-reference subscribe/unsubscribe pairing.
- 4 smaller hygiene fixes (`Volatile.Read`, log level, parallel dispose, "stale UnderlyingConnection" — same root cause as M10).

Move to writing the closing summary; the user's standard pattern is "phase complete; continue to phase N+1?".
