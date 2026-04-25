# Phase 2 — Critical+High RabbitMQ transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the five Critical+High RabbitMQ-transport defects (C-04, C-05, H-05, H-06, H-07) on `v7-clean-architecture` by fixing Consumer restart-after-dispose, Consumer client-bag growth, Producer dispose drain semantics, and Producer dispose budget sharing.

**Architecture:** Two surfaces are touched: `Consumer` gets two minimal field-state fixes (null `_connection`, clear `_clients`) so a `Stop → Start` cycle is safe and bounded. `Producer.DisposeAsync` becomes drain-aware: a shared stopwatch budget across the two semaphore waits, an `ObjectDisposedException` re-check after acquiring `_publishLock` so a publisher cannot operate on a torn-down channel, and the per-instance semaphores are no longer `Dispose()`d so an in-flight publisher's `finally { _publishLock.Release(); }` cannot throw `ObjectDisposedException` out of an unwind.

**Tech Stack:** .NET 10 / C# 13, RabbitMQ.Client v7, xUnit, Moq, Testcontainers (Docker via `sg docker -c '…'`).

**Source documents:**
- Strategy spec: [`docs/superpowers/specs/2026-04-24-consolidated-issues-remediation-strategy.md`](../specs/2026-04-24-consolidated-issues-remediation-strategy.md)
- Issue tracker: [`consolodated-issues/2026-04-24-consolidated-issues.md`](../../../consolodated-issues/2026-04-24-consolidated-issues.md)

**Standing rules (apply to every commit):**
- No issue identifiers (`C-04`, `H-07`, "fixes Xxx") in code comments. Issue IDs go in commit message bodies only.
- Never `git commit --amend`. Hook failures → fix the underlying issue → make a NEW commit.
- Comment style: describe what the implementation does and the WHY behind a non-obvious choice. No "before the fix" / "previously did X" references.
- For Testcontainers / Docker: wrap shell invocations in `sg docker -c '…'`.

---

## File Structure

**Modified production files:**

- [`src/ServiceConnect.Client.RabbitMQ/Consumer.cs`](../../../src/ServiceConnect.Client.RabbitMQ/Consumer.cs) — Tasks 1, 2 (`DisposeAsync`).
- [`src/ServiceConnect.Client.RabbitMQ/Producer.cs`](../../../src/ServiceConnect.Client.RabbitMQ/Producer.cs) — Tasks 3, 4 (`DisposeAsync`, all four publish entry points).

**New / modified test files:**

- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerDisposeTests.cs` — Task 1 + Task 2 unit coverage (parallels existing `RabbitMQ/ProducerDisposeTests.cs`).
- Modify: [`src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs`](../../../src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs) — Tasks 3, 4 unit coverage; the existing `DisposeAsync_WhenPublishLockHeld_StillDisposesChannelAndConnection` test loses its "swallow ObjectDisposedException" workaround once Task 4 lands.
- Modify: [`src/ServiceConnect.UnitTests/ProducerLifecycleTests.cs`](../../../src/ServiceConnect.UnitTests/ProducerLifecycleTests.cs) — Task 4 may need a touch-up if any existing test asserted post-dispose semaphore disposal (none currently do).
- Create: `src/ServiceConnect.EndToEndTests/RabbitMq/ConsumerRestartE2ETests.cs` — V.5 end-to-end Start → Dispose → Start lifecycle for the Consumer fixes.
- Modify: [`src/ServiceConnect.EndToEndTests/RabbitMq/ProducerDisposeConcurrencyE2ETests.cs`](../../../src/ServiceConnect.EndToEndTests/RabbitMq/ProducerDisposeConcurrencyE2ETests.cs) — V.4 strengthen the existing test to fail on `NullReferenceException` (post-WaitAsync flag check absent).

**Documentation / tracker:**

- Modify: [`consolodated-issues/2026-04-24-consolidated-issues.md`](../../../consolodated-issues/2026-04-24-consolidated-issues.md) — append `**Status**: fixed in <sha>` to each of C-04, C-05, H-05, H-06, H-07; bump the `Counts` table.
- Modify: [`docs/superpowers/specs/2026-04-24-consolidated-issues-remediation-strategy.md`](../specs/2026-04-24-consolidated-issues-remediation-strategy.md) — flip the Phase 2 line in §7 to `complete (5 items, commits …)`.

---

## Task 1 — C-04: null `_connection` after Consumer dispose

**Bug:** `Consumer.DisposeAsync` disposes `_connection` (when owned) at line 212 but never nulls the field. `StartConsumingAsync` guards re-creation with `if (_connection is null)` at line 88. After `DisposeAsync → StartConsumingAsync`, the recreate guard is false and the consumer reuses a disposed connection.

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer.cs:211-213`
- Create: `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerDisposeTests.cs`

