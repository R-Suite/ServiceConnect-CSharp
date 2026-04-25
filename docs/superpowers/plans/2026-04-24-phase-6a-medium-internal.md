# Phase 6a — Medium fixes (Streaming + RabbitMQ + Core + Persistence) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve 11 confirmed Medium-severity bugs in streaming, RabbitMQ consumer, core processors/lifecycle, and persistence (Mongo + InMemory) while recording retroactive tracker entries for 4 items already-resolved or not-a-bug at HEAD.

**Architecture:** Surgical, per-file fixes guided by re-verification against HEAD. All changes preserve public surface area and existing test coverage. New tests are unit-level except where the Mongo `EnsureGuidSerializerRegistered` invariant is intrinsically AppDomain-global — in which case the test asserts the static-ctor side-effect via reflection rather than registration count. Each task is its own commit; the closeout task does the V.1–V.7 sweep and tracker/strategy-spec updates.

**Tech Stack:** .NET 10 / C# 13, xUnit + Moq, RabbitMQ.Client v7, MongoDB.Driver, ConcurrentDictionary CAS patterns, `Interlocked.Exchange`, `ReaderWriterLockSlim`.

**Re-verification snapshot (2026-04-25):**
- **Actionable (11):** M-01, M-06, M-11, M-12, M-13, M-16, M-18, M-19, M-20, M-21, M-22
- **Retroactive — already-fixed at HEAD (3):** M-02 (immutable record + `TryUpdate` CAS via `with` expression eliminates torn read), M-03 (`Interlocked.Add` reservation with rollback on overshoot or duplicate `TryAdd` failure), M-04 (`if (setupChannel is { IsOpen: true })` guard prevents `CloseAsync` on faulted channel — masking scenario unreachable)
- **Retroactive — not-a-bug-at-HEAD (1):** M-08 (`ObjectDisposedException` already caught at line 547; `ArgumentOutOfRangeException` only reachable on non-monotonic clock — defensive hardening, not a live bug)

---

## Task 1: M-01 — `MaxActiveStreams` TOCTOU (StreamProcessor)

**Files:**
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:83-99`
- Test: `src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs`

**Bug:** `if (!_activeStreams.ContainsKey(sequenceId) && _activeStreams.Count >= MaxActiveStreams) { ... return NotHandledTask; }` then `GetOrAdd` is a TOCTOU window: between the `Count` read and `GetOrAdd`, concurrent insertions can push `_activeStreams` past `MaxActiveStreams`. Cap is intended to bound memory but is observably leaky under contention.

**Fix:** Replace the read-then-add with `GetOrAdd` followed by an atomic post-insert size check. If the post-insert count exceeds the cap *and* this call was the inserter (factory ran), `TryRemove` the just-added entry by KVP equality and return `NotHandled`. Use the existing `ActiveStreamState`'s structural equality so the `TryRemove(KeyValuePair)` overload only removes the exact instance we just added.

- [ ] **Step 1: Write the failing test**

Add to `StreamProcessorTests.cs` (paste verbatim):

```csharp
[Fact]
public async Task ProcessAsync_ConcurrentExclusiveOpensAtCap_DoNotExceedCap()
{
    using var processor = CreateProcessor(); // existing helper
    int parallelism = 64;
    int extra = 32; // we will fire (cap + extra) opens concurrently

    // Fill to cap-1 sequentially so the contended boundary is the cap-th insert.
    for (int i = 0; i < StreamProcessor.MaxActiveStreams - 1; i++)
        await OpenStream(processor, sequenceId: $"warm-{i}");

    using var startGate = new ManualResetEventSlim(false);
    var contestants = Enumerable.Range(0, parallelism + extra)
        .Select(i => Task.Run(async () =>
        {
            startGate.Wait();
            await OpenStream(processor, sequenceId: $"contest-{i}");
        }))
        .ToList();

    startGate.Set();
    await Task.WhenAll(contestants);

    Assert.True(processor.ActiveStreamCount <= StreamProcessor.MaxActiveStreams,
        $"observed {processor.ActiveStreamCount}, cap {StreamProcessor.MaxActiveStreams}");
}
```

If `ActiveStreamCount` doesn't already exist on `StreamProcessor`, add an internal `internal int ActiveStreamCount => _activeStreams.Count;` property (test-only access).

If `OpenStream` helper does not exist, create it inside the test class as:

```csharp
private static Task OpenStream(StreamProcessor processor, string sequenceId)
{
    var headers = new Dictionary<string, object>
    {
        [HeaderKeys.SequenceId] = Encoding.UTF8.GetBytes(sequenceId),
        [HeaderKeys.PacketNumber] = Encoding.UTF8.GetBytes("0"),
        [HeaderKeys.Start] = Encoding.UTF8.GetBytes(bool.TrueString),
    };
    return processor.ProcessAsync(ReadOnlyMemory<byte>.Empty, typeof(Message), null!, headers, new Envelope { Headers = headers, Body = ReadOnlyMemory<byte>.Empty }, CancellationToken.None).AsTask();
}
```

(Match exactly the header keys and helper shape used by existing tests in the file — read neighbouring tests first to confirm field names.)

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~StreamProcessorTests.ProcessAsync_ConcurrentExclusiveOpensAtCap_DoNotExceedCap"
```

Expected: FAIL with `observed N, cap 1000` where `N > 1000`.

- [ ] **Step 3: Implement fix in StreamProcessor.cs**

Replace lines 83-99 (`if (!_activeStreams.ContainsKey(sequenceId) && _activeStreams.Count >= MaxActiveStreams)` block + `GetOrAdd`) with:

```csharp
// Atomic admission: insert then post-check size, removing our own entry if the
// insertion pushed us past the cap. Avoids the read-then-add TOCTOU where two
// concurrent dispatches both observe Count == cap-1 and both GetOrAdd.
bool inserted = false;
ActiveStreamState state = _activeStreams.GetOrAdd(sequenceId, id =>
{
    inserted = true;
    return new ActiveStreamState(new MessageBusReadStream(id), _timeProvider.GetUtcNow());
});

if (inserted && _activeStreams.Count > MaxActiveStreams)
{
    // Roll back only our own insertion. Structural equality on ActiveStreamState
    // (immutable record) ensures TryRemove(KVP) only removes the exact instance.
    _activeStreams.TryRemove(new KeyValuePair<string, ActiveStreamState>(sequenceId, state));
    _logger.LogWarning(
        "Active stream cap {Cap} reached; rejecting new stream {SequenceId}",
        MaxActiveStreams, sequenceId);
    return NotHandledTask;
}
```

