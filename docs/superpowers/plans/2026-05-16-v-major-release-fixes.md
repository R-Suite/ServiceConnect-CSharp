# v-major Release Fixes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land all verified release-blocker fixes identified by the v-major code review so the major version can ship without known correctness, lifecycle, or API-surface defects.

**Architecture:** Each task is a self-contained fix to a single file (or two tightly related files). Tasks are grouped by area so a subagent can pick up one area without needing full repo context. Each task includes the exact diff, the reason, a verification step, and a commit message. Fixes that cross subsystem boundaries (e.g., interface signature changes) call that out explicitly.

**Tech Stack:** .NET 10, C# 14, RabbitMQ.Client v7, MongoDB driver, xUnit, Moq, FluentAssertions, NSubstitute.

---

## Triage Summary

The original review surfaced ~70 findings. After verifying each Critical and High finding (and most Mediums) against the actual code, the breakdown is:

| Category | Count | Action |
|---|---|---|
| **Confirmed bugs — must fix** | 14 | Tasks 1–14 |
| **API-surface / design issues — fix for v-major** | 7 | Tasks 15–21 |
| **Documentation / observability — fix for v-major** | 4 | Tasks 22–25 |
| **Architecture decisions** | 2 | Tasks 26–27 |
| **Low-impact / defer to v-major+0.1** | 13 | Listed at end; not in tasks |
| **False positives — reject** | ~18 | Listed in Rejection Log at end |

Out of 9 originally-labeled "Critical" findings, only **2** survived verification (Tasks 1 and 2). Out of ~25 "High" findings, **~8** are real bugs. The signal-to-noise ratio of the initial review was about 30% — verification was load-bearing.

---

## Group A: Confirmed Concurrency / Lifecycle Bugs (Critical)

### Task 1: Release aggregator lease when cancellation fires during `RemoveSnapshotAsync`

**Why:** The handler has already committed side effects. The current cancel-path re-throws OCE without releasing the lease; the `finally` only releases on `handlerThrew`. The lease sits orphaned until the 5-minute TTL, blocking subsequent flushes for that aggregator.