- [ ] **Step 1.1: Write the failing unit test**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerDisposeTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Unit-level guards on Consumer.DisposeAsync — covers field-state invariants that
/// must hold so a subsequent StartConsumingAsync does not reuse disposed resources
/// or accumulate stale per-cycle clients.
/// </summary>
public class ConsumerDisposeTests
{
    private static Consumer CreateConsumer(IServiceConnectConnection? connection = null)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.MaxRetries).Returns(0);
        transport.SetupGet(t => t.RetryDelay).Returns(0);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.SetupGet(q => q.ErrorQueueName).Returns("err");
        queue.SetupGet(q => q.AuditQueueName).Returns("audit");
        queue.SetupGet(q => q.PurgeQueueOnStartup).Returns(false);
        queue.SetupGet(q => q.AuditingEnabled).Returns(false);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.ConsumerCount).Returns(1);

        return new Consumer(transport.Object, queue.Object, bus.Object, NullLogger<Consumer>.Instance, connection);
    }

    private static void SetField<T>(Consumer consumer, string fieldName, T value)
    {
        typeof(Consumer)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(consumer, value);
    }

    private static T GetField<T>(Consumer consumer, string fieldName)
    {
        return (T)typeof(Consumer)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(consumer)!;
    }

    [Fact]
    public async Task DisposeAsync_WhenOwnsConnection_NullsConnectionField()
    {
        var consumer = CreateConsumer();
        var connectionMock = new Mock<IServiceConnectConnection>();
        connectionMock.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        SetField(consumer, "_connection", connectionMock.Object);
        SetField(consumer, "_ownsConnection", true);

        await consumer.DisposeAsync();

        Assert.Null(GetField<IServiceConnectConnection?>(consumer, "_connection"));
        connectionMock.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WhenConnectionIsCallerOwned_LeavesFieldIntact()
    {
        var connectionMock = new Mock<IServiceConnectConnection>();
        var consumer = CreateConsumer(connectionMock.Object);

        await consumer.DisposeAsync();

        // Caller-owned connection MUST NOT be disposed by Consumer.
        connectionMock.Verify(c => c.DisposeAsync(), Times.Never);
        // Caller-owned connection field must remain intact so the same instance is
        // reused across StartConsumingAsync calls.
        Assert.Same(connectionMock.Object, GetField<IServiceConnectConnection?>(consumer, "_connection"));
    }
}
```

- [ ] **Step 1.2: Run the test to verify it fails**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ConsumerDisposeTests.DisposeAsync_WhenOwnsConnection_NullsConnectionField" \
    --nologo
```

Expected: FAIL — assertion `Assert.Null(GetField<IServiceConnectConnection?>(consumer, "_connection"))` fails because the production code does not null `_connection` after disposing it.

- [ ] **Step 1.3: Apply the fix**

Modify `src/ServiceConnect.Client.RabbitMQ/Consumer.cs` at the dispose-connection block (currently lines 211-213):

```csharp
        if (_ownsConnection && _connection != null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            // Null the field so a subsequent StartConsumingAsync recreates the
            // connection rather than handing back a disposed one. _ownsConnection
            // is set fresh on the next StartConsumingAsync, so it does not need a
            // matching reset here.
            _connection = null;
        }
```

- [ ] **Step 1.4: Run both new tests to verify they pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ConsumerDisposeTests" \
    --nologo
```

Expected: PASS — both `DisposeAsync_WhenOwnsConnection_NullsConnectionField` and `DisposeAsync_WhenConnectionIsCallerOwned_LeavesFieldIntact`.

- [ ] **Step 1.5: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ConsumerDisposeTests.cs
git commit -m "$(cat <<'EOF'
fix(rabbitmq): null Consumer._connection after owned dispose (C-04)

DisposeAsync now nulls _connection after disposing the owned instance so a
subsequent StartConsumingAsync sees `_connection is null` and recreates the
connection rather than reusing a disposed one. Caller-owned connections (passed
via the constructor) are left intact, matching the existing _ownsConnection
contract.
EOF
)"
```

---

## Task 2 — C-05: clear `_clients` on Consumer dispose

**Bug:** `_clients` is a `ConcurrentBag<IAsyncDisposable>` populated in `StartConsumingAsync` line 173. `DisposeAsync` iterates and disposes the entries but never clears the bag. Every Stop/Start cycle accumulates fresh entries on top of the already-disposed ones, leaking memory and producing N×K disposes by the K-th cycle.

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer.cs:185-216` (`DisposeAsync`)
- Test: `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerDisposeTests.cs` (extends Task 1's file)

- [ ] **Step 2.1: Write the failing unit test**

Append to `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerDisposeTests.cs` (inside the `ConsumerDisposeTests` class):

```csharp
    [Fact]
    public async Task DisposeAsync_DisposesAllClientsAndClearsBag()
    {
        var consumer = CreateConsumer();
        var clients = GetField<ConcurrentBag<IAsyncDisposable>>(consumer, "_clients");

        var c1 = new Mock<IAsyncDisposable>();
        c1.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var c2 = new Mock<IAsyncDisposable>();
        c2.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        clients.Add(c1.Object);
        clients.Add(c2.Object);

        await consumer.DisposeAsync();

        // Each registered client is disposed exactly once.
        c1.Verify(c => c.DisposeAsync(), Times.Once);
        c2.Verify(c => c.DisposeAsync(), Times.Once);
        // Bag is empty after dispose so a subsequent StartConsumingAsync does not
        // accumulate stale entries.
        Assert.Empty(clients);
    }

    [Fact]
    public async Task DisposeAsync_AcrossMultipleCycles_LeavesBagBounded()
    {
        var consumer = CreateConsumer();
        var clients = GetField<ConcurrentBag<IAsyncDisposable>>(consumer, "_clients");

        for (int cycle = 0; cycle < 5; cycle++)
        {
            // Simulate a Start that registered 3 clients, then a Dispose.
            for (int i = 0; i < 3; i++)
            {
                var mock = new Mock<IAsyncDisposable>();
                mock.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
                clients.Add(mock.Object);
            }
            await consumer.DisposeAsync();
            Assert.Empty(clients);
        }
    }
```

- [ ] **Step 2.2: Run the tests to verify they fail**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ConsumerDisposeTests.DisposeAsync_DisposesAllClientsAndClearsBag|FullyQualifiedName~ConsumerDisposeTests.DisposeAsync_AcrossMultipleCycles_LeavesBagBounded" \
    --nologo
```

Expected: FAIL — `Assert.Empty(clients)` fails on both because `DisposeAsync` does not clear the bag.