- [ ] **Step 4: Run test to verify it passes**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Run the full StreamProcessor suite to confirm no regressions**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~StreamProcessorTests"
```

Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/Services/Processors/StreamProcessor.cs \
        src/ServiceConnect.UnitTests/Processors/StreamProcessorTests.cs
git commit -m "fix(streaming): atomic admission for MaxActiveStreams cap (M-01)

Replace read-then-add TOCTOU with GetOrAdd + post-insert size check;
roll back our own insertion via TryRemove(KVP) when overshoot detected."
```

---

## Task 2: M-06 — `BasicConsumeAsync` does not pass `cancellationToken`

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs:155`
- Test: `src/ServiceConnect.Client.RabbitMQ.UnitTests/RabbitMqConsumerHostTests.cs` (or nearest equivalent — read existing test layout)

**Bug:** `_consumerTag = await _model.BasicConsumeAsync(_queueName, false, "", false, false, null, _consumer).ConfigureAwait(false);` — trailing `cancellationToken` not passed. Startup hang on broker side becomes uncancellable.

**Fix:** Pass `cancellationToken` through. The method already receives a `CancellationToken cancellationToken` parameter on its containing method.

- [ ] **Step 1: Write the failing test**

Add to the consumer-host tests:

```csharp
[Fact]
public async Task StartConsumingAsync_BasicConsumeAsync_FlowsCancellationToken()
{
    using var cts = new CancellationTokenSource();
    var model = new Mock<IChannel>(MockBehavior.Strict);
    CancellationToken capturedToken = default;

    model.Setup(m => m.BasicConsumeAsync(
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<IAsyncBasicConsumer>(),
            It.IsAny<CancellationToken>()))
        .Callback<string, bool, string, bool, bool, IDictionary<string, object?>?, IAsyncBasicConsumer, CancellationToken>(
            (_, _, _, _, _, _, _, ct) => capturedToken = ct)
        .ReturnsAsync("tag");

    var host = CreateHostWithModel(model.Object); // existing helper or new minimal one
    await host.StartConsumingAsync((_, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }),
                                   queueName: "q",
                                   cancellationToken: cts.Token);

    Assert.Equal(cts.Token, capturedToken);
}
```

If a `CreateHostWithModel` helper does not already exist, add one that constructs a `RabbitMqConsumerHost` with the supplied mocked `IChannel` and minimal stubs for everything else. The host is `internal sealed` — make sure `InternalsVisibleTo` is set, otherwise place the test in the same assembly as existing host tests.

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test src/ServiceConnect.Client.RabbitMQ.UnitTests/ServiceConnect.Client.RabbitMQ.UnitTests.csproj \
    --filter "FullyQualifiedName~StartConsumingAsync_BasicConsumeAsync_FlowsCancellationToken"
```

Expected: FAIL — `capturedToken` is `default(CancellationToken)`, not `cts.Token`.

- [ ] **Step 3: Implement the fix**

Edit `RabbitMqConsumerHost.cs:155`. Old:

```csharp
_consumerTag = await _model.BasicConsumeAsync(_queueName, false, "", false, false, null, _consumer).ConfigureAwait(false);
```

New:

```csharp
_consumerTag = await _model.BasicConsumeAsync(_queueName, false, "", false, false, null, _consumer, cancellationToken).ConfigureAwait(false);
```

(Confirm `cancellationToken` is in scope — it is the method parameter on `StartConsumingAsync`.)

- [ ] **Step 4: Run test to verify it passes**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs \
        src/ServiceConnect.Client.RabbitMQ.UnitTests/RabbitMqConsumerHostTests.cs
git commit -m "fix(rabbitmq): flow cancellationToken into BasicConsumeAsync (M-06)

Startup hangs on broker side were uncancellable because the trailing
CancellationToken parameter was omitted from the BasicConsumeAsync call."
```

---

## Task 3: M-11 — Aggregator remove-before-execute drops batch on handler exception

**Files:**
- Modify: `src/ServiceConnect/Services/Processors/AggregatorProcessor.cs:214-216`
- Test: `src/ServiceConnect.UnitTests/Processors/AggregatorProcessorTests.cs`

**Bug:** `RemoveSnapshotAsync` is called *before* `InvokeExecuteAsync`. If the handler throws non-cancellation, the batch is already gone — messages cannot be retried.

**Fix:** Reorder to execute first, then remove on success. On execute exception, propagate without removing so the broker redelivers and the snapshot is re-flushable.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task FlushAggregatorAsync_HandlerThrows_SnapshotRemainsForRetry()
{
    var persistor = new InMemoryAggregatorPersistor("", "", "");
    string streamName = "test-stream";
    await persistor.InsertDataAsync(new TestMessage(Guid.NewGuid()), streamName);
    await persistor.InsertDataAsync(new TestMessage(Guid.NewGuid()), streamName);

    var aggregator = new ThrowingAggregator(); // existing or new test double
    var processor = CreateProcessorFor(aggregator, persistor, streamName);

    await Assert.ThrowsAnyAsync<InvalidOperationException>(() => processor.FlushAggregatorAsync(...));

    int remaining = await persistor.CountAsync(streamName);
    Assert.Equal(2, remaining);
}

private sealed class ThrowingAggregator : Aggregator<TestMessage>
{
    public override Task ExecuteAsync(IList<TestMessage> messages, CancellationToken ct)
        => throw new InvalidOperationException("boom");
}
```