**Files:**
- Modify: [src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:378-394](src/ServiceConnect/Services/Processors/AggregatorProcessor.cs#L378-L394)
- Test: [src/ServiceConnect.EndToEndTests/Aggregators/AggregatorTests.cs](src/ServiceConnect.EndToEndTests/Aggregators/AggregatorTests.cs) (add new test)

- [ ] **Step 1: Write a failing E2E test**

Add to `AggregatorTests` (or a new file `AggregatorCancelDuringRemoveTests.cs`):

```csharp
[Fact]
public async Task FlushCancelledMidRemove_ReleasesLeaseImmediately()
{
    // Arrange: aggregator that completes handler successfully, then a CT that fires
    // before RemoveSnapshotAsync completes. Use a fake persistor whose RemoveSnapshotAsync
    // honors the CT and throws OCE.
    var persistor = new CancelOnRemoveAggregatorPersistor();
    // ... arrange descriptor, processor, etc.

    var cts = new CancellationTokenSource();
    persistor.OnRemoveStart = () => cts.Cancel();

    // Act
    await Assert.ThrowsAsync<OperationCanceledException>(
        () => processor.FlushAggregatorAsync(descriptor, persistor, cts.Token));

    // Assert: ReleaseSnapshotAsync was called on the cancel path
    persistor.ReleaseSnapshotCallCount.Should().Be(1);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter FlushCancelledMidRemove_ReleasesLeaseImmediately`
Expected: FAIL — `ReleaseSnapshotCallCount` is 0, not 1.

- [ ] **Step 3: Apply the fix**

In [AggregatorProcessor.cs:382-388](src/ServiceConnect/Services/Processors/AggregatorProcessor.cs#L382), replace:

```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    // Cooperative shutdown mid-cleanup — propagate so the broker leaves the
    // delivery unacked; the lease expires naturally and the next bus instance
    // reclaims. Distinct from transient persistor faults below.
    throw;
}
```

with:

```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    // Handler succeeded; release the lease eagerly so the redelivery's next
    // GetSnapshotAsync can re-claim immediately instead of waiting for TTL.
    // Use CancellationToken.None so the release runs even though dispatch was cancelled.
    try
    {
        await persistor.ReleaseSnapshotAsync(descriptor.AggregatorName, snapshot, CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception releaseEx)
    {
        logger.LogWarning(releaseEx,
            "Aggregator {AggregatorName} lease release on cancel path failed; lease-expiry will reclaim.",
            descriptor.AggregatorName);
    }
    throw;
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test src/ServiceConnect.EndToEndTests --filter Aggregator`
Expected: PASS for the new test and all existing aggregator tests.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/Processors/AggregatorProcessor.cs src/ServiceConnect.EndToEndTests/Aggregators/
git commit -m "fix(aggregator): release lease on RemoveSnapshot cancellation

The handler has already committed its side effects when RemoveSnapshotAsync
runs, so a cancellation mid-remove must release the lease eagerly rather
than letting it sit until the 5-minute TTL. The previous finally only
released on handlerThrew, leaving the orphan window open on the cancel path."
```

---

### Task 2: Move `leaseClaimed = true` before `UpdateManyAsync` in Mongo aggregator

**Why:** `MongoDbTimeoutStore` sets the flag *before* the await with an explicit comment explaining why (a cancelled-but-committed claim must still be releasable). `MongoDbAggregatorPersistor.GetSnapshotAsync` does the opposite — flag set after the await — so OCE between server-commit and client-resume leaves the lease orphaned for 5 minutes.

**Files:**
- Modify: [src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:300-312](src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs#L300-L312)

- [ ] **Step 1: Apply the fix**

Replace:

```csharp
var leaseClaimed = false;
List<AggregatorDocument> docs;
try
{
    if (session is not null)
    {
        await _collection.UpdateManyAsync(session, claimFilter, claimUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    else
    {
        await _collection.UpdateManyAsync(claimFilter, claimUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    leaseClaimed = true;
```

with:

```csharp
// Mark the intent to claim the lease BEFORE the UpdateMany await. If the call
// commits server-side but the awaiter resumes into a cancellation, the catch
// path must still attempt release — the release filter is sessionId-gated, so
// a no-op release of an uncommitted claim is safe. This mirrors the pattern
// in MongoDbTimeoutStore.GetTimeoutsBatchAsync.
var leaseClaimed = true;
List<AggregatorDocument> docs;
try
{
    if (session is not null)
    {
        await _collection.UpdateManyAsync(session, claimFilter, claimUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    else
    {
        await _collection.UpdateManyAsync(claimFilter, claimUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
```

(Delete the post-await `leaseClaimed = true;` line.)

- [ ] **Step 2: Add a regression test**

Add to [src/ServiceConnect.EndToEndTests/Aggregators/](src/ServiceConnect.EndToEndTests/Aggregators/) — a test that cancels the CT after the server commits but before the client resumes. Use Testcontainers Mongo + a delay sandbox; or a unit test that mocks `IMongoCollection` to simulate the precise OCE timing.

```csharp
[Fact]
public async Task GetSnapshotAsync_CancelAfterClaim_ReleasesLease()
{
    var persistor = new MongoDbAggregatorPersistor(...);
    var cts = new CancellationTokenSource();

    // Wire a callback so the test cancels after UpdateMany commits but before resume.
    // Implementation may use a mockable IMongoCollection or a custom await-watcher.

    await Assert.ThrowsAsync<OperationCanceledException>(
        () => persistor.GetSnapshotAsync("agg", cts.Token));

    // Read directly from the collection: no row should still be locked under this sessionId.
    var locked = await persistor.GetLockedRowsForSessionAsync(...);
    locked.Should().BeEmpty();
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test src/ServiceConnect.EndToEndTests --filter MongoDbAggregator`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs src/ServiceConnect.EndToEndTests/Aggregators/
git commit -m "fix(mongo-aggregator): mark lease claimed before UpdateMany await

OCE between server-commit and client-resume otherwise orphans the lease
until TTL. The release filter is sessionId-gated so a no-op release of an
uncommitted claim is safe — same pattern MongoDbTimeoutStore already uses."
```

---

### Task 3: Use `TryGetChannel()` instead of `_model!` in `EnsureExchangeDeclaredAsync`

**Why:** `_model` is `volatile IChannel?` and can be nulled by a concurrent `TearDownChannelAndConnectionAsync` (under the connection semaphore) between the generation snapshot and the declare call. The null-forgiving `!` only suppresses the warning; the NRE is real.

**Files:**
- Modify: [src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs:287-305](src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L287-L305)

- [ ] **Step 1: Verify `TryGetChannel` and `ChannelTransientException` exist**

Run: `grep -n "TryGetChannel\|ChannelTransientException" src/ServiceConnect.Client.RabbitMQ/Producer/*.cs`
Expected: both exist (`Producer.cs:155` uses both).

- [ ] **Step 2: Apply the fix**

Replace [line 299](src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L299):

```csharp
await _model!.ExchangeDeclareAsync(exchangeName, type, true, false, null, false, false, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
// _model can be nulled by a concurrent TearDownChannelAndConnectionAsync between
// the generation snapshot above and this call. Use TryGetChannel so the retriable
// path classifies this as a transient channel state and reconnects, rather than
// throwing NRE through the publish pipeline.
var channel = TryGetChannel()
    ?? throw new ChannelTransientException(
        "Producer channel was torn down concurrently between generation snapshot and exchange declare; retrying.");
await channel.ExchangeDeclareAsync(exchangeName, type, true, false, null, false, false, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 3: Run existing tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter Producer`
Expected: PASS — no regression.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs
git commit -m "fix(producer): use TryGetChannel in EnsureExchangeDeclaredAsync

A concurrent connection reset can null _model between the generation
snapshot and the declare call. The previous _model! suppression masked
the NRE risk. TryGetChannel returns null cleanly so the retriable path
re-issues the publish after the reconnect lands."
```

---

### Task 4: Re-throw `OperationInterruptedException` in `HandleHandlerFailureAsync` / `HandleTerminalFailureDirectAsync`

**Why:** `AlreadyClosedException` is a *subclass* of `OperationInterruptedException`. The catch ladder re-throws `AlreadyClosedException` but lets plain `OperationInterruptedException` fall into the generic `catch` → fallback publish → same exception → swallowed → message acked despite both retry queue AND error exchange having failed. Confirmed data-loss path.

**Files:**
- Modify: [src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs:227-238](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L227-L238) and `:297-308` and `:271-282`

- [ ] **Step 1: Apply the fix in all three catch ladders**

Add a new catch clause `catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException) { throw; }` immediately after the `AlreadyClosedException` catch in:

1. `HandleTerminalFailureDirectAsync` (after [line 233](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L233)):

```csharp
catch (global::RabbitMQ.Client.Exceptions.AlreadyClosedException)
{
    throw;
}
catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException)
{
    // Non-ACE channel interruption (e.g. broker-initiated 404/406) — propagate
    // so the outer dispatch nacks-with-requeue. The generic catch below is for
    // permanent topology faults (e.g. unroutable mandatory publish); a torn
    // channel must not be classified as permanent.
    throw;
}
catch (global::RabbitMQ.Client.Exceptions.BrokerUnreachableException)
{
    throw;
}
```

2. `HandleHandlerFailureAsync` outer catch (after [line 277](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L277)) — same insertion.

3. `HandleHandlerFailureAsync` inner fallback catch (after [line 303](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L303)) — same insertion.

- [ ] **Step 2: Add a regression test**

Add to `src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorTests.cs`:

```csharp
[Fact]
public async Task HandleHandlerFailure_OperationInterruptedException_NacksForRedelivery()
{
    // Arrange: retry handler throws OperationInterruptedException (NOT AlreadyClosed)
    var retryHandler = Substitute.For<IMessageRetryHandler>();
    retryHandler
        .HandleFailureAsync(Arg.Any<IChannel>(), Arg.Any<string>(), Arg.Any<BasicDeliverEventArgs>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<Exception>(), Arg.Any<CancellationToken>())
        .ThrowsAsync(new OperationInterruptedException(new ShutdownEventArgs(...)));
    // ... wire processor

    // Act + Assert: the OCE must propagate; the message must not be acked
    await Assert.ThrowsAsync<OperationInterruptedException>(
        () => processor.ProcessAsync(...));
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter InboundMessageProcessor`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs src/ServiceConnect.UnitTests/RabbitMQ/InboundMessageProcessorTests.cs
git commit -m "fix(consumer): re-throw OperationInterruptedException in failure paths

AlreadyClosedException's base class slipped through the generic catch and
was swallowed by the error-exchange fallback, causing the delivery to be
acked despite both retry and DLQ paths having failed. Classifying it as a
transient channel state matches the existing AlreadyClosedException handling."
```

---

### Task 5: Unsubscribe `OnChannelShutdownAsync` in `StopAsync` before `BasicCancelAsync`

**Why:** A stale consumer tag (post-recovery) can trip a channel exception in `BasicCancelAsync`. The handler then fires with `Initiator = Library` (not `Application`) and flips `_consumerCancelledByBroker = 1`, sending health checks Unhealthy during a graceful stop. `DisposeAsync` already unsubscribes; `StopAsync` doesn't.

**Files:**
- Modify: [src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs](src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs) around line 584 (location of the existing `ConsumerTagChangeAfterRecoveryAsync` unsubscribe)

- [ ] **Step 1: Apply the fix**

In `StopAsync`, after the existing `ConsumerTagChangeAfterRecoveryAsync -= ...` unsubscribe (around line 584) and before `BasicCancelAsync` is invoked, insert:

```csharp
// Match the unsubscribe order in DisposeAsync: a stale consumer tag from a prior
// auto-recovery can trip a Library-initiator channel close during BasicCancelAsync,
// which would otherwise flip _consumerCancelledByBroker and lie to the health check.
if (_model is not null)
{
    _model.ChannelShutdownAsync -= OnChannelShutdownAsync;
}
```

- [ ] **Step 2: Run existing consumer-host tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter RabbitMqConsumerHost`
Expected: PASS — no regression.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/RabbitMqConsumerHost.cs
git commit -m "fix(consumer): unsubscribe ChannelShutdownAsync in StopAsync

A stale consumer tag from post-recovery can trip a Library-initiator
channel close in BasicCancelAsync, which then flips the broker-cancel
flag and lies to the health check during a graceful stop. DisposeAsync
already unsubscribes; mirror that here."
```

---

### Task 6: Add overall timeout to `MessageBusWriteStream.CloseAsync` busy-spin

**Why:** `DisposeAsync` calls `CloseAsync()` with no token. The single-flight gate spins indefinitely if the holder hangs. A stuck holder + a Dispose caller = a hang that survives DI container shutdown.

**Files:**
- Modify: [src/ServiceConnect/Services/MessageBusWriteStream.cs:279](src/ServiceConnect/Services/MessageBusWriteStream.cs#L279) (the `DisposeAsync` body)

- [ ] **Step 1: Apply the fix**

Replace:

```csharp
public async ValueTask DisposeAsync()
{
    await CloseAsync().ConfigureAwait(false);
}
```

with:

```csharp
public async ValueTask DisposeAsync()
{
    // Cap the spin-gate wait so a stuck CloseAsync holder doesn't park the
    // disposing thread indefinitely. The CloseDrainTimeout already governs the
    // drain itself; this matches that budget for the gate.
    using var cts = new CancellationTokenSource(CloseDrainTimeout);
    try
    {
        await CloseAsync(cts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        // Best-effort close; the holder is wedged. Releasing the stream here
        // matches the pattern other transports use when their close budget elapses.
    }
}
```

- [ ] **Step 2: Run stream tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter MessageBusWriteStream`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect/Services/MessageBusWriteStream.cs
git commit -m "fix(streams): cap MessageBusWriteStream.DisposeAsync close-spin

DisposeAsync passed CancellationToken.None into CloseAsync, so a wedged
holder would park the disposing thread indefinitely. Use the existing
CloseDrainTimeout as the spin-gate cap."
```

---

### Task 7: Throw on missing packets in `MessageBusReadStream.Read()` / `ReadSequence()`

**Why:** `Read()` iterates `0..lastSnapshot` with `TryGetValue` and silently skips missing keys. `IsComplete` uses a count-based check that may not catch all gaps (`_receivedCount` is incremented on every successful `TryAdd`, but `LastPacketNumber` being lower than the actual highest received can still leave gaps inside the iteration range). The current behavior silently produces truncated output — data loss with no observable error.

**Files:**
- Modify: [src/ServiceConnect/Services/MessageBusReadStream.cs:143-171](src/ServiceConnect/Services/MessageBusReadStream.cs#L143-L171)

- [ ] **Step 1: Apply the fix in `Read()`**

Replace the `if (_packets.TryGetValue(...))` block with:

```csharp
for (long i = 0; i <= lastSnapshot; i++)
{
    if (!_packets.TryGetValue(i, out var packet))
    {
        throw new InvalidOperationException(
            $"Stream {SequenceId} is missing packet {i}; cannot assemble. " +
            $"This indicates packet loss or out-of-order completion signalling.");
    }
    ms.Write(packet, 0, packet.Length);
}
```

- [ ] **Step 2: Apply the same fix to `ReadSequence()`**

In the equivalent loop in `ReadSequence()`, replace any `continue` on a missing key with the same throw.

- [ ] **Step 3: Add a regression test**

```csharp
[Fact]
public void Read_WithMissingPacket_Throws()
{
    var stream = new MessageBusReadStream(/*...*/);
    stream.Write(packet0, packetNumber: 0);
    // Skip packet 1 — write packet 2
    stream.Write(packet2, packetNumber: 2);
    stream.SetLastPacketNumber(2);

    var ex = Assert.Throws<InvalidOperationException>(() => stream.Read());
    ex.Message.Should().Contain("missing packet 1");
}
```

- [ ] **Step 4: Run tests + commit**

Run: `dotnet test src/ServiceConnect.UnitTests --filter MessageBusReadStream`
Expected: PASS.

```bash
git add src/ServiceConnect/Services/MessageBusReadStream.cs src/ServiceConnect.UnitTests/
git commit -m "fix(streams): throw on missing packets in MessageBusReadStream

Read and ReadSequence previously silently produced truncated output when
packet numbers had gaps. Throwing surfaces the data-loss condition rather
than handing the caller a quietly-corrupted payload."
```

---

### Task 8: Make `SlidingDetails` thread-safe via `Volatile`/`long` ticks

**Why:** `ExpireAt` is a `DateTimeOffset` (16-byte struct) accessed without locks from `Slide()` and `CanExpire()` concurrently. Torn reads on weak memory models produce wrong expiry decisions. Low impact in practice (sliding cache, eventually-correct), but inconsistent with the rest of the codebase's discipline.

**Files:**
- Modify: [src/ServiceConnect.Persistence.InMemory/Cache/SlidingDetails.cs](src/ServiceConnect.Persistence.InMemory/Cache/SlidingDetails.cs)

- [ ] **Step 1: Apply the fix**

Replace the property:

```csharp
private DateTimeOffset ExpireAt { get; set; }
```

with a `long` ticks field and use `Volatile.Read`/`Volatile.Write`:

```csharp
private long _expireAtUtcTicks;

public bool CanExpire(out TimeSpan tryAfter)
{
    var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
    var expireTicks = Volatile.Read(ref _expireAtUtcTicks);
    tryAfter = TimeSpan.FromTicks(expireTicks - nowTicks);
    return tryAfter.Ticks <= 0;
}

public void Slide()
{
    var newTicks = _timeProvider.GetUtcNow().Add(RelativeExpiry).UtcTicks;
    Volatile.Write(ref _expireAtUtcTicks, newTicks);
}
```

Update the constructor / `RelativeExpiry` accordingly.

- [ ] **Step 2: Run cache tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter Cache`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/Cache/SlidingDetails.cs
git commit -m "fix(in-memory-cache): make SlidingDetails.ExpireAt access atomic

DateTimeOffset is 16 bytes; concurrent read/write tears on weak memory
models, producing wrong expiry decisions. Switch to long ticks with
Volatile.Read/Volatile.Write for atomic 64-bit access."
```

---

### Task 9: Fix `BuildTypedList` → `IReadOnlyList<TMsg>` implicit cast contract

**Why:** `AggregatorDescriptor.BuildTypedList` is declared returning `IList`. The compiled invoker downstream casts it to `IReadOnlyList<TMsg>`. Today the compiled `Expression.New(typeof(List<TMsg>))` produces a `List<T>` which satisfies both, so it works. But the contract is invisible — any test/mock/refactor that returns an `IList` not also implementing `IReadOnlyList<T>` would silently `InvalidCastException` at dispatch.

**Files:**
- Modify: [src/ServiceConnect/Services/Processors/AggregatorDescriptor.cs](src/ServiceConnect/Services/Processors/AggregatorDescriptor.cs) — change the field type
- Modify: [src/ServiceConnect/Services/Processors/AggregatorRegistry.cs:144-191](src/ServiceConnect/Services/Processors/AggregatorRegistry.cs#L144-L191)

- [ ] **Step 1: Change the descriptor field type**

Change `Func<IList<object>, IList>` to `Func<IList<object>, object>` (or to `Func<IList<object>, IReadOnlyList<object>>` if MSIL emit can produce that — verify by compiling). The latter is preferable.

- [ ] **Step 2: Update `CompileBuildTypedList` to emit the matching return type**

Change the compiled lambda to return `IReadOnlyList<TMsg>` so the type system carries the contract:

```csharp
// In CompileBuildTypedList<TMsg>:
var addMethod = typeof(List<TMsg>).GetMethod(nameof(List<TMsg>.Add), [typeof(TMsg)])!;
// ... existing body ...
return Expression.Lambda<Func<IList<object>, IReadOnlyList<TMsg>>>(body, sourceParameter).Compile();
```

Update `CompileInvokeExecuteAsync` to read the list via `IReadOnlyList<TMsg>` directly without a cast.

- [ ] **Step 3: Run aggregator tests**

Run: `dotnet test src/ServiceConnect.UnitTests --filter Aggregator`
Expected: PASS — no regression.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect/Services/Processors/AggregatorDescriptor.cs src/ServiceConnect/Services/Processors/AggregatorRegistry.cs
git commit -m "fix(aggregator): tighten BuildTypedList contract to IReadOnlyList

The compiled invoker cast IList to IReadOnlyList<TMsg>. The cast worked
only because the implementation returns List<TMsg>; any future mock or
refactor that returns a non-IReadOnlyList<T> IList would InvalidCastException
at dispatch with no startup signal."
```

---

### Task 10: Audit publish fallback should carry the original handler exception, not the retry-publish exception

**Why:** When a handler throws, the retry-publish path can fail (e.g. `PublishException` on `mandatory:true` unroutable). The fallback to the error exchange currently passes the *retry* exception into the DLQ headers, so operators see "publish failed" rather than "NullReferenceException in OrderHandler line 42".

**Files:**
- Modify: [src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs:288-296](src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs#L288-L296)

- [ ] **Step 1: Apply the fix**

In the fallback inside `HandleHandlerFailureAsync`, change:

```csharp
await _retryHandler.HandleTerminalFailureAsync(
    publishChannel,
    args,
    headers,
    retryEx,     // <-- this is the retry-publish exception, not the handler exception
    shutdownToken).ConfigureAwait(false);
```

to:

```csharp
await _retryHandler.HandleTerminalFailureAsync(
    publishChannel,
    args,
    headers,
    // Use the original handler exception so the DLQ entry's Exception header
    // identifies the actual handler failure. retryEx is logged above so
    // operators can still see why the retry path failed.
    handlerException ?? retryEx,
    shutdownToken).ConfigureAwait(false);
```

- [ ] **Step 2: Update / add tests**

Update tests that assert the DLQ header content to expect the handler exception.

- [ ] **Step 3: Run tests + commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer/InboundMessageProcessor.cs src/ServiceConnect.UnitTests/
git commit -m "fix(consumer): DLQ entry carries handler exception, not retry-publish exception

The fallback to the error exchange previously stamped the retry-publish
exception into the DLQ headers, hiding the actual handler failure. Carry
the original handler exception; the retry-publish exception is already
logged for operator visibility."
```

---

### Task 11: Aggregator name derivation should not embed assembly-qualified generic noise

**Why:** `AggregatorRegistry` uses `aggregatorBaseType.FullName` which on a closed generic returns the assembly-qualified-name embedded form (`Aggregator\`1[[MyApp.OrderCreated, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]`). The version field rotates on every assembly version bump, so MongoDB-persisted aggregator state is silently orphaned at deploy time.

**Files:**
- Modify: [src/ServiceConnect/Services/Processors/AggregatorRegistry.cs:136](src/ServiceConnect/Services/Processors/AggregatorRegistry.cs#L136)

- [ ] **Step 1: Derive a stable aggregator name**

Replace:

```csharp
AggregatorName: aggregatorBaseType.FullName!,
```

with a name derived from the concrete handler type and the message type, without assembly-version noise:

```csharp
// Concrete handler type + message type — no assembly version, no qualified-name embedding.
// Stable across deploys; deterministic when the same handler is registered in two assemblies
// (forbidden by upstream validation, but the name itself is also unique).
AggregatorName: $"{href.HandlerType.FullName}+{typeof(TMessage).FullName}",
```

(Verify `href.HandlerType` is the concrete type, not the base `Aggregator<T>`. Adjust if needed.)

- [ ] **Step 2: Add a migration note**

Add a note to the v-major release notes: existing aggregator state in MongoDB will not be found by the new name. Document the migration path (manual rename of `Name` field in the `ServiceConnect.Aggregators` collection).

- [ ] **Step 3: Run aggregator tests + commit**

```bash
git add src/ServiceConnect/Services/Processors/AggregatorRegistry.cs
git commit -m "fix(aggregator): use stable name derivation (no assembly version)

aggregatorBaseType.FullName on a closed generic returns the AQN-embedded
form, whose Version=X.Y.Z component rotates every deploy. Deployments
were silently orphaning persisted aggregator state. Derive the name
from the concrete handler type + message type, both of which are
version-independent FullNames.

BREAKING: existing aggregator persistor state will not be found by the
new name. Operators must rename the Name field in the
ServiceConnect.Aggregators collection during upgrade."
```

---

### Task 12: `HandlerProcessor` should rethrow OCE before wrapping other handler exceptions in `AggregateException`

**Why:** If handler A throws a non-OCE exception and handler B then throws OCE (both before the loop's CT check), the OCE gets caught by the generic catch, added to `handlerExceptions`, and wrapped in `AggregateException`. The dispatcher upstream sees `AggregateException` instead of OCE and applies retry semantics instead of shutdown semantics.

**Files:**
- Modify: [src/ServiceConnect/Services/Processors/HandlerProcessor.cs:79-105](src/ServiceConnect/Services/Processors/HandlerProcessor.cs#L79-L105)

- [ ] **Step 1: Apply the fix**

After the per-handler loop, before throwing the `AggregateException`, check whether any inner exception is an OCE and the token was cancelled; if so, rethrow the OCE directly:

```csharp
if (handlerExceptions.Count > 0)
{
    if (cancellationToken.IsCancellationRequested)
    {
        var oce = handlerExceptions.OfType<OperationCanceledException>().FirstOrDefault();
        if (oce is not null)
        {
            // Cancellation outranks other handler failures — surface as OCE so
            // upstream dispatch applies shutdown semantics, not retry semantics.
            throw oce;
        }
    }
    throw new AggregateException("One or more message handlers threw.", handlerExceptions);
}
```

- [ ] **Step 2: Add a regression test + commit**

Test: two handlers, first throws `InvalidOperationException`, second throws `OperationCanceledException`, CT is cancelled — expect OCE, not `AggregateException`.

```bash
git add src/ServiceConnect/Services/Processors/HandlerProcessor.cs src/ServiceConnect.UnitTests/
git commit -m "fix(handler): rethrow OCE rather than wrapping in AggregateException

When the CT is cancelled and any handler also threw OCE, surface the
OCE directly so upstream dispatch applies shutdown semantics. Previously
the AggregateException wrapper hid the cancellation signal."
```

---

### Task 13: `ProcessManagerTimeoutService` catch-up loop should not gate on remove success

**Why:** `dispatchedCount` is incremented only after a successful `RemoveDispatchedTimeoutAsync`. The catch-up loop continues only while `dispatched > 0`. If the persistor is transiently failing removes, sends still fire but the catch-up loop exits after one iteration, dramatically extending drain time under store degradation.

**Files:**
- Modify: [src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:143-213](src/ServiceConnect/Services/ProcessManagerTimeoutService.cs#L143-L213)

- [ ] **Step 1: Apply the fix**

Introduce a separate `sentCount` that increments on send-success regardless of remove-success. Use `sentCount` for the catch-up loop decision; keep `dispatchedCount` for the observability metric.

```csharp
int sentCount = 0;
int removedCount = 0;
foreach (var due in batch)
{
    try
    {
        await SendAsync(due, ct).ConfigureAwait(false);
        sentCount++;
        try
        {
            await store.RemoveDispatchedTimeoutAsync(due.Id, ct).ConfigureAwait(false);
            removedCount++;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Timeout remove failed; will redeliver on next poll until the row's TTL or until the next remove succeeds.");
        }
    }
    catch (Exception sendEx) { /* existing handling */ }
}
// catch-up loop continues while sentCount == batchSize (full batch this iteration)
```

- [ ] **Step 2: Run tests + commit**

```bash
git add src/ServiceConnect/Services/ProcessManagerTimeoutService.cs
git commit -m "fix(timeouts): catch-up loop driven by sent count, not remove count

Transient remove failures previously starved the catch-up loop, so a
single batch was processed per tick during store degradation. Decouple
the loop signal from the remove path so backlog drains at full rate."
```

---

### Task 14: `_messageIdCached` / `_correlationIdCached` should be `volatile` on `PooledConsumeContext`

**Why:** Non-pooled `ConsumeContext` declares these as `volatile bool` with explicit comments. Pooled variant uses plain `bool`. On weakly-ordered processors, a reader can see `_messageIdCached == true` but read a stale `_messageId`. Idempotent on x86, real on ARM.

**Files:**
- Modify: [src/ServiceConnect/Services/ConsumeContextPool.cs:133-149](src/ServiceConnect/Services/ConsumeContextPool.cs#L133-L149)

- [ ] **Step 1: Apply the fix**

Add `volatile` to both fields, matching the non-pooled `ConsumeContext` pattern. Confirm write-payload-before-flag ordering in the setters.

- [ ] **Step 2: Run tests + commit**

```bash
git add src/ServiceConnect/Services/ConsumeContextPool.cs
git commit -m "fix(pool): mark _messageIdCached / _correlationIdCached volatile

Match the non-pooled ConsumeContext pattern; without volatile, ARM
readers can see the flag set but the payload not yet visible."
```

---

## Group B: API Surface (must fix for v-major)

### Task 15: Freeze sub-configurations after `AddServiceConnect`

**Why:** `BusConfiguration.Freeze()` only latches top-level fields. `TransportConfiguration`, `QueueConfiguration`, `PersistenceConfiguration`, `PipelineConfiguration` all remain mutable. The class xmldoc explicitly admits "not yet" frozen. For v-major this must be done.

**Files:**
- Modify: [src/ServiceConnect/Configuration/BusConfiguration.cs](src/ServiceConnect/Configuration/BusConfiguration.cs) and each sub-config

- [ ] **Step 1: Add a shared `FreezableConfiguration` base or `Freeze()` method**

In each sub-config (Transport, Queue, Persistence, Pipeline), add:

```csharp
private bool _frozen;
internal void Freeze() => _frozen = true;
private void ThrowIfFrozen()
{
    if (_frozen)
        throw new InvalidOperationException(
            $"{GetType().Name} cannot be mutated after the bus has been built.");
}
```

Add `ThrowIfFrozen()` at the start of every setter.

- [ ] **Step 2: Call sub-freeze from `BusConfiguration.Freeze()`**

```csharp
public void Freeze()
{
    _frozen = true;
    ((TransportConfiguration)Transport).Freeze();
    ((QueueConfiguration)Queues).Freeze();
    ((PersistenceConfiguration)Persistence).Freeze();
    ((PipelineConfiguration)Pipeline).Freeze();
}
```

- [ ] **Step 3: Update the xmldoc comment to remove the "not yet" admission**

- [ ] **Step 4: Add tests + commit**

Test: mutate each sub-config property after `AddServiceConnect`, expect `InvalidOperationException`.

```bash
git add src/ServiceConnect/Configuration/
git commit -m "feat(config): freeze sub-configurations after bus build

Previously only BusConfiguration's top-level was frozen; sub-configurations
(Transport, Queues, Persistence, Pipeline) remained mutable, allowing
post-build mutation that the DI-registered singletons would silently
honor. Lock the entire surface for v-major."
```

---

### Task 16: Add `QueueName` validation in `ServiceConnectBuilder.ConfigureQueues`

**Why:** `QueueName` defaults to `""` and is not validated until broker-connect time, where it fails with an opaque AMQP error.

**Files:**
- Modify: [src/ServiceConnect/ServiceConnectBuilder.cs](src/ServiceConnect/ServiceConnectBuilder.cs) — `ConfigureQueues`

- [ ] **Step 1: Add validation**

```csharp
public ServiceConnectBuilder ConfigureQueues(Action<IQueueConfiguration> configure)
{
    configure(BusConfig.Queues);
    if (string.IsNullOrWhiteSpace(BusConfig.Queues.QueueName))
    {
        throw new InvalidOperationException(
            "QueueConfiguration.QueueName must be a non-empty, non-whitespace string.");
    }
    return this;
}
```

Also add a second pass inside `AddServiceConnect` (after the user has applied all options) so applications that forget `ConfigureQueues` also fail fast.

- [ ] **Step 2: Add tests + commit**

```bash
git add src/ServiceConnect/ServiceConnectBuilder.cs src/ServiceConnect/DependencyInjection/
git commit -m "feat(config): validate QueueName at startup, not at broker connect

Empty/whitespace QueueName previously failed with an opaque AMQP error
at broker-connect time. Fail fast at AddServiceConnect time with an
actionable InvalidOperationException."
```

---

### Task 17: Mark `SslConfigurationBuilder` as `internal`

**Why:** It's `public static`, but it returns `RabbitMQ.Client.SslOption` — leaking the transport-specific type into the public API surface. Locks the package to that specific RabbitMQ.Client class shape.

**Files:**
- Modify: [src/ServiceConnect.Client.RabbitMQ/Connection/SslConfigurationBuilder.cs:9](src/ServiceConnect.Client.RabbitMQ/Connection/SslConfigurationBuilder.cs#L9)

- [ ] **Step 1: Change `public static class` to `internal static class`**

- [ ] **Step 2: Verify no public callers**

Run: `grep -rn "SslConfigurationBuilder" src/ examples/`
Expected: no external usage outside the RabbitMQ adapter project.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Connection/SslConfigurationBuilder.cs
git commit -m "refactor(ssl): make SslConfigurationBuilder internal

The class returned RabbitMQ.Client.SslOption — exposing the transport
dependency through the public API. Hide it; SSL configuration is fully
encapsulated behind ITransportConfiguration."
```

---

### Task 18: Suppress `AuditRoutingKey` knob until implemented (or remove for v-major)

**Why:** `IQueueConfiguration.AuditRoutingKey` is documented as "Reserved — currently ignored." Shipping a non-functional knob in the public API for v-major is a trap.

**Files:**
- Modify: [src/ServiceConnect.Interfaces/Configuration/IQueueConfiguration.cs:27-34](src/ServiceConnect.Interfaces/Configuration/IQueueConfiguration.cs#L27-L34) and [src/ServiceConnect/Configuration/QueueConfiguration.cs:19](src/ServiceConnect/Configuration/QueueConfiguration.cs#L19)

Pick one of two options (raise with user before applying):

**Option A — Remove the knob** (preferred for v-major if the feature isn't on the roadmap):
- Delete `AuditRoutingKey` from the interface and implementation.

**Option B — Throw on non-empty value**:
- Add a `ThrowIfFrozen`-style guard in the setter that throws when value is non-empty: `throw new NotSupportedException("AuditRoutingKey is not yet implemented.")`

- [ ] **Step 1: Confirm direction with user, then apply**

- [ ] **Step 2: Commit**

```bash
git commit -m "refactor(config): [remove|guard] unimplemented AuditRoutingKey knob"
```

---

### Task 19: Move TLS-off-vs-non-loopback warning to `ServiceConnectBuilder.ValidateTransport`

**Why:** The warning currently lives in the RabbitMQ adapter (`ConnectionFactoryBuilder.IsLoopback`). If someone swaps adapters, the safeguard silently disappears. Also: `IsLoopback` only recognizes the literal string `"localhost"`, producing noisy warnings on Docker Compose service names like `"rabbitmq"`.

**Files:**
- Modify: [src/ServiceConnect/ServiceConnectBuilder.cs](src/ServiceConnect/ServiceConnectBuilder.cs) — `ValidateTransport`
- Modify: [src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs:163](src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L163) — add an opt-in suppression flag

- [ ] **Step 1: Add adapter-independent validation in `ValidateTransport`**

Add a check that logs a Warning when `SslEnabled == false` and `Host` is not a loopback address. Recognize: `IPAddress.IsLoopback`, `"localhost"` (case-insensitive), `"127.x.x.x"`, `"::1"`, `"[::1]"`.

- [ ] **Step 2: Add a config-driven suppression flag**

Add `RabbitMQSettingKeys.SuppressPlaintextWarning` (or equivalent) so deployments where plaintext is intentional (Docker Compose, dev clusters) can silence the noise without resorting to log-level changes.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect/ src/ServiceConnect.Client.RabbitMQ/
git commit -m "feat(transport): adapter-independent TLS-off warning + suppression flag

Move the plaintext-against-non-loopback warning out of the RabbitMQ
adapter and into ServiceConnectBuilder.ValidateTransport so it survives
adapter swaps. Add a SuppressPlaintextWarning flag so Docker Compose
deployments where plaintext is intentional can silence the noise."
```

---

### Task 20: `IHandlerRegistry` registrations should use `TryAddEnumerable`

**Why:** Currently four `services.AddSingleton<IHandlerRegistry>(...)` calls. Idempotent in current code (one-shot `AddServiceConnect` guard) but fragile to refactor. `TryAddEnumerable` is the correct idiom for "register one of many implementations under a single service type."

**Files:**
- Modify: [src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.Handlers.cs:73-104](src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.Handlers.cs#L73-L104)

- [ ] **Step 1: Change `AddSingleton<IHandlerRegistry>(...)` to `TryAddEnumerable(ServiceDescriptor.Singleton<IHandlerRegistry, TImpl>(...))` for all 4 sites**

- [ ] **Step 2: Run DI tests + commit**

```bash
git add src/ServiceConnect/DependencyInjection/
git commit -m "refactor(di): use TryAddEnumerable for IHandlerRegistry registrations"
```

---

### Task 21: Apply pre-registration suppression to all handler kinds, not only `IMessageHandler<T>`

**Why:** The `preExistingServiceTypes` check fires only in the `MessageHandler` branch. `IProcessHandler`, `IStreamHandler`, and `Aggregator` user pre-registrations are not suppressed → both user and scanner implementations fire on dispatch.

**Files:**
- Modify: [src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.Handlers.cs:154-218](src/ServiceConnect/DependencyInjection/ServiceCollectionExtensions.Handlers.cs#L154-L218)

- [ ] **Step 1: Apply the same `preExistingServiceTypes` check in all four switch branches**

- [ ] **Step 2: Add tests + commit**

Test: register a fake `IProcessHandler<TData, TMsg>` explicitly, then run scanner — only the user-registered one should fire.

```bash
git add src/ServiceConnect/DependencyInjection/
git commit -m "fix(di): apply user pre-registration suppression to all handler kinds"
```

---

## Group C: Observability / documentation (must fix for v-major)

### Task 22: Document the "span-tag-only, never a metric dimension" contract on high-cardinality `MessagingAttributes`

**Why:** `messaging.message.id` and `messaging.message.conversation_id` are appropriate as span attributes but would explode cardinality if copied into a `TagList` for a metric. The current source has no enforcement and no warning comment at the attribute-name constant.

**Files:**
- Modify: [src/ServiceConnect.Telemetry/MessagingAttributes.cs](src/ServiceConnect.Telemetry/MessagingAttributes.cs)

- [ ] **Step 1: Add a prominent comment**

Right above each high-cardinality constant:

```csharp
// HIGH CARDINALITY. Permitted as a span tag (Activity attribute) because span
// backends cap retention; MUST NOT be added to a TagList used for metrics. A
// per-message GUID dimension on a Counter/Histogram fans out to unbounded
// series and will exhaust a Prometheus/VictoriaMetrics backend's cardinality budget.
public const string MessageId = "messaging.message.id";
```

(Same for `MessageConversationId`.)

- [ ] **Step 2: Commit**

```bash
git add src/ServiceConnect.Telemetry/MessagingAttributes.cs
git commit -m "docs(telemetry): mark high-cardinality span attributes never-as-metric-tag"
```

---

### Task 23: Map AMQP-specific exceptions explicitly in `ExceptionTypeMapper`

**Why:** Currently falls through to `exception.GetType().Name` for `OperationInterruptedException`, `AlreadyClosedException`, `BrokerUnreachableException`, `PublishException` — short names that can collide and that are not stable across RabbitMQ.Client major version bumps.

**Files:**
- Modify: [src/ServiceConnect/Diagnostics/ExceptionTypeMapper.cs:25](src/ServiceConnect/Diagnostics/ExceptionTypeMapper.cs#L25)

- [ ] **Step 1: Add explicit mappings**

```csharp
return exception switch
{
    OperationCanceledException => "cancelled",
    TimeoutException => "timeout",
    global::RabbitMQ.Client.Exceptions.AlreadyClosedException => "channel_closed",
    global::RabbitMQ.Client.Exceptions.BrokerUnreachableException => "broker_unreachable",
    global::RabbitMQ.Client.Exceptions.PublishException => "publish_nacked",
    global::RabbitMQ.Client.Exceptions.OperationInterruptedException => "broker_interrupted",
    _ => exception.GetType().Name,
};
```

(Note: `AlreadyClosedException` must come *before* `OperationInterruptedException` since the former is a subclass.)

- [ ] **Step 2: Commit**

```bash
git add src/ServiceConnect/Diagnostics/ExceptionTypeMapper.cs
git commit -m "feat(diagnostics): explicit error.type mapping for AMQP exceptions

Names are now stable across RabbitMQ.Client version bumps and unambiguous
across namespaces."
```

---

### Task 24: Make `ServiceConnectInstrumentationOptions` immutable post-registration

**Why:** `AddTelemetry` captures the options object reference. The caller can mutate `MaxTagValueLength`, `EnrichWithMessage`, etc. after DI registration → tearing under concurrent message dispatch.

**Files:**
- Modify: [src/ServiceConnect.Telemetry/ServiceConnectInstrumentationOptions.cs](src/ServiceConnect.Telemetry/ServiceConnectInstrumentationOptions.cs) and [src/ServiceConnect.Telemetry/TelemetryBuilderExtensions.cs:30-42](src/ServiceConnect.Telemetry/TelemetryBuilderExtensions.cs#L30-L42)

- [ ] **Step 1: Switch to init-only setters; pass `configure` callback a mutable builder; freeze afterwards**

Make `ServiceConnectInstrumentationOptions` properties `init`-only. Have `AddTelemetry` accept `Action<ServiceConnectInstrumentationOptionsBuilder>`, build into a frozen options instance, register that.

- [ ] **Step 2: Commit**

```bash
git add src/ServiceConnect.Telemetry/
git commit -m "feat(telemetry): immutable instrumentation options after registration"
```

---

### Task 25: Add ‎`server.address` and `server.port` to OTel spans

**Why:** OTel `messaging` semantic conventions require these on producer/consumer spans. Missing fields make standard dashboards display empty broker columns and break multi-broker correlation.

**Files:**
- Modify: [src/ServiceConnect.Telemetry/IMessagingSystemAttributes.cs](src/ServiceConnect.Telemetry/IMessagingSystemAttributes.cs) — add `ServerAddress` / `ServerPort`
- Modify: [src/ServiceConnect.Telemetry/RabbitMqMessagingSystemAttributes.cs](src/ServiceConnect.Telemetry/RabbitMqMessagingSystemAttributes.cs) — implement
- Modify: [src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs](src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs) — stamp on every span

- [ ] **Step 1: Add to interface + implementations + span-builder**

- [ ] **Step 2: Update consume operation type from `"receive"` to `"process"`**

Also covers a separate finding: the `Consume` span should carry `messaging.operation.type = "process"` for handler dispatch, not `"receive"` (which is for broker-side polling).

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Telemetry/
git commit -m "feat(telemetry): add server.address/port; correct consume operation.type

Aligns with OTel messaging semantic conventions: producer/consumer spans
carry the broker endpoint; consume spans report 'process' (handler dispatch)
rather than 'receive' (broker poll)."
```

---

## Group D: Architecture decisions (must resolve for v-major)

### Task 26: Decide on RabbitMQ topology recovery strategy

**Why:** `ConnectionFactoryBuilder` sets `TopologyRecoveryEnabled = true`, which means RabbitMQ.Client auto-redeclares exchanges/queues on reconnect. The application *also* explicitly redeclares all topology in `Consumer.StartConsumingAsync` and `ProducerConnection.EnsureExchangeDeclaredAsync`. Running both is the dangerous middle ground — under topology drift (e.g. operator changes queue arguments), the library's recovery channel can be closed by the broker's `PRECONDITION_FAILED` before consumer bindings are restored, producing a silent half-recovered state. This is a design decision, not a mechanical fix; it has to be made before v-major because reversing it post-1.0 is breaking.

**Files:**
- [src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs:47](src/ServiceConnect.Client.RabbitMQ/Connection/ConnectionFactoryBuilder.cs#L47) — flag value
- [src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs](src/ServiceConnect.Client.RabbitMQ/Consumer/Consumer.cs) — application redeclaration path
- [src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs:287-305](src/ServiceConnect.Client.RabbitMQ/Producer/ProducerConnection.cs#L287-L305) — producer redeclaration

- [ ] **Step 1: Investigate the failure-mode trade-off**

For each option, document:
- Recovery latency under broker rolling restart (TopologyRecovery is faster — fires immediately on reconnect; app-level fires on next consume/publish)
- Behavior under topology drift (TopologyRecovery silently fails on PRECONDITION_FAILED; app-level surfaces the error to the application)
- Code complexity (TopologyRecovery hides reconnect plumbing; app-level keeps it explicit)
- Observability (TopologyRecovery logs at the RabbitMQ.Client level; app-level uses our logger / metrics)

- [ ] **Step 2: Pick one and remove the other**

**Option A — Disable `TopologyRecoveryEnabled`:** the application owns recovery entirely. Better observability, explicit failure modes. Higher reconnect latency.

**Option B — Remove application-level redeclaration:** delete redeclaration from `Consumer.StartConsumingAsync` and `ProducerConnection.EnsureExchangeDeclaredAsync`. Trust the client library. Loses our cache-invalidation logic; need to verify the library's redeclaration order against consumer bindings.

Recommended starting point: **Option A** — the application already has explicit recovery, the producer's exchange-declaration cache is keyed on connection generation, and the failure modes are visible to our logger. Disabling library recovery removes the duplication without losing functionality.

- [ ] **Step 3: Apply + add a regression test**

Spin up Testcontainers RabbitMQ; restart it mid-test; assert the consumer resumes and the producer's next publish succeeds.

- [ ] **Step 4: Commit**

```bash
git commit -m "refactor(transport): disable RabbitMQ.Client topology recovery

Application-level recovery already redeclares topology on each connect.
Running both paths produced a silent half-recovered state under topology
drift. Application-level recovery is the canonical path; library recovery
is now off."
```

---

### Task 27: Document `ConfigureMapper` purity contract for saga `IProcessHandler<TData, TMessage>`

**Why:** Suspect finding from the processor review. `ProcessManagerProcessor.PersistAsync` calls `descriptor.FindData(finder, mapper, message, ct)` for a second time after the handler ran. The `mapper` was built from the dispatch-time handler instance; if the handler mutated instance state used inside `ConfigureMapper`, the second `MessageProp.Invoke(msg)` could return a different key than the first. The fix isn't to refactor (caching the key from the first find would change semantics); it's to *document the contract* that `ConfigureMapper` must produce a pure, stable mapping.

**Files:**
- Modify: [src/ServiceConnect.Interfaces/ProcessManagers/IProcessHandler.cs](src/ServiceConnect.Interfaces/ProcessManagers/IProcessHandler.cs) (or wherever `ConfigureMapper` is declared)

- [ ] **Step 1: Add an xmldoc `<remarks>` block**

```xml
/// <remarks>
/// <para><strong>Purity:</strong> Implementations MUST be pure with respect to handler instance
/// state. The framework invokes <see cref="ConfigureMapper"/> twice per delivery — once before the
/// handler runs to locate the saga state, and once during persistence to re-find the state for
/// concurrency-safe update. Returning a different mapping based on handler-instance mutation between
/// these calls will cause the second find to resolve a different saga row than the first, with
/// undefined persistence behaviour.</para>
/// </remarks>
```

- [ ] **Step 2: Commit**

```bash
git add src/ServiceConnect.Interfaces/ProcessManagers/
git commit -m "docs(saga): document ConfigureMapper purity contract

Framework invokes it twice per delivery (before-handler + during-persist).
Implementations dependent on handler-instance mutation produce undefined
persistence behaviour."
```

---

## Deferred to v-major+0.1 (not in this plan)

The following findings are real but low-impact. Document them in a `TECH-DEBT.md` (or GitHub issues) and ship v-major without them:

1. `BusHostedService.StopAsync` doesn't enforce handler drain at the `IConsumer` interface level — documentation gap.
2. `ConsumeContextPool.Return` count-check is non-atomic (TOCTOU on `MaxPoolSize`) — overflow bounded by thread count.
3. `RoutingSlipDestinationValidator` uses `OrdinalIgnoreCase` for `amq.` prefix — slight over-rejection of upper-case names.
4. `Bus.BuildHeadersDirect` doesn't stamp `TypeName`/`FullTypeName` — header set differs across filter-on vs filter-off, but wire behaviour identical.
5. `MessageBusReadStream.IsComplete` overflow at `long.MaxValue` — physically unreachable.
6. `MongoClientFactory.ClearCertificateCache` cert dispose race — `internal`, test-only.
7. `MessageTypeRegistry._mappingsView` cache invalidation not `volatile` — populated at startup, read at dispatch.
8. `IProcessManagerTypeRegistry` registered with `AddSingleton`, not `TryAddSingleton`.
9. `PerProviderCache` factory retry-on-exception undocumented.
10. `IsLoopback` doesn't recognize `[::1]` bracket form.
11. `OutboundHeaderBuilder.FormatTimestamp` buffer comment off-by-5.
12. Public `ServiceConnectActivitySource.Shutdown()` / `ServiceConnectMeter.Shutdown()` — should be `internal` (consider `[EditorBrowsable(Never)]`).

---

## Rejection Log (false positives — do not fix)

The following findings did NOT survive verification. Documented here so they don't get re-raised:

1. **`Producer._disposed` Volatile.Read** — the field doesn't exist in `Producer.cs`; the cited `_disposed` resolves to `ProducerConnection._disposed` which already uses `Volatile.Read` correctly.
2. **`_connectionGeneration` non-atomic RMW** — both writes occur inside `_connectionSemaphore`; no two writers can race.
3. **`GetShutdownPublishToken()` ODE** — `_admissionGate.BeginShutdown()` blocks new admits before the CTS is disposed; the drain wait ensures no in-flight delivery can still reach the token getter when disposal completes.
4. **`publishChannel!` NRE** — the only path that creates the race requires `RaiseDeliveryForTests`; not reachable in production.
5. **`RequestState.TryHandleReply` OnReply under lock** — documented design choice. The lock is per-`RequestState`; serialization is intentional.
6. **`SendRequestMultiAsync` snapshot race** — `_closed=true` is set under `_stateLock` before `TrySetResult`; any "late" reply must see `_closed` and return without mutating.
7. **`AggregatorProcessor` SemaphoreSlim dispose race** — the drain awaits TCS tasks that complete only after `flushLock.Release()`, so the semaphore can't be disposed while a `WaitAsync` is pending.
8. **`RabbitMqAdmissionGate._drainTcs` not reset** — host is single-use; the second-drain path is structurally unreachable.
9. **`CoerceToQueueArgs` returns mutable Dictionary** — no library caller mutates the returned dictionary.
10. **`IsCancelledByBroker` LINQ race** — `ConcurrentBag` enumeration is snapshot-safe; `Volatile.Read(ref int)` on a disposed host is safe.
11. **`ConsumeContextPool.Release` clear order** — `Interlocked.Increment` is a full fence; `EnsureActive` check on the stale rent token prevents observers from reaching the cleared fields.
12. **`FilterPipeline` async leak** — user-misuse pattern, not a framework bug. (Optional: add a remark to `IFilter.ProcessAsync` xmldoc.)
13. **`SendMessagePipeline` scoped middleware** — already validated at startup in `ValidateSendMessageMiddlewareLifetimes`.
14. **`MongoDbProcessManagerFinder.UpdateDataAsync` MatchedCount vs ModifiedCount** — every `UpdateDataAsync` bumps the version field, so `ModifiedCount=0` while `MatchedCount=1` is unreachable.
15. **`MongoDbTimeoutStore.GetTimeoutsBatchAsync` NotSupportedException** — already caught at line 153.
16. **Saga `BuildLockKey` Guid.Empty fallback** — documented design trade-off; the fallback only fires when correlation was never set (in which case the persistor's correlation-keyed find would also fail).
17. **`Bus.SendToManyAsync` ignores filter-injected RoutingKey** — `Send` semantics target named endpoints, not topic-routed exchanges; `RoutingKey` doesn't apply.
18. **`HandlerProcessor` AggregateException swallow OCE** — actually a real finding, kept as Task 12. (Removed from rejection log.)

---

## Self-Review Checklist

- [x] Every Task references real files at real line numbers (verified via Read during planning).
- [x] Every confirmed bug has a corresponding Task.
- [x] No "TBD" / "TODO" placeholders.
- [x] Each Task has a commit message that explains *why*, not just *what*.
- [x] Tasks are ordered by area, then by approximate severity within each area.
- [x] Tasks 18 (AuditRoutingKey) flags a user decision needed before applying — not silently chosen.
- [x] Tasks 11 (aggregator name) flags a BREAKING change — needs migration note in release notes.
- [x] Rejection log explains why each false positive was rejected.
- [x] Deferred list is explicit about what's *not* shipping in v-major.

---

## Execution

Recommended approach: **Subagent-driven**, one task per agent, with review between tasks. Many tasks are independent and can be parallelized (e.g., Tasks 17, 22, 23 don't touch the same files as the concurrency fixes). The data-correctness tasks (1, 2, 4, 7, 10) should land first since they affect production traffic.

After all tasks land, run the full E2E suite (`dotnet test src/ServiceConnect.EndToEndTests`) before merging. The cgroup-fence (`~/.local/bin/dotnet`) prevents the desktop-freeze regression — see `CLAUDE.md`.