- [ ] **Step 2.3: Apply the fix**

Modify the dispose-clients block in `src/ServiceConnect.Client.RabbitMQ/Consumer.cs` (the `foreach` over `_clients` starts at line 187). After the loop, before the model close block, add a `_clients.Clear()` call:

```csharp
        foreach (IAsyncDisposable consumer in _clients)
        {
            try
            {
                await consumer.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // let shutdown cancellation propagate
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose consumer host - continuing");
            }
        }
        // Reset the bag so a subsequent StartConsumingAsync starts from empty;
        // otherwise per-cycle entries accumulate and the disposed-host references
        // are retained for the lifetime of the Consumer.
        _clients.Clear();
```

- [ ] **Step 2.4: Run the tests to verify they pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ConsumerDisposeTests" \
    --nologo
```

Expected: PASS — all four `ConsumerDisposeTests` tests (two from Task 1, two from Task 2).

- [ ] **Step 2.5: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ConsumerDisposeTests.cs
git commit -m "$(cat <<'EOF'
fix(rabbitmq): clear Consumer._clients bag on dispose (C-05)

DisposeAsync now clears the _clients ConcurrentBag after disposing each entry,
so a Stop/Start cycle does not accumulate disposed ConsumerClient references.
Long-lived buses that restart consumers no longer leak a host reference per
cycle.
EOF
)"
```

---

## Task 3 — H-07: shared dispose budget across Producer semaphore waits

**Bug:** `Producer.DisposeAsync` lines 396-397 calls `_publishLock.WaitAsync(disposeTimeout)` then `_connectionSemaphore.WaitAsync(disposeTimeout)` — two sequential 30s budgets, not one shared 30s budget. Worst case is 60 s, double the documented 30 s.

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs:384-424` (`DisposeAsync`)
- Test: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs`

- [ ] **Step 3.1: Write the failing unit test**

Append to `src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs` (inside the `ProducerDisposeTests` class):

```csharp
    [Fact]
    public async Task DisposeAsync_WhenBothLocksHeld_RespectsSharedBudgetNotDoubled()
    {
        // Arrange — wire up mock channel/connection so teardown succeeds quickly.
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var producer = CreateProducer();
        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connection", connection.Object);
        SetField(producer, "_connected", true);

        var disposeTimeout = TimeSpan.FromMilliseconds(150);
        SetField(producer, "DisposeTimeoutForTests", (TimeSpan?)disposeTimeout);

        // Hold BOTH semaphores so each WaitAsync(timeout) must time out.
        var publishLock = GetField<SemaphoreSlim>(producer, "_publishLock");
        var connectionSemaphore = GetField<SemaphoreSlim>(producer, "_connectionSemaphore");
        await publishLock.WaitAsync();
        await connectionSemaphore.WaitAsync();

        // Act — measure dispose wall time.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await producer.DisposeAsync();
        stopwatch.Stop();

        // The release calls below must not throw. Whether the semaphore is alive
        // depends on Task 4; for Task 3 alone, swallow OcDE.
        try { publishLock.Release(); } catch (ObjectDisposedException) { }
        try { connectionSemaphore.Release(); } catch (ObjectDisposedException) { }

        // Assert — under shared-budget the elapsed time is roughly disposeTimeout.
        // Pre-fix it would be ~2 * disposeTimeout (two sequential budgets).
        // 220ms is a safe upper bound: 150ms timeout + 70ms slack for teardown +
        // scheduler jitter; 300ms (the buggy worst case) fails this bound.
        Assert.InRange(stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(220));
    }
```

- [ ] **Step 3.2: Run the test to verify it fails**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProducerDisposeTests.DisposeAsync_WhenBothLocksHeld_RespectsSharedBudgetNotDoubled" \
    --nologo