If `CreateProcessorFor` doesn't exist, follow the construction pattern from neighbouring tests in the file. If `TestMessage` doesn't exist with a Guid ctor, define a minimal one in the test file: `private sealed record TestMessage(Guid CorrelationId) : Message(CorrelationId);`.

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~FlushAggregatorAsync_HandlerThrows_SnapshotRemainsForRetry"
```

Expected: FAIL — `remaining` is 0 because removal happened before execute.

- [ ] **Step 3: Implement the fix**

Edit `AggregatorProcessor.cs` around lines 214-216. Old:

```csharp
await persistor.RemoveSnapshotAsync(descriptor.AggregatorName, snapshot, cancellationToken).ConfigureAwait(false);
await descriptor.InvokeExecuteAsync(aggregator, typedList, cancellationToken).ConfigureAwait(false);
```

New:

```csharp
// Execute first, then remove on success. On handler exception we propagate
// without removing so the broker redelivers and the snapshot is re-flushable.
// CancellationException from a co-operative shutdown also leaves the snapshot
// in place — by-design for retry on next admission.
await descriptor.InvokeExecuteAsync(aggregator, typedList, cancellationToken).ConfigureAwait(false);
await persistor.RemoveSnapshotAsync(descriptor.AggregatorName, snapshot, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 4: Run test to verify it passes**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Run full AggregatorProcessor suite**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~AggregatorProcessorTests"
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/Services/Processors/AggregatorProcessor.cs \
        src/ServiceConnect.UnitTests/Processors/AggregatorProcessorTests.cs
git commit -m "fix(aggregator): execute before remove so handler exceptions don't drop batch (M-11)

If the handler throws non-cancellation, the batch is now preserved in the
persistor for redelivery; previously RemoveSnapshotAsync ran first and the
messages were unrecoverable."
```

---

## Task 4: M-12 — `HandlerProcessor` first exception skips remaining handlers

**Files:**
- Modify: `src/ServiceConnect/Services/Processors/HandlerProcessor.cs:70-77`
- Test: `src/ServiceConnect.UnitTests/Processors/HandlerProcessorTests.cs`

**Bug:** Per-message multi-handler dispatch short-circuits on first `throw`. Independent downstream handlers for the same message are skipped.

**Fix (chosen — collect-then-throw `AggregateException`):** Wrap each `InvokeHandleAsync` in try/catch, collect exceptions, then `throw new AggregateException(list)` after the loop. If only one exception, `AggregateException.Flatten` is *not* applied — callers still see one entry. If no exceptions, no throw.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task ProcessAsync_FirstHandlerThrows_RemainingHandlersStillRun()
{
    int handlerARan = 0, handlerBRan = 0, handlerCRan = 0;
    var handlers = new[]
    {
        TestHandler<TestMessage>.Create(_ => { handlerARan++; throw new InvalidOperationException("A"); }),
        TestHandler<TestMessage>.Create(_ => { handlerBRan++; }),
        TestHandler<TestMessage>.Create(_ => { handlerCRan++; throw new InvalidOperationException("C"); }),
    };

    var processor = CreateProcessorWith(handlers);
    var ex = await Assert.ThrowsAsync<AggregateException>(() => DispatchAsync(processor, new TestMessage()));

    Assert.Equal(1, handlerARan);
    Assert.Equal(1, handlerBRan);
    Assert.Equal(1, handlerCRan);
    Assert.Equal(2, ex.InnerExceptions.Count);
    Assert.Contains(ex.InnerExceptions, e => e.Message == "A");
    Assert.Contains(ex.InnerExceptions, e => e.Message == "C");
}
```

(Adapt `TestHandler<T>` and `CreateProcessorWith` to whatever the existing test file uses — look at the neighbouring test for the established pattern.)

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProcessAsync_FirstHandlerThrows_RemainingHandlersStillRun"
```

Expected: FAIL — `handlerBRan` and `handlerCRan` are 0 because the `A` exception bypassed them.

- [ ] **Step 3: Implement the fix**

Edit `HandlerProcessor.cs:70-74`. Old:

```csharp
foreach (var (handler, descriptor) in invocations)
{
    descriptor.SetContext(handler, context);
    await descriptor.InvokeHandleAsync(handler, message).ConfigureAwait(false);
}
```

New:

```csharp
List<Exception>? handlerExceptions = null;
foreach (var (handler, descriptor) in invocations)
{
    try
    {
        descriptor.SetContext(handler, context);
        await descriptor.InvokeHandleAsync(handler, message).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Co-operative shutdown — don't run remaining handlers; rethrow the OCE
        // unaltered so callers can distinguish shutdown from handler faults.
        throw;
    }
    catch (Exception ex)
    {
        // Collect handler faults so independent handlers for the same message
        // all get a chance to run; aggregate at end of loop.
        (handlerExceptions ??= new List<Exception>()).Add(ex);
    }
}

if (handlerExceptions is not null)
    throw new AggregateException(
        $"{handlerExceptions.Count} handler(s) threw while dispatching {message.GetType().Name}.",
        handlerExceptions);
```

(Confirm `cancellationToken` is in scope. If it isn't passed into the loop, thread it through from the method's outer parameter — read the surrounding code first.)

- [ ] **Step 4: Run test to verify it passes**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Run full HandlerProcessor suite**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~HandlerProcessorTests"
```

Expected: all pass — including any test that asserts the *previous* short-circuit semantic. If such a test exists, update it: the new contract is "all handlers run; faults aggregate". Document the change in the test's name/comment if it was a behavioural assertion.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/Services/Processors/HandlerProcessor.cs \
        src/ServiceConnect.UnitTests/Processors/HandlerProcessorTests.cs
git commit -m "fix(handlers): aggregate handler faults instead of short-circuiting (M-12)

All independent handlers for a message now run; non-cancellation exceptions
are collected and rethrown as AggregateException at end of dispatch.
OperationCanceledException for the dispatch CT still short-circuits."
```

---

## Task 5: M-13 — `Bus.DisposeAsync` races `_lifecycleSemaphore` with `StartConsumingAsync`

**Files:**
- Modify: `src/ServiceConnect/Bus.cs` (lifecycle gating around `_disposed`/`_lifecycleSemaphore`; specifically the `ThrowIfDisposed` check, the `WaitAsync` call site, and `DisposeAsync`)
- Test: `src/ServiceConnect.UnitTests/BusTests.cs` (or nearest)

**Bug:** `DisposeAsync` sets `_disposed = true` under `_stateLock` then disposes `_lifecycleSemaphore`, but `StartConsumingAsync` reads `_disposed` and acquires `_lifecycleSemaphore` without a barrier with disposal. Concurrent `Start` + `Dispose` produces an `ObjectDisposedException` from the semaphore, which is well-typed but ugly — and skirts the `ThrowIfDisposed` contract.

**Fix:** Promote `_disposed` to `int` and gate Dispose with `if (Interlocked.Exchange(ref _disposed, 1) != 0) return;` before touching the semaphore. In `StartConsumingAsync`/other lifecycle entry points, read `_disposed` via `Volatile.Read` before `_lifecycleSemaphore.WaitAsync`, and after acquiring the semaphore re-check inside the critical section. Wrap `_lifecycleSemaphore.WaitAsync` in `try { ... } catch (ObjectDisposedException) { ThrowIfDisposed(); }` so a Dispose that wins the race surfaces as `ObjectDisposedException(typeof(Bus).FullName)` rather than a raw semaphore disposal.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task ConcurrentStartAndDispose_DoesNotThrowSemaphoreDisposedException()
{
    int iterations = 100;
    int unexpected = 0;
    for (int i = 0; i < iterations; i++)
    {
        var bus = CreateBus(); // existing helper

        var startTask = Task.Run(async () =>
        {
            try { await bus.StartConsumingAsync(); }
            catch (ObjectDisposedException ode) when (ode.ObjectName == typeof(Bus).FullName) { /* expected */ }
            catch (ObjectDisposedException) { Interlocked.Increment(ref unexpected); }
            catch (InvalidOperationException) { /* race-acceptable */ }
        });
        var disposeTask = Task.Run(async () => await bus.DisposeAsync());

        await Task.WhenAll(startTask, disposeTask);
    }

    Assert.Equal(0, unexpected);
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ConcurrentStartAndDispose_DoesNotThrowSemaphoreDisposedException"
```

Expected: FAIL intermittently with `unexpected > 0`. Run several times if the race doesn't trigger on first run.

- [ ] **Step 3: Implement the fix in Bus.cs**

In the field declarations, change `private bool _disposed;` to `private int _disposed; // 0=alive, 1=disposed; access via Interlocked/Volatile`.

Replace `ThrowIfDisposed()` with:

```csharp
private void ThrowIfDisposed()
{
    if (Volatile.Read(ref _disposed) != 0)
        throw new ObjectDisposedException(typeof(Bus).FullName);
}
```

In `DisposeAsync`, replace the `lock (_stateLock) { if (_disposed) return; _disposed = true; }` block with:

```csharp
if (Interlocked.Exchange(ref _disposed, 1) != 0)
    return;
```

…and keep the rest of the dispose body (semaphore disposal etc.) unchanged.

In every lifecycle entry point that calls `await _lifecycleSemaphore.WaitAsync(...)` (e.g., `StartConsumingAsync`, `StopConsumingCoreAsync`), wrap the wait + re-check pattern:

```csharp
ThrowIfDisposed();
try
{
    await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
}
catch (ObjectDisposedException)
{
    // Dispose won the race between ThrowIfDisposed and WaitAsync — surface as
    // a typed Bus disposal so callers see one exception type, not a raw
    // SemaphoreSlim ObjectDisposedException.
    throw new ObjectDisposedException(typeof(Bus).FullName);
}
ThrowIfDisposed(); // re-check inside the critical section
```

(Read each call site individually; do not blanket-`replace_all` — some of these may already do post-acquisition disposed checks.)

- [ ] **Step 4: Run test to verify it passes**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ConcurrentStartAndDispose_DoesNotThrowSemaphoreDisposedException"
```

Expected: PASS, no `unexpected` counts.

- [ ] **Step 5: Run the full BusTests suite**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~BusTests"
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/Bus.cs src/ServiceConnect.UnitTests/BusTests.cs
git commit -m "fix(bus): gate DisposeAsync with Interlocked.Exchange and re-throw typed ODE on semaphore race (M-13)

DisposeAsync now flips _disposed atomically before touching _lifecycleSemaphore.
Lifecycle entry points re-check _disposed after acquiring the semaphore and
translate raw SemaphoreSlim ObjectDisposedException into a typed Bus disposal."
```

---

## Task 6: M-16 — Mongo timeout-store index-ensure flag must invalidate on write error

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs:19, 290` (and the index-ensure helper around line 374-415)
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs:20` (and its index-ensure helper)
- Test: `src/ServiceConnect.Persistence.MongoDb.IntegrationTests` (new test) — run via Testcontainers under `sg docker -c '...'`.

**Bug:** Once `_timeoutIndexEnsuredFlag` (or `_indexesEnsured`) is set to 1, it never resets. After a DB drop in a long-lived process, indexes are not re-created on next write because the flag short-circuits the ensure call. Pass-2 verdict scoped to "DB-drop-without-process-restart" — a legitimate ops/test pattern.

**Fix:** On any write error from the index-create call, reset the flag to 0 so the next caller retries the ensure. Same change in both persistors. Use `Interlocked.Exchange(ref _flag, 0)` in the `catch` block.

- [ ] **Step 1: Write the failing integration test**

```csharp
[Collection("Mongo")] // existing collection fixture if present
public class TimeoutStoreIndexEnsureRetryTests : IClassFixture<MongoFixture>
{
    private readonly MongoFixture _fixture;
    public TimeoutStoreIndexEnsureRetryTests(MongoFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task EnsureIndexes_AfterDbDrop_RecreatesOnNextWrite()
    {
        var store = new MongoDbTimeoutStore(_fixture.ConnectionString, _fixture.DatabaseName);

        // Force first ensure
        await store.InsertTimeoutAsync(NewTimeout(), CancellationToken.None);
        Assert.True(await IndexExistsAsync(_fixture, "timeouts", "Time_1"));

        // Admin drops DB while process still alive
        var client = new MongoClient(_fixture.ConnectionString);
        await client.DropDatabaseAsync(_fixture.DatabaseName);

        // Next write must re-ensure indexes
        await store.InsertTimeoutAsync(NewTimeout(), CancellationToken.None);
        Assert.True(await IndexExistsAsync(_fixture, "timeouts", "Time_1"));
    }
}
```

(Replace index name `"Time_1"` with the actual key the timeout store creates — read `EnsureIndexesAsync` in the source. `IndexExistsAsync` is a small utility: query `db.GetCollection<BsonDocument>("system.indexes")` or use `IMongoCollection.Indexes.ListAsync()`.)

- [ ] **Step 2: Run test to verify it fails**

```
sg docker -c "dotnet test src/ServiceConnect.Persistence.MongoDb.IntegrationTests/ServiceConnect.Persistence.MongoDb.IntegrationTests.csproj --filter 'FullyQualifiedName~EnsureIndexes_AfterDbDrop_RecreatesOnNextWrite'"
```

Expected: FAIL — second `IndexExistsAsync` returns `false` because the cached flag suppressed re-creation.

- [ ] **Step 3: Implement the fix in MongoDbTimeoutStore.cs**

Locate the index-ensure helper (around lines 374-415) and the flag check at line 290. Wrap the `CreateOneAsync`/`CreateManyAsync` calls in `try/catch (Exception)` that resets the flag and rethrows:

```csharp
private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
{
    if (Interlocked.CompareExchange(ref _timeoutIndexEnsuredFlag, 0, 0) == 1)
        return;

    try
    {
        await _collection.Indexes.CreateManyAsync(...).ConfigureAwait(false); // existing call
        Interlocked.Exchange(ref _timeoutIndexEnsuredFlag, 1);
    }
    catch
    {
        // Reset so the next caller retries: a transient write error or a DB drop
        // between calls must not be cached as "already ensured".
        Interlocked.Exchange(ref _timeoutIndexEnsuredFlag, 0);
        throw;
    }
}
```

(Read the actual ensure body and adapt — do not invent calls that aren't there. The shape above is illustrative; the actual `CreateManyAsync` invocation already exists.)

- [ ] **Step 4: Apply the same fix to MongoDbAggregatorPersistor.cs**

Locate the equivalent ensure helper. It uses `private volatile bool _indexesEnsured;`. Convert to `private int _indexesEnsured; // 0/1, access via Interlocked` and apply the same try/catch reset pattern. Replacing `volatile bool` with `int` matches the timeout-store implementation and keeps the reset/re-try story uniform.

- [ ] **Step 5: Run test to verify it passes**

Same command as Step 2. Expected: PASS.

- [ ] **Step 6: Run full Mongo integration suite**

```
sg docker -c "dotnet test src/ServiceConnect.Persistence.MongoDb.IntegrationTests/ServiceConnect.Persistence.MongoDb.IntegrationTests.csproj"
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs \
        src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs \
        src/ServiceConnect.Persistence.MongoDb.IntegrationTests/TimeoutStoreIndexEnsureRetryTests.cs
git commit -m "fix(mongo): reset index-ensured flag on write error so DB drop is recoverable (M-16)

Both MongoDbTimeoutStore and MongoDbAggregatorPersistor now reset their
ensure-flag in the catch block. After a DB drop in a long-lived process
the next write re-creates indexes instead of silently skipping the ensure."
```

---

## Task 7: M-18 — `InMemoryAggregatorPersistor.RemoveDataAsync` requires `is Message`

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs:95-123`
- Test: `src/ServiceConnect.UnitTests/InMemory/InMemoryAggregatorPersistorTests.cs` (or nearest)

**Bug:** `if (list[index].Data is Message message && message.CorrelationId == correlationId)` — third-party DTOs without `: Message` inheritance can be inserted but never matched on remove. MongoDb persistor does not require this constraint.

**Fix:** Use a duck-typed `CorrelationId` extractor that pulls a `Guid CorrelationId` property via reflection (cached per-type) and compares. Falls back to nothing if the type lacks the property — the existing `ConcurrencyException` already covers "not found".

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task RemoveDataAsync_NonMessageDtoWithCorrelationIdProperty_RemovesEntry()
{
    var persistor = new InMemoryAggregatorPersistor("", "", "");
    var dto = new ThirdPartyDto { CorrelationId = Guid.NewGuid(), Payload = "x" };
    await persistor.InsertDataAsync(dto, "stream-A");

    await persistor.RemoveDataAsync("stream-A", dto.CorrelationId);

    Assert.Equal(0, await persistor.CountAsync("stream-A"));
}

private sealed class ThirdPartyDto
{
    public Guid CorrelationId { get; init; }
    public string Payload { get; init; } = "";
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~RemoveDataAsync_NonMessageDtoWithCorrelationIdProperty_RemovesEntry"
```

Expected: FAIL with `ConcurrencyException` because the `is Message` guard rejects the DTO.

- [ ] **Step 3: Implement the fix**

In `InMemoryAggregatorPersistor.cs`, add a static reflection-cache for the `CorrelationId` getter:

```csharp
private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, Guid?>> CorrelationIdAccessors = new();

private static Guid? GetCorrelationId(object data)
{
    if (data is Message message)
        return message.CorrelationId;

    var accessor = CorrelationIdAccessors.GetOrAdd(data.GetType(), static type =>
    {
        var prop = type.GetProperty("CorrelationId");
        if (prop is null || prop.PropertyType != typeof(Guid))
            return _ => null;
        return obj => (Guid?)prop.GetValue(obj);
    });
    return accessor(data);
}
```

Replace lines 104-112 (the `for` loop body) with:

```csharp
for (var index = 0; index < list.Count; index++)
{
    if (GetCorrelationId(list[index].Data) is { } id && id == correlationId)
    {
        list.RemoveAt(index);
        removed = true;
        break;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Run full InMemoryAggregatorPersistor suite**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~InMemoryAggregatorPersistorTests"
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/InMemoryAggregatorPersistor.cs \
        src/ServiceConnect.UnitTests/InMemory/InMemoryAggregatorPersistorTests.cs
git commit -m "fix(inmemory): match aggregator entries by duck-typed CorrelationId, not 'is Message' (M-18)

Third-party DTOs that expose a Guid CorrelationId property are now removed
correctly. Mongo persistor already supports this; InMemory diverged."
```

---

## Task 8: M-19 — `InMemoryTimeoutStore` has no batch-size cap

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs:59-93`
- Modify (interface re-check): `src/ServiceConnect.Interfaces/Persistence/ITimeoutStore.cs` — confirm `GetTimeoutsBatchAsync` signature accepts a `batchSize` (or equivalent) parameter. If not, add it as an optional parameter to the contract; if so, just plumb it through.
- Test: `src/ServiceConnect.UnitTests/InMemory/InMemoryTimeoutStoreTests.cs`

**Bug:** Mongo persistor applies a caller-supplied batch cap; InMemory ignores any cap and returns all due timeouts. Contract-parity issue.

**Fix:** Honour the cap. If the contract does not currently expose one, add an optional `int? batchSize = null` parameter to `GetTimeoutsBatchAsync` and respect it in both persistors (Mongo already has internal logic that just needs to read the new param). Default `null` keeps existing callers unaffected.

- [ ] **Step 1: Confirm interface signature**

Read `src/ServiceConnect.Interfaces/Persistence/ITimeoutStore.cs` for the `GetTimeoutsBatchAsync` declaration. Note whether `batchSize` already exists. The Mongo cap location is documented in the strategy spec at section 5.2 ("Phase 6 (M-19 batch cap, M-21 Version bump)") — confirm it by reading `MongoDbTimeoutStore.cs` `GetTimeoutsBatchAsync` for any `Limit(...)` call.

- [ ] **Step 2: Write the failing test**

```csharp
[Fact]
public async Task GetTimeoutsBatchAsync_WithBatchSize_HonoursCap()
{
    var store = new InMemoryTimeoutStore("", "");
    for (int i = 0; i < 50; i++)
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = DateTimeOffset.UtcNow.AddSeconds(-1),
            Headers = new Dictionary<string, object>(),
        }, CancellationToken.None);

    var batch = await store.GetTimeoutsBatchAsync(batchSize: 10);

    Assert.Equal(10, batch.DueTimeouts.Count);
}
```

- [ ] **Step 3: Run test to verify it fails**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~GetTimeoutsBatchAsync_WithBatchSize_HonoursCap"
```

Expected: FAIL — either compile error (no `batchSize` parameter) or `batch.DueTimeouts.Count == 50`.

- [ ] **Step 4: Implement the fix**

If the interface lacks the parameter, add it:

```csharp
// ITimeoutStore.cs
Task<TimeoutsBatch> GetTimeoutsBatchAsync(int? batchSize = null, CancellationToken cancellationToken = default);
```

In `InMemoryTimeoutStore.cs:59-93`, plumb the cap through the loop:

```csharp
public Task<TimeoutsBatch> GetTimeoutsBatchAsync(int? batchSize = null, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();

    var retval = new TimeoutsBatch { DueTimeouts = [] };
    DateTimeOffset utcNow = _timeProvider.GetUtcNow();
    var sessionId = Guid.NewGuid();

    _state.SyncRoot.EnterWriteLock();
    try
    {
        foreach (var entry in _state.TimeoutIndex)
        {
            if (entry.Time > utcNow)
                break;

            if (!entry.Data.Locked || entry.Data.LockExpiresAt <= utcNow)
            {
                entry.Data.Locked = true;
                entry.Data.LockedBy = sessionId;
                entry.Data.LockExpiresAt = utcNow + LockLeaseDuration;
                retval.DueTimeouts.Add(Clone(entry.Data));

                if (batchSize is { } cap && retval.DueTimeouts.Count >= cap)
                    break;
            }
        }
    }
    finally
    {
        _state.SyncRoot.ExitWriteLock();
    }

    return Task.FromResult(retval);
}
```