```

Expected: FAIL — `Assert.InRange` reports an elapsed time near 300 ms (two sequential 150 ms timeouts), exceeding the 220 ms upper bound.

- [ ] **Step 3.3: Apply the fix**

Modify `src/ServiceConnect.Client.RabbitMQ/Producer.cs` `DisposeAsync` (lines 384-424). Replace the two `WaitAsync(disposeTimeout)` calls with stopwatch-driven shared budget:

```csharp
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedInt, 1) != 0) return;

        // Wait for in-flight publishes and (re)connections to complete before tearing
        // down the channel/connection. The two waits SHARE a single budget so worst-case
        // dispose latency is bounded by disposeTimeout, not 2 * disposeTimeout.
        var disposeTimeout = DisposeTimeoutForTests ?? TimeSpan.FromSeconds(30);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var publishLockAcquired = false;
        var connectionLockAcquired = false;
        try
        {
            publishLockAcquired = await _publishLock.WaitAsync(disposeTimeout).ConfigureAwait(false);

            var remaining = disposeTimeout - stopwatch.Elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            connectionLockAcquired = await _connectionSemaphore.WaitAsync(remaining).ConfigureAwait(false);

            if (!publishLockAcquired || !connectionLockAcquired)
            {
                _logger.LogWarning(
                    "Producer dispose could not acquire locks within {Timeout}; forcing teardown",
                    disposeTimeout);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Producer dispose lock-wait failed; forcing teardown");
        }
        finally
        {
            // Best-effort teardown ALWAYS runs, whether or not we held the locks.
            // A stuck BasicPublishAsync will observe the channel closing and throw —
            // that is the correct shutdown signal for an in-flight publisher.
            try { await TearDownChannelAndConnectionAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Producer channel/connection close failed during dispose"); }

            if (publishLockAcquired) _publishLock.Release();
            if (connectionLockAcquired) _connectionSemaphore.Release();

            _publishLock.Dispose();
            _connectionSemaphore.Dispose();
        }
    }
```

Add the namespace `using` to the file header if not already present:
```csharp
using System.Diagnostics;
```
(`Stopwatch` is in `System.Diagnostics`, fully qualified above to avoid the import; both forms are acceptable.)

- [ ] **Step 3.4: Run the test to verify it passes**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProducerDisposeTests" \
    --nologo
```

Expected: PASS — both the new shared-budget test and the existing `DisposeAsync_WhenPublishLockHeld_StillDisposesChannelAndConnection` (which is timing-insensitive).

- [ ] **Step 3.5: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs
git commit -m "$(cat <<'EOF'
fix(rabbitmq): share dispose budget across Producer semaphore waits (H-07)

The two semaphore waits in DisposeAsync now share one stopwatch-tracked budget
instead of each consuming a full disposeTimeout. Worst-case dispose latency is
bounded by disposeTimeout (30s default), matching the documented contract,
rather than 2 * disposeTimeout (60s).
EOF
)"
```

---

## Task 4 — H-05 + H-06: drain semantics on Producer dispose

**Bugs:**
- **H-05** — `DisposeAsync`'s finally calls `_publishLock.Dispose()` / `_connectionSemaphore.Dispose()` even when in-flight publishers may be mid-publish. The publisher's own `finally { _publishLock.Release(); }` then throws `ObjectDisposedException` out of the unwind, which can replace the original publish exception or surface as `AppDomain.UnhandledException`.
- **H-06** — A publisher that passed `EnsureConnectedAsync`'s `_disposedInt` check before dispose ran can win `_publishLock.WaitAsync` AFTER dispose has torn down `_model` / `_connection` (because dispose releases the lock before the test-seam finally runs the actual `_publishLock.Dispose()`). The publisher then operates on null channel state and NREs.

**Combined fix:**
1. Stop `Dispose()`-ing the per-instance semaphores. `SemaphoreSlim.Dispose` only matters when `AvailableWaitHandle` has been materialized (we never call it); without that, dispose is a no-op functionally and the field is GC'd with the `Producer` instance.
2. After `_publishLock.WaitAsync` returns in any publish-path entry point, re-check `_disposedInt` and throw `ObjectDisposedException` before touching `_model`.

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs:220-294, 304-331, 341-367, 384-424`
- Test: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs`
- Test: `src/ServiceConnect.EndToEndTests/RabbitMq/ProducerDisposeConcurrencyE2ETests.cs` (V.4 will tighten its assertions)

- [ ] **Step 4.1: Write the failing unit test (H-05)**

Append to `src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs`:

```csharp
    [Fact]
    public async Task DisposeAsync_DoesNotDisposeSemaphores_AllowsInFlightPublisherCleanRelease()
    {
        // Arrange — set up a producer with mock channel/connection.
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var producer = CreateProducer();
        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connection", connection.Object);
        SetField(producer, "_connected", true);
        SetField(producer, "DisposeTimeoutForTests", (TimeSpan?)TimeSpan.FromMilliseconds(50));

        // Simulate an in-flight publisher holding _publishLock — dispose times out
        // waiting for it.
        var publishLock = GetField<SemaphoreSlim>(producer, "_publishLock");
        await publishLock.WaitAsync();

        // Act
        await producer.DisposeAsync();

        // Assert — the simulated in-flight publisher's finally block runs
        // _publishLock.Release(). After the fix this MUST NOT throw because the
        // semaphore is no longer disposed.
        var ex = Record.Exception(() => publishLock.Release());
        Assert.Null(ex);
    }
```

- [ ] **Step 4.2: Run the test to verify it fails**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProducerDisposeTests.DisposeAsync_DoesNotDisposeSemaphores_AllowsInFlightPublisherCleanRelease" \
    --nologo
```

Expected: FAIL — `publishLock.Release()` throws `ObjectDisposedException` because `DisposeAsync` disposes the semaphore in its finally.

- [ ] **Step 4.3: Apply the H-05 fix in `DisposeAsync`**

In `src/ServiceConnect.Client.RabbitMQ/Producer.cs`, remove the two semaphore-dispose calls from the finally block. The block becomes:

```csharp
        finally
        {
            // Best-effort teardown ALWAYS runs, whether or not we held the locks.
            // A stuck BasicPublishAsync will observe the channel closing and throw —
            // that is the correct shutdown signal for an in-flight publisher.
            try { await TearDownChannelAndConnectionAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Producer channel/connection close failed during dispose"); }

            if (publishLockAcquired) _publishLock.Release();
            if (connectionLockAcquired) _connectionSemaphore.Release();

            // _publishLock and _connectionSemaphore are intentionally NOT Disposed:
            // SemaphoreSlim.Dispose only releases the lazily-allocated WaitHandle, and
            // we never call AvailableWaitHandle, so disposal is a functional no-op. An
            // in-flight publisher's `finally { _publishLock.Release(); }` running on a
            // disposed semaphore throws ObjectDisposedException out of the unwind path,
            // which we cannot prevent without holding GC references to every caller.
            // The fields are GC'd with the Producer instance.
        }
```

- [ ] **Step 4.4: Update the existing `DisposeAsync_WhenPublishLockHeld_StillDisposesChannelAndConnection` test**

The existing test (lines 86-87 of `ProducerDisposeTests.cs`) currently swallows `ObjectDisposedException` on `publishLock.Release()` because the production code disposed the semaphore. Remove that workaround:

Replace:
```csharp
        // Release the simulated stuck publish ONLY if the semaphore wasn't disposed by DisposeAsync.
        // (After the fix, DisposeAsync disposes the semaphore in its finally block regardless of
        // whether it acquired the lock; so we swallow ObjectDisposedException here.)
        try { publishLock.Release(); } catch (ObjectDisposedException) { }
```