In `MongoDbTimeoutStore.cs`, ensure `GetTimeoutsBatchAsync` accepts the new optional param and respects it via a `Limit(cap)` on the query (it likely already does — read the source and confirm).

- [ ] **Step 5: Run test to verify it passes**

Same command as Step 3. Expected: PASS.

- [ ] **Step 6: Run full TimeoutStore suites (both persistors)**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~TimeoutStore"
sg docker -c "dotnet test src/ServiceConnect.Persistence.MongoDb.IntegrationTests/ServiceConnect.Persistence.MongoDb.IntegrationTests.csproj --filter 'FullyQualifiedName~TimeoutStore'"
```

Expected: all pass.

- [ ] **Step 7: Update parity matrix at bottom of `consolodated-issues/2026-04-24-consolidated-issues.md`**

Per strategy spec section 5.2: update the "TimeoutStore batch cap" matrix row from "Divergent" to "Aligned". Stage the file, do not commit yet — commit alongside the source fix.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs \
        src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs \
        src/ServiceConnect.Interfaces/Persistence/ITimeoutStore.cs \
        src/ServiceConnect.UnitTests/InMemory/InMemoryTimeoutStoreTests.cs \
        consolodated-issues/2026-04-24-consolidated-issues.md
git commit -m "fix(timeoutstore): honour batchSize cap in InMemory persistor; align contract (M-19)

InMemory previously returned all due timeouts irrespective of caller cap.
Adds optional batchSize parameter to ITimeoutStore.GetTimeoutsBatchAsync;
both persistors now respect it. Updates parity matrix."
```

---

## Task 9: M-20 — `InMemoryTimeoutStore` shallow header clone for non-`byte[]` values

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs:110-117` (`CloneHeaderValue`)
- Test: `src/ServiceConnect.UnitTests/InMemory/InMemoryTimeoutStoreTests.cs`

**Bug:** `CloneHeaderValue` only clones `byte[]`. Reference-type values (`List<byte>`, custom DTOs) are shared by reference — caller mutations leak into stored state.

**Fix:** Use the project's existing `DeepClone.Clone(...)` helper for non-`byte[]` reference types. Strings and value-types pass through unchanged (DeepClone is a no-op on those).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task InsertTimeoutAsync_HeaderValueIsList_CallerMutationDoesNotLeakIntoStore()
{
    var store = new InMemoryTimeoutStore("", "");
    var sharedList = new List<byte> { 1, 2, 3 };
    var id = Guid.NewGuid();
    await store.InsertTimeoutAsync(new TimeoutData
    {
        Id = id,
        Destination = "d",
        ProcessManagerId = Guid.NewGuid(),
        Time = DateTimeOffset.UtcNow.AddSeconds(-1),
        Headers = new Dictionary<string, object> { ["custom"] = sharedList },
    }, CancellationToken.None);

    sharedList.Add(99); // mutate after insert

    var batch = await store.GetTimeoutsBatchAsync();
    var stored = (List<byte>)batch.DueTimeouts.Single(t => t.Id == id).Headers["custom"];
    Assert.Equal(new byte[] { 1, 2, 3 }, stored);
}
```

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~InsertTimeoutAsync_HeaderValueIsList_CallerMutationDoesNotLeakIntoStore"
```

Expected: FAIL — stored value contains `{1,2,3,99}`.

- [ ] **Step 3: Implement the fix**

Replace `CloneHeaderValue` (lines 110-117) with:

```csharp
private static object CloneHeaderValue(object value)
{
    return value switch
    {
        byte[] bytes => (byte[])bytes.Clone(),
        // Strings and value types are immutable / pass-by-value — return as-is.
        string or ValueType => value,
        // Any other reference type: deep-clone to prevent caller mutations leaking
        // into stored snapshot. Mirror the deep-clone behaviour the aggregator
        // persistor uses on Insert/Get for the same reason.
        _ => DeepClone.Clone(value),
    };
}
```

(Confirm `DeepClone` is in scope. It is in `ServiceConnect.Persistence.InMemory` namespace already — used by `InMemoryAggregatorPersistor`. No `using` change needed.)

- [ ] **Step 4: Run test to verify it passes**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Run full InMemoryTimeoutStore suite**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~InMemoryTimeoutStoreTests"
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs \
        src/ServiceConnect.UnitTests/InMemory/InMemoryTimeoutStoreTests.cs
git commit -m "fix(inmemory): deep-clone non-byte[] header values on timeout insert (M-20)

CloneHeaderValue now deep-clones reference-type header values (e.g.,
List<byte>) instead of sharing them by reference. Strings and value-types
still pass through unchanged."
```

---

## Task 10: M-21 — `InMemoryProcessManagerFinder.UpdateDataAsync` doesn't bump caller's `Version`

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs:207-256`
- Test: `src/ServiceConnect.UnitTests/InMemory/InMemoryProcessManagerFinderTests.cs`

**Bug:** `Update` writes `newData.Version + 1` to the store but the caller's `MemoryData<T>` instance still holds the old version. A subsequent `UpdateDataAsync(sameInstance)` raises `ConcurrencyException` because the caller's stale `Version` mismatches the store. Mongo persistor returns the new version via `IFindOneAndUpdateOptions.ReturnDocument` semantics — InMemory diverges.

**Fix:** Mutate the caller's `MemoryData<T>.Version` after a successful update. Since `MemoryData<T>.Version` is `init`-only, expose an `internal void IncrementVersion()` on `MemoryData<T>` (or use unsafe field write) — read the type first.

- [ ] **Step 1: Inspect `MemoryData<T>` shape**

Read `src/ServiceConnect.Persistence.InMemory/MemoryData.cs` (or wherever it lives). Note whether `Version` is `init` or `set`. If `init`, add an `internal void IncrementVersion() => Version++;` — but `Version++` won't work on `init`. Either change to `set` (preferred, internal-only) or expose `internal void SetVersion(int v) { /* unsafe direct backing-field write */ }`. Cleanest is `internal int Version { get; internal set; }` — keeps the `init` story for external callers via the implementing interface and lets the persistor mutate.

If a cleaner contract exists (e.g., `IPersistenceData<T>` has a method), prefer that.

- [ ] **Step 2: Write the failing test**

```csharp
[Fact]
public async Task UpdateDataAsync_BumpsCallerVersion_AllowsConsecutiveUpdates()
{
    var finder = new InMemoryProcessManagerFinder("", "");
    var data = new TestProcessManagerData { CorrelationId = Guid.NewGuid(), Counter = 0 };
    await finder.InsertDataAsync(data);

    var mapper = TestMapper(); // existing helper
    var found = await finder.FindDataAsync<TestProcessManagerData>(mapper, new TriggerMessage(data.CorrelationId));
    Assert.NotNull(found);

    found!.Data.Counter = 1;
    await finder.UpdateDataAsync(found);

    found.Data.Counter = 2;
    await finder.UpdateDataAsync(found); // must not throw ConcurrencyException
}
```

- [ ] **Step 3: Run test to verify it fails**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~UpdateDataAsync_BumpsCallerVersion_AllowsConsecutiveUpdates"
```