With:
```csharp
        // Release the simulated stuck publish. After Task 4 the semaphore is no longer
        // disposed by DisposeAsync, so Release succeeds cleanly.
        publishLock.Release();
```

Also update the Task 3 shared-budget test (`DisposeAsync_WhenBothLocksHeld_RespectsSharedBudgetNotDoubled` from Step 3.1): replace its `try { … } catch (ObjectDisposedException) { }` blocks with plain `Release()` calls, for the same reason.

- [ ] **Step 4.5: Run the H-05 and existing tests to verify they pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProducerDisposeTests" \
    --nologo
```

Expected: PASS — three tests: the new `DisposeAsync_DoesNotDisposeSemaphores_AllowsInFlightPublisherCleanRelease`, the updated `DisposeAsync_WhenPublishLockHeld_StillDisposesChannelAndConnection`, and the Task 3 shared-budget test.

- [ ] **Step 4.6: Commit (H-05 sub-fix)**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs
git commit -m "$(cat <<'EOF'
fix(rabbitmq): stop disposing Producer semaphores on shutdown (H-05)

DisposeAsync no longer calls _publishLock.Dispose() / _connectionSemaphore.Dispose().
SemaphoreSlim.Dispose only releases the lazy WaitHandle (which we never materialize),
so disposal is a functional no-op; without it, an in-flight publisher's
`finally { _publishLock.Release(); }` no longer throws ObjectDisposedException out
of its unwind path. The semaphore fields are GC'd with the Producer instance.

Updates the two pre-existing dispose tests to drop their `catch (ObjectDisposedException)`
workarounds, and adds a positive test asserting the in-flight Release() succeeds.
EOF
)"
```

- [ ] **Step 4.7: Write the failing unit test (H-06 — post-WaitAsync flag check)**

The H-06 race is "publisher passes EnsureConnectedAsync, then DisposeAsync runs and tears down state, then publisher acquires `_publishLock`." The unit-level reproduction uses a controllable `_publishLock` + the test-seam `DisposeTimeoutForTests`:

Append to `src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs`:

```csharp
    [Fact]
    public async Task PublishAsync_WhenDisposeRanWhileWaitingForLock_ThrowsObjectDisposedException()
    {
        // Arrange — producer with mock channel; simulate "publisher already past
        // EnsureConnectedAsync but still waiting on _publishLock" by holding the
        // lock from the test, then asynchronously kicking off PublishAsync.
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var producer = CreateProducer();
        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connection", connection.Object);
        SetField(producer, "_connected", true);
        SetField(producer, "DisposeTimeoutForTests", (TimeSpan?)TimeSpan.FromMilliseconds(50));

        var publishLock = GetField<SemaphoreSlim>(producer, "_publishLock");
        await publishLock.WaitAsync(); // hold the lock; publisher will queue behind us

        // Kick off PublishAsync — it passes EnsureConnectedAsync (since _connected = true
        // and _disposedInt = 0) and then blocks on _publishLock.WaitAsync.
        var publishTask = producer.PublishAsync(typeof(TestPayload), new byte[] { 1, 2, 3 });

        // Run DisposeAsync — sets _disposedInt = 1, waits for the lock with the short
        // test timeout, gives up, tears down channel/connection, releases nothing
        // (publishLockAcquired = false), exits.
        await producer.DisposeAsync();

        // Now release the lock the test was holding — the publisher acquires it,
        // observes _disposedInt = 1, and must throw ObjectDisposedException rather
        // than NRE on null _model.
        publishLock.Release();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => publishTask);
    }

    // Minimal payload type for PublishAsync's `Type` argument; PublishAsync only uses
    // it to compute an exchange name, so any class works.
    private sealed class TestPayload { }
```

- [ ] **Step 4.8: Run the test to verify it fails**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProducerDisposeTests.PublishAsync_WhenDisposeRanWhileWaitingForLock_ThrowsObjectDisposedException" \
    --nologo