Expected: FAIL with `ConcurrencyException` on the second update — caller's Version is stale.

- [ ] **Step 4: Implement the fix in InMemoryProcessManagerFinder.cs**

In `UpdateDataAsync<T>` (lines 207-256), after the `_state.Provider.Update(...)` call, bump the caller's `Version`:

```csharp
_state.Provider.Update(key, new MemoryData<T>
{
    Data = DeepClone.Clone(data.Data),
    Version = newData.Version + 1
});

// Reflect the store-side increment back to the caller so consecutive updates
// using the same MemoryData<T> instance don't fail concurrency check. Mongo
// persistor returns the post-update document via FindOneAndUpdate; InMemory
// previously diverged.
newData.Version = newData.Version + 1;
```

If `MemoryData<T>.Version` is `init`-only, change the property declaration in `MemoryData.cs` to `internal set;`:

```csharp
public int Version { get; internal set; }
```

(Keeps external read-only contract; persistors in the same assembly can mutate.)

- [ ] **Step 5: Run test to verify it passes**

Same command as Step 3. Expected: PASS.

- [ ] **Step 6: Run full ProcessManagerFinder suites (both persistors)**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~ProcessManagerFinder"
sg docker -c "dotnet test src/ServiceConnect.Persistence.MongoDb.IntegrationTests/ServiceConnect.Persistence.MongoDb.IntegrationTests.csproj --filter 'FullyQualifiedName~ProcessManagerFinder'"
```

Expected: all pass.

- [ ] **Step 7: Update parity matrix**

Per strategy spec 5.2: flip "ProcessManagerFinder Version write-back" matrix row from "Divergent" to "Aligned". Stage the file.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs \
        src/ServiceConnect.Persistence.InMemory/MemoryData.cs \
        src/ServiceConnect.UnitTests/InMemory/InMemoryProcessManagerFinderTests.cs \
        consolodated-issues/2026-04-24-consolidated-issues.md
git commit -m "fix(inmemory): bump caller's Version after UpdateDataAsync (M-21)

Reflects the post-update Version back onto the caller's MemoryData<T>
instance so consecutive updates with the same handle don't trip
ConcurrencyException. Aligns with Mongo persistor's FindOneAndUpdate
return-the-new-doc semantics. Updates parity matrix."
```

---

## Task 11: M-22 — Persistors bypass `EnsureGuidSerializerRegistered` when ctor'd directly

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs` (add static ctor)
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs` (add static ctor)
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs` (add static ctor)
- Test: `src/ServiceConnect.Persistence.MongoDb.IntegrationTests/GuidSerializerRegistrationTests.cs` (new file)

**Bug:** `EnsureGuidSerializerRegistered` is invoked from DI factories only. A test or custom-composition `new MongoDbAggregatorPersistor(...)` leaves `Guid` serialization on the driver default (binary, subtype 3), incompatible with the canonical serializer the rest of the stack expects.

**Fix:** Add `static MongoDbAggregatorPersistor() => MongoDbGuidSerializerRegistrar.EnsureGuidSerializerRegistered();` (and equivalents in the other two persistors). Static ctor runs once per AppDomain on first use of the type — covers both DI and direct-`new` paths.

- [ ] **Step 1: Write the failing test**

```csharp
public class GuidSerializerRegistrationTests
{
    [Fact]
    public void DirectCtorOnEachPersistor_RegistersGuidSerializer()
    {
        // Use reflection on each type to *force* its static ctor to run, then
        // assert the canonical serializer is registered. We don't construct
        // instances because that would need a connection string and is tangential.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(MongoDbAggregatorPersistor).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(MongoDbProcessManagerFinder).TypeHandle);
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(MongoDbTimeoutStore).TypeHandle);

        var registered = MongoDB.Bson.Serialization.BsonSerializer.LookupSerializer<Guid>();
        Assert.IsType<MongoDB.Bson.Serialization.Serializers.GuidSerializer>(registered);
        Assert.Equal(MongoDB.Bson.GuidRepresentation.Standard, ((MongoDB.Bson.Serialization.Serializers.GuidSerializer)registered).GuidRepresentation);
    }
}
```

(Adapt the asserted serializer / representation to whatever `EnsureGuidSerializerRegistered` actually registers — read it first. The point is "static ctor runs and the canonical Guid serializer is in the registry".)

The test must run **before any other test in the assembly** binds the default — use `[Collection("MongoSerializerStaticCtors")]` and put nothing else in that collection, OR run it via a dedicated `[CollectionDefinition(DisableParallelization = true)]` collection. If the integration test base already touches Mongo, this test belongs in a unit-test project that does not.

If the existing tests already assert canonical Guid serialization is set up, this test still serves as a regression guard against a future refactor that removes the static ctors.

- [ ] **Step 2: Run test to verify it fails**

```
dotnet test src/ServiceConnect.Persistence.MongoDb.IntegrationTests/ServiceConnect.Persistence.MongoDb.IntegrationTests.csproj \
    --filter "FullyQualifiedName~DirectCtorOnEachPersistor_RegistersGuidSerializer"
```

Expected: FAIL — driver default is in registry, not canonical.

- [ ] **Step 3: Implement the fix in each persistor**

Add to each of the three classes (just below the field declarations):

```csharp
static MongoDbAggregatorPersistor()
{
    // Ensure the canonical Guid serializer is registered before any direct-ctor
    // path serialises a Guid. DI factories also call this; the static ctor covers
    // tests and custom compositions that bypass DI.
    MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
}
```

(Confirmed registrar location: `MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered()` — same call already used by `MongoClientFactory.cs:35`.)

Repeat for `MongoDbProcessManagerFinder` and `MongoDbTimeoutStore`.

- [ ] **Step 4: Run test to verify it passes**

Same command as Step 2. Expected: PASS.

- [ ] **Step 5: Run full Mongo integration suite**

```
sg docker -c "dotnet test src/ServiceConnect.Persistence.MongoDb.IntegrationTests/ServiceConnect.Persistence.MongoDb.IntegrationTests.csproj"
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/MongoDbAggregatorPersistor.cs \
        src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs \
        src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs \
        src/ServiceConnect.Persistence.MongoDb.IntegrationTests/GuidSerializerRegistrationTests.cs