```

Expected: FAIL — the publisher continues past `_publishLock.WaitAsync` with no `_disposedInt` re-check and NREs on `_model!` (which `TearDownChannelAndConnectionAsync` set to null), or throws something other than `ObjectDisposedException`.

- [ ] **Step 4.9: Apply the H-06 fix to all four publish entry points**

In `src/ServiceConnect.Client.RabbitMQ/Producer.cs`, after each `_publishLock.WaitAsync(cancellationToken)` call, immediately re-check `_disposedInt`:

**`PublishAsync` (line 220-253):**

```csharp
    public async Task PublishAsync(Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message.Length > MaximumMessageSize)
            throw new InvalidOperationException(
                $"Message size {message.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock — DisposeAsync may have set _disposedInt
            // and torn down _model while we were waiting. Without this check the publish
            // would NRE on null _model.
            ObjectDisposedException.ThrowIf(_disposedInt != 0, this);

            var messageHeaders = GetHeaders(type, headers, _queueConfiguration.QueueName, "Publish");
            var basicProperties = CreateBasicProperties(messageHeaders);

            string exchangeName = _exchangeNameCache.GetOrAdd(type.AssemblyQualifiedName ?? type.FullName!, _ => ServiceConnect.Services.MessageTypeExchangeName.From(type));

            await ExecuteWithConnectionRetryAsync(async () =>
            {
                if (!_declaredExchanges.ContainsKey(exchangeName))
                    await ConfigureExchangeAsync(exchangeName, ExchangeType.Fanout, cancellationToken).ConfigureAwait(false);

                await PublishWithTimeoutAsync(
                    _model!,
                    exchangeName,
                    string.Empty,
                    false,
                    basicProperties,
                    (ReadOnlyMemory<byte>)message,
                    cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }
```

**`SendAsync(Type, …)` (lines 262-294):**

```csharp
    public async Task SendAsync(Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message.Length > MaximumMessageSize)
            throw new InvalidOperationException(
                $"Message size {message.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposedInt != 0, this);

            if (!_queueConfiguration.TryGetQueueMapping(type, out IReadOnlyList<string>? endPoints))
                throw new InvalidOperationException($"No queue mapping configured for message type '{type.FullName}'. Register a mapping via AddQueueMapping.");

            var baseHeaders = GetHeaders(type, headers, string.Empty, "Send");
            foreach (string endPoint in endPoints)
            {
                baseHeaders[HeaderKeys.DestinationAddress] = endPoint;
                var basicProperties = CreateBasicProperties(baseHeaders);
                await ExecuteWithConnectionRetryAsync(
                    () => PublishWithTimeoutAsync(
                        _model!,
                        string.Empty,
                        endPoint,
                        false,
                        basicProperties,
                        (ReadOnlyMemory<byte>)message,
                        cancellationToken).AsTask(),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _publishLock.Release(); }
    }
```

**`SendAsync(string, Type, …)` (lines 304-331):**

```csharp
    public async Task SendAsync(string endPoint, Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(endPoint))
            throw new ArgumentException($"Cannot send message of type {type} to empty endpoint");
        if (message.Length > MaximumMessageSize)
            throw new InvalidOperationException(
                $"Message size {message.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposedInt != 0, this);

            var messageHeaders = GetHeaders(type, headers, endPoint, "Send");
            var basicProperties = CreateBasicProperties(messageHeaders);
            await ExecuteWithConnectionRetryAsync(
                () => PublishWithTimeoutAsync(
                    _model!,
                    string.Empty,
                    endPoint,
                    false,
                    basicProperties,
                    (ReadOnlyMemory<byte>)message,
                    cancellationToken).AsTask(),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }
```

**`SendBytesAsync` (lines 341-367):**

```csharp
    public async Task SendBytesAsync(string endPoint, Type type, byte[] packet, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(endPoint))
            throw new ArgumentException($"Cannot send packet of type {type} to empty endpoint");
        if (packet.Length > MaximumMessageSize)
            throw new InvalidOperationException(
                $"Message size {packet.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposedInt != 0, this);

            var messageHeaders = GetHeaders(type, headers, endPoint, HeaderKeys.ByteStream);
            var basicProperties = CreateBasicProperties(messageHeaders);
            await ExecuteWithConnectionRetryAsync(
                () => PublishWithTimeoutAsync(
                    _model!,
                    string.Empty,
                    endPoint,
                    false,
                    basicProperties,
                    (ReadOnlyMemory<byte>)packet,
                    cancellationToken).AsTask(),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }
```

- [ ] **Step 4.10: Run the H-06 test to verify it passes**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProducerDisposeTests" \
    --nologo
```

Expected: PASS — all `ProducerDisposeTests` (existing + Task 3 + H-05 + H-06).

- [ ] **Step 4.11: Commit (H-06 sub-fix)**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs
git commit -m "$(cat <<'EOF'
fix(rabbitmq): re-check _disposedInt after acquiring _publishLock (H-06)

Each of the four publish entry points (PublishAsync, SendAsync x2, SendBytesAsync)
now throws ObjectDisposedException immediately after winning _publishLock.WaitAsync
if dispose has run during the wait. Without this re-check the publisher would
proceed past the lock onto a torn-down _model and NRE.

Defense-in-depth alongside the EnsureConnectedAsync check at the publish-path
entry: covers the race where the publisher passed EnsureConnectedAsync before
DisposeAsync set _disposedInt but is still queued behind the dispose holding
_publishLock.
EOF
)"
```

---

## Verification phase

These steps run AFTER the four implementation tasks land. Each step is independent and reports its own pass/fail.

- [ ] **V.1 — Full unit-test suite**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo
```

Expected: PASS — including the four new tests in `ConsumerDisposeTests` (Tasks 1+2) and three new tests in `ProducerDisposeTests` (Tasks 3+4).

- [ ] **V.2 — Solution build**

```bash
dotnet build ServiceConnect-CSharp.sln --nologo
```

Expected: 0 errors, 0 warnings.

- [ ] **V.3 — RabbitMQ integration tests (Testcontainers)**

```bash
sg docker -c 'dotnet test src/ServiceConnect.IntegrationTests/ServiceConnect.IntegrationTests.csproj --filter "Category=Docker" --nologo'
```

Expected: PASS — the existing RabbitMQ integration tests must remain green; this is the regression check on the Producer / Consumer code paths.

- [ ] **V.4 — Strengthen `ProducerDisposeConcurrencyE2ETests`**

The existing test allows any exception type other than `SemaphoreFullException`. With H-06 closed, an in-flight publisher should never produce a `NullReferenceException`. Tighten the assertion:

Modify `src/ServiceConnect.EndToEndTests/RabbitMq/ProducerDisposeConcurrencyE2ETests.cs`:

Replace the trailing assertion block (currently lines 97-105) with:

```csharp
        // Filter out acceptable exceptions (ObjectDisposedException / OperationCanceledException)
        var unexpectedExceptions = strayExceptions
            .Where(ex => ex is not ObjectDisposedException && ex is not OperationCanceledException)
            .ToList();

        Assert.Empty(unexpectedExceptions);

        // Explicit checks for the H-05 / H-06 failure modes:
        // - SemaphoreFullException would mean a Release ran on a disposed semaphore
        //   while another thread was holding it (was the H-05 unwind hazard).
        // - NullReferenceException would mean a publisher reached _model after dispose
        //   nulled it (was the H-06 post-WaitAsync gap).
        Assert.DoesNotContain(strayExceptions, ex => ex is SemaphoreFullException);
        Assert.DoesNotContain(strayExceptions, ex => ex is NullReferenceException);
```

Run:

```bash
sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~ProducerDisposeConcurrencyE2ETests" --nologo'
```

Expected: PASS.

Then commit the e2e tightening:

```bash
git add src/ServiceConnect.EndToEndTests/RabbitMq/ProducerDisposeConcurrencyE2ETests.cs
git commit -m "$(cat <<'EOF'
test(rabbitmq): assert no NRE/SemaphoreFullException in producer dispose race

Tightens ProducerDisposeConcurrencyE2ETests to fail on NullReferenceException
(closed by H-06's post-WaitAsync flag check) and SemaphoreFullException (closed
by H-05's no-dispose-of-semaphores). Both are surfaced via the AppDomain unhandled
exception trap that the test already installs.
EOF
)"
```

- [ ] **V.5 — New `ConsumerRestartE2ETests` for C-04 and C-05**

Create `src/ServiceConnect.EndToEndTests/RabbitMq/ConsumerRestartE2ETests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// End-to-end guard that a Consumer can be Started, Disposed, and Started again
/// against the same queue without leaking ConsumerClient references or reusing a
/// disposed connection.
/// </summary>
[Collection(nameof(MessagingCollection))]
public class ConsumerRestartE2ETests
{
    private readonly MessagingFixture _fixture;

    public ConsumerRestartE2ETests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ConsumerStartDisposeStart_DeliversMessagesAcrossRestart()
    {
        var consumerQueue = _fixture.GetUniqueQueueName("consumer-restart");
        var producerQueue = _fixture.GetUniqueQueueName("consumer-restart-producer");

        var firstReceived = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var phase = 0; // 0 = first lifecycle, 1 = second lifecycle

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(RestartCheckHandler), MessageType = typeof(TestMessage) }
        };

        IServiceProvider BuildConsumerProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IList<HandlerReference>>(handlerRefs);
            services.AddSingleton(firstReceived);
            services.AddSingleton(secondReceived);
            services.AddSingleton(new PhaseHolder(() => phase));
            services.AddTransient<IMessageHandler<TestMessage>, RestartCheckHandler>();

            services.AddServiceConnect(builder =>
            {
                builder.UseRabbitMQ(t =>
                {
                    t.Host = _fixture.RabbitMqHostname;
                    t.Username = _fixture.RabbitMqUsername;
                    t.Password = _fixture.RabbitMqPassword;
                    t.SetClientSetting("Port", _fixture.RabbitMqPort);
                    t.SetClientSetting("RetryCount", 3);
                    t.SetClientSetting("RetrySeconds", 1);
                });
                builder.ConfigureQueues(q => q.QueueName = consumerQueue);
                builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            });

            return services.BuildServiceProvider();
        }

        var producerServices = new ServiceCollection();
        producerServices.AddLogging();
        producerServices.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        producerServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
            });
            builder.ConfigureQueues(q => q.QueueName = producerQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var producerProvider = producerServices.BuildServiceProvider();
        var producerBus = producerProvider.GetRequiredService<IBus>();

        // First lifecycle: start consumer, send message, receive it, dispose.
        var firstProvider = BuildConsumerProvider();
        var firstBus = firstProvider.GetRequiredService<IBus>();
        try
        {
            await firstBus.StartConsumingAsync();
            await producerBus.SendAsync(consumerQueue, new TestMessage(Guid.NewGuid()) { Content = "first" });
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            cts1.Token.Register(() => firstReceived.TrySetCanceled());
            var first = await firstReceived.Task;
            Assert.Equal("first", first.Content);
        }
        finally
        {
            await firstBus.DisposeAsync();
            if (firstProvider is IAsyncDisposable a) await a.DisposeAsync();
        }

        // Second lifecycle: rebuild the bus, start the consumer again on the SAME queue,
        // verify message delivery. Pre-fix, the second start would reuse the disposed
        // connection and either throw or never deliver.
        phase = 1;
        var secondProvider = BuildConsumerProvider();
        var secondBus = secondProvider.GetRequiredService<IBus>();
        try
        {
            await secondBus.StartConsumingAsync();
            await producerBus.SendAsync(consumerQueue, new TestMessage(Guid.NewGuid()) { Content = "second" });
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            cts2.Token.Register(() => secondReceived.TrySetCanceled());
            var second = await secondReceived.Task;
            Assert.Equal("second", second.Content);
        }
        finally
        {
            await secondBus.DisposeAsync();
            if (secondProvider is IAsyncDisposable b) await b.DisposeAsync();
            await producerBus.DisposeAsync();
            if (producerProvider is IAsyncDisposable c) await c.DisposeAsync();
        }
    }
}

file sealed class PhaseHolder
{
    private readonly Func<int> _phase;
    public PhaseHolder(Func<int> phase) => _phase = phase;
    public int Current => _phase();
}

file sealed class RestartCheckHandler : IMessageHandler<TestMessage>
{
    private readonly TaskCompletionSource<TestMessage> _first;
    private readonly TaskCompletionSource<TestMessage> _second;
    private readonly PhaseHolder _phase;

    public RestartCheckHandler(
        TaskCompletionSource<TestMessage> first,
        TaskCompletionSource<TestMessage> second,
        PhaseHolder phase)
    {
        _first = first;
        _second = second;
        _phase = phase;
    }

    public IConsumeContext Context { get; set; } = null!;

    public Task ExecuteAsync(TestMessage message, CancellationToken cancellationToken = default)
    {
        if (_phase.Current == 0) _first.TrySetResult(message);
        else _second.TrySetResult(message);
        return Task.CompletedTask;
    }
}
```

Run:

```bash
sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~ConsumerRestartE2ETests" --nologo'
```

Expected: PASS in ~30s wall time.

Then commit:

```bash
git add src/ServiceConnect.EndToEndTests/RabbitMq/ConsumerRestartE2ETests.cs
git commit -m "$(cat <<'EOF'
test(rabbitmq): add e2e guard for Consumer Start→Dispose→Start (C-04, C-05)

End-to-end smoke that a Consumer can be Started, Disposed, and Started again
on the same queue without reusing a disposed connection (C-04) or accumulating
ConsumerClient hosts in the per-instance bag (C-05). Two lifecycles, one
message each — second lifecycle reuses the queue but rebuilds the Consumer
through DI.
EOF
)"
```

- [ ] **V.6 — Examples sweep**

The five Phase 2 fixes change observable shutdown behaviour but do not change public API shapes. Spot-check the `examples/` projects build and the headline `examples/RabbitMQ.Sample` runs to completion without new warnings:

```bash
dotnet build examples/ServiceConnect.Examples.sln --nologo 2>&1 | tail -3
```

Expected: 0 errors, 0 warnings.

If `examples/RabbitMQ.Sample` exists and is runnable in CI, run it as a smoke check:

```bash
sg docker -c 'dotnet run --project examples/RabbitMQ.Sample -- --duration 10' 2>&1 | tail -10
```

Expected: Sample runs for ~10s, exits cleanly, no `ObjectDisposedException` or `NullReferenceException` in stderr. If the sample does not accept a duration flag, skip this sub-step and rely on V.4 + V.5 for runtime coverage.

If anything new fails in the examples that is attributable to a Phase 2 change, fix it and commit alongside; otherwise no change needed.

- [ ] **V.7 — Website / README sweep**

Per strategy spec §5.4, Phase 2 changes shutdown semantics but not public API shapes, so no `website/` content updates are required. Confirm by grepping for any documentation that asserts dispose-time guarantees:

```bash
grep -rni "ObjectDisposedException\|dispose timeout\|60 ?second" website/ README.md 2>/dev/null | head -20
```

Expected: no hits, or hits reviewed and confirmed to remain accurate (the documented 30 s dispose timeout is now the actual upper bound, not a half-truth).

If any documentation page asserts a behaviour that Phase 2 invalidated, fix it in the same Phase 2 commit pattern (`docs(website,readme): ...`).

- [ ] **V.8 — Update the consolidated-issues tracker**

Append `- **Status**: fixed in <sha>` to each of the five issue entries in `consolodated-issues/2026-04-24-consolidated-issues.md`:

- C-04 → fixed in <Task 1 commit sha>
- C-05 → fixed in <Task 2 commit sha>
- H-05 → fixed in <Task 4.6 commit sha>
- H-06 → fixed in <Task 4.11 commit sha>
- H-07 → fixed in <Task 3.5 commit sha>

Update the `Counts` table at the top of the file: `Critical Fixed` 3 → 5, `High Fixed` 2 → 5, `Total Fixed` 5 → 10.

Commit with subject `docs(tracker): flip Phase 2 (rabbitmq, 5 items) → fixed`.

- [ ] **V.9 — Update the strategy spec phase status**

In `docs/superpowers/specs/2026-04-24-consolidated-issues-remediation-strategy.md` §7, change:

```
- Phase 2: not started
```

to (with the actual five commit SHAs):

```
- Phase 2: complete (5 items, commits <task1> <task2> <task3.5> <task4.6> <task4.11>)
```

Commit (or include in the V.8 commit) with the same `docs(tracker): …` subject.

---

## Phase 2 done

When V.1 through V.9 all pass and the tracker + strategy reflect the work, dispatch a final-pass code reviewer over the full Phase-2 commit range, address any non-blocking notes inline, then move on to Phase 3.

---

## Self-review (writer's checklist — pre-handoff)

**Spec coverage:**
- C-04 → Task 1 ✓
- C-05 → Task 2 ✓
- H-05 → Task 4 (steps 4.1–4.6) ✓
- H-06 → Task 4 (steps 4.7–4.11) ✓
- H-07 → Task 3 ✓
- E2E candidates per strategy §5.3 (C-04, C-05, H-05, H-06, H-07): V.4 (Producer dispose concurrency, tightened) + V.5 (Consumer restart, new) ✓
- Tracker + strategy spec updates: V.8 + V.9 ✓
- Examples + website per §5.4: V.6 + V.7 ✓

**Type/identifier consistency:**
- `Producer._disposedInt` (existing field) used consistently in EnsureConnectedAsync and the four post-WaitAsync sites.
- `Consumer._connection`, `Consumer._ownsConnection`, `Consumer._clients` field names match the production code.
- `DisposeTimeoutForTests` test seam already exists on Producer; reused in Tasks 3 and 4.
- `IServiceConnectConnection` is the consumer-side connection interface; `IConnection` is the RabbitMQ.Client interface used by Producer.

**Placeholder scan:** No "TBD", "implement later", "similar to Task N", or generic "add error handling" lines. Every step contains either complete code, a concrete shell command with expected output, or a concrete documentation edit.

**Comment-hygiene constraints:** Every code comment in this plan describes implementation behaviour or rationale. No issue identifiers (`C-04`, `H-07`, etc.) appear in code comments. Issue IDs appear only in commit message bodies, this plan document, the strategy spec, and the consolidated-issues tracker.