git commit -m "fix(mongo): register Guid serializer from each persistor's static ctor (M-22)

Direct-ctor paths (tests, custom compositions) previously bypassed the
DI-factory call to EnsureGuidSerializerRegistered, leaving Guid
serialisation on the driver default. Static ctors run once per AppDomain
on first use of the type and cover all construction paths."
```

---

## Task 12: Phase 6a closeout — verification + tracker + strategy spec

**Files:**
- Modify: `consolodated-issues/2026-04-24-consolidated-issues.md`
- Modify: `docs/superpowers/specs/2026-04-24-consolidated-issues-remediation-strategy.md`

**No source changes in this task** — verification gates plus tracker/spec status updates.

- [ ] **Step 1: V.1 — Build clean**

```
dotnet build src/ServiceConnect.sln
```

Expected: zero errors, zero warnings.

- [ ] **Step 2: V.2 — Unit tests**

```
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    -e xUnit.ParallelizeAssembly=false -e xUnit.ParallelizeTestCollections=false
```

Expected: all pass. (Sequential execution suppresses the known pre-existing `StreamProcessorTests.ProcessAsync_ConcurrentFinalPacketDeliveries_DispatchesHandlerOnce` flake — see Phase 5 closeout note in the strategy spec.)

- [ ] **Step 3: V.3 — Integration / e2e (Docker via `sg`)**

```
sg docker -c "dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj"
sg docker -c "dotnet test src/ServiceConnect.Persistence.MongoDb.IntegrationTests/ServiceConnect.Persistence.MongoDb.IntegrationTests.csproj"
sg docker -c "dotnet test src/ServiceConnect.Client.RabbitMQ.IntegrationTests/ServiceConnect.Client.RabbitMQ.IntegrationTests.csproj"
```

Expected: all pass.

- [ ] **Step 4: V.4 — Sample apps build (smoke)**

```
dotnet build samples/
```

Expected: no errors.

- [ ] **Step 5: V.5 — Public-API audit**

```
git diff master..HEAD -- 'src/ServiceConnect.Interfaces/**/*.cs' \
                         'src/ServiceConnect/Bus.cs' \
                         'src/ServiceConnect/Configuration/**/*.cs'
```

Expected: only the `ITimeoutStore.GetTimeoutsBatchAsync` overload (M-19) and any minimal `MemoryData<T>.Version` setter visibility change (M-21) appear. No accidental surface changes from M-13's `_disposed` field.

- [ ] **Step 6: V.6 — Comment-style audit**

```
grep -nrE "M-0[1-9]|M-1[0-9]|M-2[0-9]|fixes M-|fix M-|ticket M-" \
    src/ServiceConnect/ src/ServiceConnect.Persistence.InMemory/ \
    src/ServiceConnect.Persistence.MongoDb/ src/ServiceConnect.Client.RabbitMQ/
```

Expected: no matches. Per RTK rule, no ticket IDs / "fixes Xxx" in code or test comments.

- [ ] **Step 7: V.7 — Phase 0 contract drift check**

Confirm no Task 1–11 commit re-touched `IFilter`/`IFilterPipeline`/`FilterAction` (Phase 5 surface):

```
git log master..HEAD --oneline -- src/ServiceConnect.Interfaces/Filters/
```

Expected: empty (Phase 5 commits are pre-master-merge baseline).

- [ ] **Step 8: Update tracker — `consolodated-issues/2026-04-24-consolidated-issues.md`**

For each of the 11 actionable items, change the line ending `- **Status**: TBD` (or absence of status line) to `- **Status**: fixed in <SHA>` using the SHA of the corresponding task's commit. For the 4 retroactive items, add status lines:

- M-02 — `**Status**: not-a-bug-at-HEAD (immutable record + TryUpdate CAS via 'with' expression eliminates torn read)`
- M-03 — `**Status**: not-a-bug-at-HEAD (Interlocked.Add reservation rolls back on overshoot or duplicate TryAdd)`
- M-04 — `**Status**: not-a-bug-at-HEAD (IsOpen guard at Consumer.cs:148 prevents CloseAsync on faulted channel)`
- M-08 — `**Status**: not-a-bug-at-HEAD (ObjectDisposedException already caught at line 547; ArgumentOutOfRangeException only on non-monotonic clock)`

Also update the counts table at the top of the file:
- Medium Fixed: previous-N → previous-N + 11
- Total Fixed: previous-N → previous-N + 11
- Confirmed-but-retroactive count (if the table tracks it): bump by 4

Read the current counts before editing. Use exact arithmetic.

- [ ] **Step 9: Update strategy spec — `docs/superpowers/specs/2026-04-24-consolidated-issues-remediation-strategy.md`**

At line 236 (`- Phase 6a: not started`), change to:

```
- Phase 6a: complete (11 items, retroactive 4, commits <SHA-task-1> <SHA-task-2> <SHA-task-3> <SHA-task-4> <SHA-task-5> <SHA-task-6> <SHA-task-7> <SHA-task-8> <SHA-task-9> <SHA-task-10> <SHA-task-11>)
```

(Use actual SHAs from `git log --oneline master..HEAD` after Tasks 1–11 are committed.)

If section 5.2 (parity matrix) references M-19 / M-21 status, confirm those rows are flipped to "Aligned" — Tasks 8 and 10 already staged the change, but verify it survived.

- [ ] **Step 10: Commit closeout**

```bash
git add consolodated-issues/2026-04-24-consolidated-issues.md \
        docs/superpowers/specs/2026-04-24-consolidated-issues-remediation-strategy.md
git commit -m "docs(tracker,strategy): close Phase 6a (11 fixed, 4 retroactive)

Updates tracker counts and per-item statuses; flips Phase 6a status in
the strategy spec to complete with the 11 task SHAs and 4 retroactive
not-a-bug-at-HEAD entries (M-02, M-03, M-04, M-08)."
```

---

## End-of-plan reminders

- One commit per task; no `--no-verify`, no amend, no `Co-Authored-By:` trailer.
- Comments in source/tests must follow the RTK rule: describe implementation and *why*; never include ticket IDs or "fixes Xxx".
- Use `sg docker -c '...'` for any docker / Testcontainers invocation.
- If a task's TDD red-green flow surfaces a deeper issue than the plan accounts for, escalate (status `BLOCKED`) rather than expand scope inline.
- Subagent dispatch model selection per strategy spec section 5.6: tasks 2, 9, 11 are mechanical (sonnet); tasks 1, 4, 5, 8, 10 are integration/judgment (sonnet); tasks 3, 6, 7 plus 12-closeout are higher-judgment (opus).
