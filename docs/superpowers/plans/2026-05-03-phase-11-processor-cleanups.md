# Phase 11 — Processor cleanups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sweep through `AggregatorProcessor` (already mostly fixed), `StreamProcessor`, `HandlerProcessor`, `ProcessManagerTimeoutService`, plus `ConsumeScopeAccessor`, `ConsumeContext`, `MessageBusReadStream`, `MessageDispatcher`, `MessageTypeExchangeName`. Close the remaining concurrency races, contract ambiguities, and one synchronous-throttle bug. v8 (major) release; one breaking API change shipped (`ExceptionHandler` signature) plus one deployment-visible change (exchange name hash drops assembly version).

**Architecture:** Two threads:
1. **Concurrency-correctness fixes** (M16, M23, M24, M25, M26) — discrete races, each a small surface change + MRES-gated concurrency test that fails pre-fix.
2. **Contract / API changes** (M21, M27, slip pair) — XML doc updates + interface signature changes where applicable + behavioral test pinning the new contract.

No new abstractions, no new files in production code. All fixes land in existing files. New test files per concurrency-test affinity.

**Tech Stack:** .NET multi-target net8.0/net10.0, LangVersion=14, xUnit + Moq, Testcontainers MongoDB for the M21 Mongo regression test, Astro/Starlight for docs.

**Spec:** [`docs/superpowers/specs/2026-05-03-phase-11-processor-cleanups-design.md`](../specs/2026-05-03-phase-11-processor-cleanups-design.md).

---

## Build/test safety

`~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup (8 cores / 8 GiB / 200 tasks). Always per-csproj with `-m:1`. Test filter for this phase:

```
--filter "FullyQualifiedName~Aggregator|Stream|Handler|ProcessManagerTimeout|ConsumeContext|ConsumeScope|MessageDispatcher|MessageBusReadStream|MessageTypeExchangeName"
```

---

## File structure

### Modified — production code

- `src/ServiceConnect.Interfaces/ProcessManagers/IProcessManagerFinder.cs` — Task 3 (XML doc).
- `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs:41` — Task 4 (signature change).
- `src/ServiceConnect/Configuration/BusConfiguration.cs:21` — Task 4 (signature change).
- `src/ServiceConnect/Services/MessageDispatcher.cs:188` — Task 4 (await + cancellation token).
- `src/ServiceConnect/Services/ConsumeScopeAccessor.cs:13` — Task 5 (static → instance).
- `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:120-136` — Task 6 (post-send lease check).
- `src/ServiceConnect/Services/MessageBusReadStream.cs:31-59` — Task 7 (validate-inside-CAS).
- `src/ServiceConnect/Services/Processors/StreamProcessor.cs:304-307` — Task 8 (dispose flag + late-packet guard).
- `src/ServiceConnect/Services/Processors/StreamProcessor.cs:120-145` — Task 9 (pre-increment counter).
- `src/ServiceConnect/Services/MessageBusReadStream.cs:62-103` and `:106-123` — Task 10 (null-guard, IsComplete re-check).
- `src/ServiceConnect/Services/ConsumeContext.cs:66-68` — Task 11 (volatile publication).
- `src/ServiceConnect/Services/MessageTypeExchangeName.cs:43` — Task 12 (drop AssemblyQualifiedName).
- `src/ServiceConnect/Services/Processors/HandlerProcessor.cs:181-186` — Task 13 (replace IsKnownQueue with format check).

### Created — tests

- `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderFreshDataTests.cs` — Task 3.
- `src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderFreshDataTests.cs` — Task 3.
- `src/ServiceConnect.UnitTests/Configuration/AsyncExceptionHandlerTests.cs` — Task 4.
- `src/ServiceConnect.UnitTests/Services/ConsumeScopeAccessorPerInstanceTests.cs` — Task 5.
- `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceLeaseGuardTests.cs` — Task 6.
- `src/ServiceConnect.UnitTests/Services/MessageBusReadStreamCasRaceTests.cs` — Task 7.
- `src/ServiceConnect.UnitTests/Processors/StreamProcessorDisposeTests.cs` — Task 8.
- `src/ServiceConnect.UnitTests/Processors/StreamProcessorAdmissionCapTests.cs` — Task 9.
- `src/ServiceConnect.UnitTests/Services/MessageBusReadStreamGuardTests.cs` — Task 10.
- `src/ServiceConnect.UnitTests/Services/ConsumeContextVolatileTests.cs` — Task 11.
- `src/ServiceConnect.UnitTests/Services/MessageTypeExchangeNameVersionStableTests.cs` — Task 12.
- `src/ServiceConnect.UnitTests/Processors/HandlerProcessorRoutingSlipTests.cs` — Task 13.

### Modified — website

- `website/src/content/docs/releases.mdx` — Task 14.
- `website/src/content/docs/reference/extension-points/exception-handler.mdx` (or equivalent) — Task 15.
- `website/src/content/docs/reference/process-managers/...` — Task 15.
- `website/src/content/docs/reference/handlers/...` — Task 15.
- `website/src/content/docs/learn/messaging-patterns/routing-slip.mdx` (or equivalent) — Task 15.

### Modified — examples

- `examples/Aggregator/README.md` — Task 16.
- `examples/ProcessManager/README.md` — Task 16.
- `examples/Streaming/README.md` — Task 16.

---

## Task 1: Spec

**Already shipped at commit `e77272a2`** (`docs(spec): phase 11 processor cleanups`). Skip.

---

## Task 2: This plan

```bash
git add docs/superpowers/plans/2026-05-03-phase-11-processor-cleanups.md
git commit -m "$(cat <<'EOF'
docs(plan): phase 11 implementation plan

17 tasks. v8 breaking change: IBusConfiguration.ExceptionHandler signature.
Deployment-visible: MessageTypeExchangeName drops AssemblyQualifiedName from
hash (existing exchanges need migration).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: M21 — `IProcessManagerFinder.FindDataAsync<T>` fresh-copy contract

**Files:**
- Modify: `src/ServiceConnect.Interfaces/ProcessManagers/IProcessManagerFinder.cs:8-16`.
- Create: `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderFreshDataTests.cs`.
- Create: `src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderFreshDataTests.cs`.

**Background.** `ProcessManagerProcessor.UpdateData` (line 135) re-reads the persisted row on retry. If the persistor caches the row instance, a handler that mutated `Data` then threw will leak partial state into the retry.

Both built-in persistors already comply: InMemory's `FindMatchingItem<T>` clones via `DeepClone.Clone` (Phase 10); Mongo's BSON deserialization produces a fresh CLR object per query. Document the contract and pin it with regression tests.

- [ ] **Step 1: Update XML doc on the interface**

In `src/ServiceConnect.Interfaces/ProcessManagers/IProcessManagerFinder.cs`, replace the `FindDataAsync` doc block:

```csharp
/// <summary>
/// Finds the persisted process-manager state that matches an incoming message.
/// </summary>
/// <typeparam name="T">The process-manager data type.</typeparam>
/// <param name="mapper">The property mapper describing correlation rules.</param>
/// <param name="message">The incoming message.</param>
/// <param name="cancellationToken">A token that cancels the operation.</param>
/// <returns>The persisted data wrapper, or <see langword="null"/> when no match exists.</returns>
/// <remarks>
/// <para>
/// <b>Fresh-copy contract.</b> The returned <see cref="IPersistenceData{T}.Data"/> reference
/// MUST be a fresh copy per call, independent of any cached storage. Callers (notably
/// <c>ProcessManagerProcessor.UpdateData</c>) freely mutate <c>Data</c> in handler scope;
/// the persistence layer must guarantee that a subsequent <see cref="FindDataAsync"/>
/// invocation observes the previously-stored state, not the in-flight mutation. Implementors
/// that cache rows internally MUST clone (or otherwise materialise a fresh graph) before returning.
/// </para>
/// <para>
/// Built-in implementations comply: <c>InMemoryProcessManagerFinder</c> deep-clones via
/// <c>DeepClone.Clone</c>; <c>MongoDbProcessManagerFinder</c> relies on BSON deserialization
/// to produce a fresh CLR object per query.
/// </para>
/// </remarks>
Task<IPersistenceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
```

- [ ] **Step 2: Write the InMemory regression test**

Create `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderFreshDataTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using ServiceConnect.UnitTests.Helpers;  // existing helpers folder for TestProcessManagerData / mapper
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryProcessManagerFinderFreshDataTests
{
    [Fact]
    public async Task FindDataAsync_ReturnsFreshDataReferencePerCall()
    {
        var state = new InMemoryPersistenceState(new FakeTimeProvider());
        var finder = new InMemoryProcessManagerFinder(new ProcessManagerPredicateCache(), state);

        var correlationId = Guid.NewGuid();
        var data = new TestProcessManagerData { CorrelationId = correlationId, Name = "n" };
        await finder.InsertDataAsync(data, CancellationToken.None);

        var mapper = TestProcessManagerPropertyMapper.ForCorrelation();
        var msg = new TestMessage { Id = correlationId };

        var first = await finder.FindDataAsync<TestProcessManagerData>(mapper, msg, CancellationToken.None);
        var second = await finder.FindDataAsync<TestProcessManagerData>(mapper, msg, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(ReferenceEquals(first!.Data, second!.Data),
            "FindDataAsync must return a fresh Data instance per call so handler mutation can't leak across retries.");
    }
}
```

If `TestProcessManagerData`, `TestMessage`, or `TestProcessManagerPropertyMapper` aren't directly importable from existing tests, mirror the canonical shape from `src/ServiceConnect.UnitTests/InMemoryProcessManagerFinderTests.cs` (read it first to find the actual helper names — they may be `TestData`, `TestProcessManagerPropertyMapper`, etc.).

- [ ] **Step 3: Run the InMemory test pre-commit (verify it passes against the already-compliant impl)**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryProcessManagerFinderFreshDataTests" -m:1
```

Expected: 1/1 pass (this is a regression-pin, not a fix — the impl already complies).

- [ ] **Step 4: Write the Mongo E2E regression test**

Mongo's verification needs a real container (Phase 8/9 pattern). Create `src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderFreshDataTests.cs`. Mirror the construction shape from existing `MongoDbProcessManagerFinder*` E2E tests:

```csharp
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbProcessManagerFinderFreshDataTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task FindDataAsync_ReturnsFreshDataReferencePerCall()
    {
        var dbName = _fixture.GetUniqueDatabaseName("freshdata");
        // Build finder per the canonical pattern in MongoDbProcessManagerFinderConcurrencyE2ETests
        // ... insert a row, FindDataAsync twice, assert !ReferenceEquals(first.Data, second.Data).
    }
}
```

Read an existing Mongo E2E ProcessManager test (e.g. `MongoDbProcessManagerFinderConcurrencyE2ETests.cs`) for the canonical fixture / connection-string pattern. Copy that arrangement and substitute the assertion.

- [ ] **Step 5: Run the Mongo test (Docker required)**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj \
  --filter "FullyQualifiedName~MongoDbProcessManagerFinderFreshDataTests" -m:1
```

Expected: 1/1 pass (Mongo's BSON deserialization naturally produces fresh objects per query).

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Interfaces/ProcessManagers/IProcessManagerFinder.cs \
        src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderFreshDataTests.cs \
        src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderFreshDataTests.cs
git commit -m "$(cat <<'EOF'
docs(persistence): pin FindDataAsync<T> fresh-copy contract (M21)

ProcessManagerProcessor.UpdateData mutates IPersistenceData<T>.Data inside
handler scope. If the persistor caches the row, a handler throw + retry
leaks partial mutation. Both built-in persistors already comply (InMemory
clones via DeepClone, Mongo via BSON deserialize); document the contract
and pin per-persistor regression tests.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(No `!` — doc + tests only, no behavior change.)

---

## Task 4: M27 — `ExceptionHandler` async signature (breaking)

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs:41`.
- Modify: `src/ServiceConnect/Configuration/BusConfiguration.cs:21`.
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs:186-194`.
- Create: `src/ServiceConnect.UnitTests/Configuration/AsyncExceptionHandlerTests.cs`.

**Background.** Sync `Action<Exception>` blocks the dispatcher thread. v8 replaces with `Func<Exception, CancellationToken, ValueTask>?`. Migration shim: `(ex, _) => { Sync(ex); return ValueTask.CompletedTask; }`.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/Configuration/AsyncExceptionHandlerTests.cs`:

```csharp
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.Configuration;

public class AsyncExceptionHandlerTests
{
    [Fact]
    public async Task ExceptionHandler_AsyncSignature_AwaitsHandler()
    {
        var cfg = new BusConfiguration();
        var observed = new List<Exception>();
        cfg.ExceptionHandler = async (ex, ct) =>
        {
            await Task.Yield();
            observed.Add(ex);
        };

        // Act
        var failure = new InvalidOperationException("test-failure");
        await cfg.ExceptionHandler!.Invoke(failure, CancellationToken.None);

        Assert.Single(observed);
        Assert.Same(failure, observed[0]);
    }

    [Fact]
    public async Task ExceptionHandler_SyncShim_Compiles()
    {
        // Compile-time regression guard: the v7-style sync shim must remain valid for v8.
        var cfg = new BusConfiguration();
        cfg.ExceptionHandler = (ex, _) => { /* Log.Error(ex); */ return ValueTask.CompletedTask; };

        await cfg.ExceptionHandler!.Invoke(new Exception(), CancellationToken.None);
    }
}
```

- [ ] **Step 2: Run test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~AsyncExceptionHandlerTests" -m:1
```

Expected: compilation FAIL on `cfg.ExceptionHandler = async (ex, ct) => ...` because the property is currently `Action<Exception>?`.

- [ ] **Step 3: Update the interface**

In `src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs`, change line 41 from:

```csharp
Action<Exception>? ExceptionHandler { get; set; }
```

to:

```csharp
/// <summary>
/// Optional async hook invoked when message dispatch throws. Awaited by the dispatcher
/// before returning the failure result, so slow handlers no longer block the consumer
/// thread (v7 used <c>Action&lt;Exception&gt;</c> and was synchronous).
/// </summary>
/// <remarks>
/// The cancellation token is the dispatcher's shutdown CTS; honour it to avoid
/// stretching shutdown deadlines. If the handler itself throws, the dispatcher logs
/// at warning level and continues (a handler-thrown exception is not propagated).
/// Migration from v7: wrap your <c>Action&lt;Exception&gt;</c> as
/// <c>(ex, _) =&gt; { Sync(ex); return ValueTask.CompletedTask; }</c>.
/// </remarks>
Func<Exception, CancellationToken, ValueTask>? ExceptionHandler { get; set; }
```

- [ ] **Step 4: Update the implementation property**

In `src/ServiceConnect/Configuration/BusConfiguration.cs:21`, change:

```csharp
public Action<Exception>? ExceptionHandler { get; set; }
```

to:

```csharp
public Func<Exception, CancellationToken, ValueTask>? ExceptionHandler { get; set; }
```

- [ ] **Step 5: Update the dispatcher invocation**

In `src/ServiceConnect/Services/MessageDispatcher.cs:186-194`, replace:

```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Error dispatching message of type {MessageType}", messageType);
    try
    {
        _config.ExceptionHandler?.Invoke(ex);
    }
    catch (Exception handlerEx)
    {
        _logger.LogWarning(handlerEx, "ExceptionHandler threw while handling dispatch error");
    }
    return new ConsumeEventResult { Success = false, Exception = ex };
}
```

with:

```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Error dispatching message of type {MessageType}", messageType);
    if (_config.ExceptionHandler is { } handler)
    {
        try
        {
            await handler(ex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception handlerEx)
        {
            _logger.LogWarning(handlerEx, "ExceptionHandler threw while handling dispatch error");
        }
    }
    return new ConsumeEventResult { Success = false, Exception = ex };
}
```

The `await` requires the enclosing method to be `async`. Verify by reading the surrounding method signature; it should already be `async Task<ConsumeEventResult>`. If not, the change requires more surgery — pause and surface.

- [ ] **Step 6: Build + run tests**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~AsyncExceptionHandlerTests|FullyQualifiedName~MessageDispatcher" -m:1
```

Expected: build clean (0 errors, 0 warnings); all pass.

If pre-existing tests use `cfg.ExceptionHandler = ex => ...` (sync lambda), they must migrate to `cfg.ExceptionHandler = (ex, _) => { ...; return ValueTask.CompletedTask; }`. Search and update as part of this commit:

```bash
grep -rn "ExceptionHandler\s*=\s*" src/ --include="*.cs" | grep -v "BusConfiguration\.cs\|IBusConfiguration\.cs"
```

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Interfaces/Configuration/IBusConfiguration.cs \
        src/ServiceConnect/Configuration/BusConfiguration.cs \
        src/ServiceConnect/Services/MessageDispatcher.cs \
        src/ServiceConnect.UnitTests/Configuration/AsyncExceptionHandlerTests.cs \
        # any pre-existing test files migrated to the async signature
git commit -m "$(cat <<'EOF'
feat(bus)!: ExceptionHandler async signature (M27)

Action<Exception> blocked the dispatcher thread synchronously and throttled
RabbitMQ prefetch when user code was slow. Replace with
Func<Exception, CancellationToken, ValueTask>; await it inside the catch.
Cancellation token is the dispatcher shutdown CTS so slow handlers honour
shutdown. Migration shim documented on the interface.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(`!` for breaking API change.)

---

## Task 5: M23 — `ConsumeScopeAccessor` static→instance

**Files:**
- Modify: `src/ServiceConnect/Services/ConsumeScopeAccessor.cs:13`.
- Create: `src/ServiceConnect.UnitTests/Services/ConsumeScopeAccessorPerInstanceTests.cs`.

**Background.** `private static readonly AsyncLocal<IServiceProvider?> _current` is shared across all `ConsumeScopeAccessor` instances in the AppDomain. Two `Bus` instances dispatching concurrently see each other's scopes. `ConsumeScopeAccessor` is registered as a DI singleton, so per-instance scoping has no ergonomic cost.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/Services/ConsumeScopeAccessorPerInstanceTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ConsumeScopeAccessorPerInstanceTests
{
    [Fact]
    public void TwoInstances_DoNotShareScope()
    {
        var accessorA = new ConsumeScopeAccessor();
        var accessorB = new ConsumeScopeAccessor();
        var providerA = new ServiceCollection().BuildServiceProvider();
        var providerB = new ServiceCollection().BuildServiceProvider();

        using (accessorA.Push(providerA))
        {
            // accessorB.Current must throw — its scope was not pushed.
            Assert.Throws<InvalidOperationException>(() => accessorB.Current);
            Assert.Same(providerA, accessorA.Current);
        }
    }

    [Fact]
    public async Task ConcurrentDispatches_AcrossInstances_DoNotBleed()
    {
        var accessorA = new ConsumeScopeAccessor();
        var accessorB = new ConsumeScopeAccessor();
        var providerA = new ServiceCollection().BuildServiceProvider();
        var providerB = new ServiceCollection().BuildServiceProvider();

        // Two concurrent flows, each pushing their own scope on their own accessor.
        var taskA = Task.Run(async () =>
        {
            using (accessorA.Push(providerA))
            {
                await Task.Yield();
                Assert.Same(providerA, accessorA.Current);
                Assert.Throws<InvalidOperationException>(() => accessorB.Current);
            }
        });
        var taskB = Task.Run(async () =>
        {
            using (accessorB.Push(providerB))
            {
                await Task.Yield();
                Assert.Same(providerB, accessorB.Current);
                Assert.Throws<InvalidOperationException>(() => accessorA.Current);
            }
        });

        await Task.WhenAll(taskA, taskB);
    }
}
```

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConsumeScopeAccessorPerInstanceTests" -m:1
```

Expected: `TwoInstances_DoNotShareScope` fails — `accessorB.Current` returns the static-shared `providerA` instead of throwing.

- [ ] **Step 3: Apply the fix**

In `src/ServiceConnect/Services/ConsumeScopeAccessor.cs`, change line 13 from:

```csharp
private static readonly AsyncLocal<IServiceProvider?> _current = new();
```

to:

```csharp
// Instance-scoped AsyncLocal so multiple ConsumeScopeAccessor instances in the same
// AppDomain (e.g. two Bus instances) maintain independent scopes. Pre-Phase-11 this
// was static-shared, leaking scopes across bus boundaries.
private readonly AsyncLocal<IServiceProvider?> _current = new();
```

The `Popper` inner class also references `_current.Value = previous;` at line 46 — that captures the outer instance's `_current` because `Popper` is non-static nested in the spec sense. Verify the IL by reading `Popper`: it's a `private sealed class` declared inside the outer class, so it uses an outer-`this` capture for `_current`. Should compile without modification, but if `Popper` is declared `sealed class Popper(IServiceProvider? previous)` (a primary-ctor-with-no-outer-capture pattern), it may need a back-reference field. Read the source first; pause and adapt if so.

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConsumeScopeAccessorPerInstanceTests|FullyQualifiedName~ConsumeScope" -m:1
```

Expected: 2/2 new pass. No regressions in pre-existing `ConsumeScope*` tests.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/ConsumeScopeAccessor.cs \
        src/ServiceConnect.UnitTests/Services/ConsumeScopeAccessorPerInstanceTests.cs
git commit -m "$(cat <<'EOF'
fix(bus): ConsumeScopeAccessor scopes are per-instance (M23)

Pre-fix the AsyncLocal<IServiceProvider?> was a static field, shared across
every ConsumeScopeAccessor instance in the AppDomain. Two Bus instances
dispatching concurrently saw each other's scopes. The class is registered
as a DI singleton so the field's lifetime is unchanged; per-instance
scoping has no ergonomic cost.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: M16 — `ProcessManagerTimeoutService` post-send lease check

**Files:**
- Modify: `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:118-136`.
- Create: `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceLeaseGuardTests.cs`.

**Background.** Phase 8 added a pre-send lease-margin check (line 107-114). After `SendAsync` returns, if it took long enough that the lease has expired, a peer poller may have already re-acquired and re-dispatched the timeout. Don't call `RemoveDispatchedTimeoutAsync` when the lease has expired — let Phase 8's lease-expiry sweep re-process the row on the next poll instead.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceLeaseGuardTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ProcessManagerTimeoutServiceLeaseGuardTests
{
    [Fact]
    public async Task PollOnceAsync_LeaseExpiredDuringSend_DoesNotCallRemove()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));
        var bus = new Mock<IBus>();
        var store = new Mock<ITimeoutStore>();
        var config = new Mock<IBusConfiguration>();
        config.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var lockOwner = Guid.NewGuid();
        var timeoutId = Guid.NewGuid();
        var dueTimeout = new TimeoutData
        {
            Id = timeoutId,
            ProcessManagerId = Guid.NewGuid(),
            Destination = "test-destination",
            Time = clock.GetUtcNow(),
            // Lease expires 10s after the test "now". SendAsync below advances the clock
            // by 30s so by the time SendAsync returns, the lease has expired.
            LockExpiresAt = clock.GetUtcNow() + TimeSpan.FromSeconds(10),
            LockedBy = lockOwner,
        };
        store.Setup(s => s.GetTimeoutsBatchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new TimeoutBatch { DueTimeouts = new List<TimeoutData> { dueTimeout } });

        // SendAsync advances the FakeTimeProvider by 30s, so the post-send lease check sees expiration.
        bus.Setup(b => b.SendAsync(It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()))
           .Callback(() => clock.Advance(TimeSpan.FromSeconds(30)))
           .Returns(Task.CompletedTask);

        var svc = new ProcessManagerTimeoutService(
            config.Object, new Lazy<IBus>(() => bus.Object), store.Object,
            NullLogger<ProcessManagerTimeoutService>.Instance, clock);

        await svc.PollOnceAsync();

        bus.Verify(b => b.SendAsync(It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
        store.Verify(s => s.RemoveDispatchedTimeoutAsync(timeoutId, lockOwner, It.IsAny<CancellationToken>()),
            Times.Never,
            "Lease expired during SendAsync; Remove must NOT be called — let the lease-expiry sweep reclaim the row.");
    }

    [Fact]
    public async Task PollOnceAsync_LeaseValidAfterSend_CallsRemove()
    {
        // Regression guard: the happy path still calls Remove when the lease is still valid post-send.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));
        var bus = new Mock<IBus>();
        var store = new Mock<ITimeoutStore>();
        var config = new Mock<IBusConfiguration>();
        config.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var lockOwner = Guid.NewGuid();
        var timeoutId = Guid.NewGuid();
        var dueTimeout = new TimeoutData
        {
            Id = timeoutId,
            ProcessManagerId = Guid.NewGuid(),
            Destination = "test-destination",
            LockExpiresAt = clock.GetUtcNow() + TimeSpan.FromMinutes(5), // plenty of margin
            LockedBy = lockOwner,
        };
        store.Setup(s => s.GetTimeoutsBatchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new TimeoutBatch { DueTimeouts = new List<TimeoutData> { dueTimeout } });
        bus.Setup(b => b.SendAsync(It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        var svc = new ProcessManagerTimeoutService(
            config.Object, new Lazy<IBus>(() => bus.Object), store.Object,
            NullLogger<ProcessManagerTimeoutService>.Instance, clock);

        await svc.PollOnceAsync();

        bus.Verify(b => b.SendAsync(It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
        store.Verify(s => s.RemoveDispatchedTimeoutAsync(timeoutId, lockOwner, It.IsAny<CancellationToken>()),
            Times.Once,
            "Lease still valid post-send; Remove is the expected at-least-once dispatch ack.");
    }
}
```

NOTE: adapt member names to the actual `TimeoutData` / `TimeoutBatch` types (read from `ITimeoutStore.cs`). The intent is the test arranges a scenario where `SendAsync` takes long enough that the lease expires before the post-send guard.

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProcessManagerTimeoutServiceLeaseGuardTests" -m:1
```

Expected: `LeaseExpiredDuringSend_DoesNotCallRemove` fails — `Remove` is currently called unconditionally after `SendAsync` returns.

- [ ] **Step 3: Apply the fix**

In `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs`, around line 118-136, restructure the dispatch+remove path:

```csharp
if (!string.IsNullOrEmpty(timeout.Destination))
{
    var timeoutMessage = new TimeoutMessage(timeout.ProcessManagerId);
    await _bus.Value.SendAsync(timeoutMessage, new SendOptions
    {
        EndPoint = timeout.Destination,
        Headers = TimeoutHeaderPersistence.BuildOutgoingHeaders(timeout.Headers, logger)
    }, cancellationToken).ConfigureAwait(false);
}

// Post-send lease check. SendAsync may have taken longer than the remaining lease;
// if so a peer poller may have already re-acquired and re-dispatched. Skip Remove
// and let the lease-expiry sweep reclaim the row on the next poll. The trade-off is
// a possible duplicate send (at-least-once timeout semantics, already documented),
// not a duplicate Remove racing a peer's lease reclaim.
if (timeout.LockExpiresAt.HasValue &&
    timeout.LockExpiresAt.Value <= _timeProvider.GetUtcNow())
{
    logger.LogWarning(
        "Lease for timeout {TimeoutId} expired during SendAsync (expires at {Expires}); skipping Remove. " +
        "Next poll will reclaim the row.",
        timeout.Id, timeout.LockExpiresAt.Value);
    continue;
}

// Pass the captured lease owner only when one is set — the store treats null
// as the unconditional id-only path and a non-null Guid as lease-checked.
Guid? lockOwner = timeout.LockedBy != Guid.Empty ? timeout.LockedBy : null;
// See learn/operations/cancellation: token propagation rule. StopAsync becomes
// bounded by the lifecycle token's deadline. A cancel-during-remove leaves the
// timeout "dispatched but not removed" — next poll redispatches, consistent with
// the existing at-least-once timeout semantics.
await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, lockOwner, cancellationToken).ConfigureAwait(false);
```

The `continue;` is inside the existing `foreach (var timeout in batch.DueTimeouts)` loop (line 100 — confirmed in the source); the post-send check skips this iteration's remove without breaking out of the loop.

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProcessManagerTimeout" -m:1
```

Expected: 2/2 new pass; no regressions in existing tests.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/ProcessManagerTimeoutService.cs \
        src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceLeaseGuardTests.cs
git commit -m "$(cat <<'EOF'
fix(timeout): skip Remove when lease expired during SendAsync (M16)

Phase 8 added a pre-send lease-margin check, but SendAsync itself can run
long enough that the lease expires before Remove fires. A peer poller
sweeping expired leases may then re-dispatch the same timeout before our
Remove lands. Add a post-send lease check; if expired, log and continue —
the lease-expiry sweep reclaims the row on the next poll. Trade-off: at
worst one duplicate send (already at-least-once timeout semantics).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: M24 — `MessageBusReadStream.SetLastPacketNumber` validate-inside-CAS

**Files:**
- Modify: `src/ServiceConnect/Services/MessageBusReadStream.cs:31-59`.
- Create: `src/ServiceConnect.UnitTests/Services/MessageBusReadStreamCasRaceTests.cs`.

**Background.** Current code does `Interlocked.CompareExchange` to set `_lastPacketNumber`, then iterates `_packets.Keys` to validate. A concurrent `Write` can insert a packet with `packetNumber > value` between the CAS and the validation — that packet survives the validation loop.

The fix: move the validation INSIDE the CAS retry loop. If the CAS races and loses, retry with a fresh validation against the updated state. If the CAS wins, the validation already happened against the state we're committing to.

Wait — re-reading the current code: the CAS goes from `-1` to `value`, so it never retries. The race is: `_packets.Keys` enumeration sees a snapshot AFTER the CAS, so a concurrent `Write` after the CAS but before the enumeration would have already validated against `lastPacketNumber` (because `Write` does `Volatile.Read(ref _lastPacketNumber)` and refuses `packetNumber > last`). So actually... is M24 a real bug, or did the verification audit miss this defense?

Re-read the audit:
> M24 — STILL REAL. SetLastPacketNumber iterates _packets.Keys (lines 50-58) after the CAS on _lastPacketNumber. A concurrent Write call can insert a new packet between the CAS and that loop, causing the iteration to miss (or falsely flag) the newly written packet. The ConcurrentDictionary enumeration is a snapshot but the check is not atomic with the CAS.

OK — the race is real because `Write` validates against the OLD `_lastPacketNumber` (still `-1` before the CAS) and adds the packet successfully, then the validation loop in `SetLastPacketNumber` iterates keys (a snapshot) but the snapshot taken AFTER the CAS may or may not see the racing-Write packet depending on insert ordering.

Actually the more careful framing: a `Write` that races `SetLastPacketNumber` may:
- If `Write` reads `_lastPacketNumber == -1` BEFORE the CAS commits: passes validation in `Write` (no upper bound), inserts packet `K`. If `K > value`, the post-CAS validation in `SetLastPacketNumber` may catch it (depending on enumeration timing).
- If `Write` reads `_lastPacketNumber == value` AFTER the CAS commits: validates `K <= value` in `Write` and rejects if `K > value`.

The window: `Write` reads `_lastPacketNumber` (still `-1`), inserts packet, returns. Meanwhile `SetLastPacketNumber` does its CAS and starts the validation loop. The validation loop's snapshot may include or exclude that insert. If excluded, the bad packet survives.

Fix: move the validation BEFORE the CAS, then attempt the CAS. If CAS fails because some other thread set `_lastPacketNumber` first, throw the existing `InvalidOperationException`. If CAS succeeds, do a SECOND validation pass on the now-committed value. If the second pass finds a packet > value, throw — the second pass is the one that catches concurrent Writes that landed during the first validation.

Actually the cleanest pattern: make the CAS ONLY commit when the validation passes, AND re-validate after the commit to catch the in-flight Write that was admitted just before our CAS. This requires either:
- A retry loop that re-validates after each failed CAS.
- A two-phase: validate, CAS, re-validate.

Given the current `Write` validates against `_lastPacketNumber` post-CAS (rejects if `packetNumber > last`), the Write that races us is the one whose validation read `_lastPacketNumber == -1` before our CAS. So the fix is: after the CAS commits, re-iterate the keys; if any > value, fail loudly.

```csharp
public void SetLastPacketNumber(long lastPacketNumber)
{
    if (lastPacketNumber < 0)
    {
        throw new ArgumentOutOfRangeException(nameof(lastPacketNumber));
    }

    // Pre-CAS validation: if any already-stored packet exceeds the value, fail before
    // committing the sentinel.
    foreach (var key in _packets.Keys)
    {
        if (key > lastPacketNumber)
        {
            throw new InvalidOperationException(
                $"Packet number {key} already received for stream {SequenceId} but exceeds " +
                $"the requested LastPacketNumber {lastPacketNumber}. The stream is inconsistent.");
        }
    }

    var previous = Interlocked.CompareExchange(ref _lastPacketNumber, lastPacketNumber, -1);
    if (previous != -1 && previous != lastPacketNumber)
    {
        throw new InvalidOperationException(
            $"LastPacketNumber already set to {previous}; refusing to overwrite with {lastPacketNumber} for stream {SequenceId}.");
    }

    // Post-CAS re-validation: a concurrent Write that read _lastPacketNumber == -1 may
    // have committed an out-of-range packet between our pre-validation and the CAS.
    // Now that _lastPacketNumber is published, any future Write will reject; but the
    // already-committed in-flight Write is still a violation we must surface.
    foreach (var key in _packets.Keys)
    {
        if (key > lastPacketNumber)
        {
            throw new InvalidOperationException(
                $"Packet number {key} arrived concurrently and exceeds LastPacketNumber {lastPacketNumber} for stream {SequenceId}. The stream is inconsistent.");
        }
    }
}
```

OK — this is the right shape. Let me write the test/fix steps.

- [ ] **Step 1: Write the concurrency test**

Create `src/ServiceConnect.UnitTests/Services/MessageBusReadStreamCasRaceTests.cs`:

```csharp
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class MessageBusReadStreamCasRaceTests
{
    [Fact]
    public void SetLastPacketNumber_RaceWithWrite_RejectsOutOfRangePacket()
    {
        // Drive the race directly: arrange Write(K, packetNumber=N+1) and
        // SetLastPacketNumber(N) such that Write commits its packet via TryAdd
        // BEFORE SetLastPacketNumber's CAS publishes N. The post-CAS re-validation
        // must catch the now-out-of-range packet.
        //
        // Determinism: don't rely on Task scheduling. Instead, exploit Write's
        // Volatile.Read-then-TryAdd shape: a single thread calls Write *before*
        // SetLastPacketNumber, then SetLastPacketNumber must observe the prior
        // packet during its post-CAS validation.

        var stream = new MessageBusReadStream("test-seq");
        // packet 5 lands first (when _lastPacketNumber == -1, no upper bound applied).
        stream.Write(new byte[] { 0xAA }, packetNumber: 5);

        // Now declare LastPacketNumber = 3. The pre-CAS validation must fire.
        var ex = Assert.Throws<InvalidOperationException>(() => stream.SetLastPacketNumber(3));
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public async Task SetLastPacketNumber_ConcurrentWrite_AllOutOfRangeDetected()
    {
        // True concurrency: 100 streams, each running Write(N+1) and SetLastPacketNumber(N)
        // in parallel. Either Write throws (caught and counted), or SetLastPacketNumber
        // throws (caught and counted). Both is OK; both succeeding silently is the bug.
        const int trials = 100;
        var bothSucceededSilently = 0;
        for (var i = 0; i < trials; i++)
        {
            var stream = new MessageBusReadStream($"seq-{i}");
            const long N = 3;

            Exception? writeEx = null;
            Exception? setEx = null;
            var tWrite = Task.Run(() =>
            {
                try { stream.Write(new byte[] { 0xBB }, packetNumber: N + 1); }
                catch (Exception ex) { writeEx = ex; }
            });
            var tSet = Task.Run(() =>
            {
                try { stream.SetLastPacketNumber(N); }
                catch (Exception ex) { setEx = ex; }
            });
            await Task.WhenAll(tWrite, tSet);

            // If both succeeded, the stream now has packet N+1 with LastPacket = N — a
            // violation that survived. Count those for the assertion below.
            if (writeEx is null && setEx is null)
            {
                bothSucceededSilently++;
            }
        }

        Assert.Equal(0, bothSucceededSilently);
    }
}
```

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageBusReadStreamCasRaceTests" -m:1
```

Expected: the second test fails non-deterministically (some trials find both succeeded silently). The first test passes pre-fix because the validation loop already catches strict-priority writes. Note: if neither test fails reliably pre-fix, the fix is still correct theoretically — the second test is the regression guard. Document any flakiness in the report.

- [ ] **Step 3: Apply the fix**

In `src/ServiceConnect/Services/MessageBusReadStream.cs`, replace the existing `SetLastPacketNumber` body (lines 31-59) with:

```csharp
/// <inheritdoc />
public void SetLastPacketNumber(long lastPacketNumber)
{
    if (lastPacketNumber < 0)
    {
        throw new ArgumentOutOfRangeException(nameof(lastPacketNumber));
    }

    // Pre-CAS validation: any already-received packet that exceeds the proposed
    // LastPacketNumber means the stream is inconsistent regardless of the CAS outcome.
    foreach (var key in _packets.Keys)
    {
        if (key > lastPacketNumber)
        {
            throw new InvalidOperationException(
                $"Packet number {key} already received for stream {SequenceId} but exceeds " +
                $"the requested LastPacketNumber {lastPacketNumber}. The stream is inconsistent.");
        }
    }

    var previous = Interlocked.CompareExchange(ref _lastPacketNumber, lastPacketNumber, -1);
    if (previous != -1 && previous != lastPacketNumber)
    {
        throw new InvalidOperationException(
            $"LastPacketNumber already set to {previous}; refusing to overwrite with {lastPacketNumber} for stream {SequenceId}.");
    }

    // Post-CAS re-validation: a concurrent Write that read _lastPacketNumber == -1
    // before our CAS landed may have committed an out-of-range packet between the
    // pre-CAS check and the CAS. Now that _lastPacketNumber is published every
    // *future* Write rejects, but an in-flight Write that already TryAdd'd is still
    // a violation we surface here. Idempotent CAS commit (same value) is harmless;
    // throwing here leaves _lastPacketNumber set, which is correct — the stream is
    // permanently poisoned and IsComplete will never observe it as complete with
    // the bad packet because the test only counts indices 0..LastPacketNumber.
    foreach (var key in _packets.Keys)
    {
        if (key > lastPacketNumber)
        {
            throw new InvalidOperationException(
                $"Packet number {key} arrived concurrently and exceeds LastPacketNumber {lastPacketNumber} for stream {SequenceId}. The stream is inconsistent.");
        }
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageBusReadStream" -m:1
```

Expected: 2/2 new pass; existing `MessageBusReadStream*` tests still pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/MessageBusReadStream.cs \
        src/ServiceConnect.UnitTests/Services/MessageBusReadStreamCasRaceTests.cs
git commit -m "$(cat <<'EOF'
fix(stream): SetLastPacketNumber validates before AND after CAS (M24)

Pre-fix the validation loop iterated _packets.Keys after the CAS published
_lastPacketNumber. A concurrent Write that read _lastPacketNumber == -1
could TryAdd an out-of-range packet during the gap and the post-CAS
snapshot might miss it (or include it but the existing ordering was racy).
Validate before AND after the CAS so any in-flight Write is caught.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: M25 — `StreamProcessor.DisposeAsync` late-packet protection

**Files:**
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:9, 304-307`.
- Create: `src/ServiceConnect.UnitTests/Processors/StreamProcessorDisposeTests.cs`.

**Background.** `DisposeAsync` only disposes the cleanup timer. After dispose, late-arriving packets still call `_activeStreams.GetOrAdd(...)` and populate the dictionary. Add a `_disposed` flag; check it on the packet path before touching `_activeStreams`.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/Processors/StreamProcessorDisposeTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class StreamProcessorDisposeTests
{
    [Fact]
    public async Task ProcessAsync_AfterDispose_DoesNotPopulateActiveStreams()
    {
        var typeRegistry = new Mock<IMessageTypeRegistry>().Object;
        var streamHandlerRegistry = new StreamHandlerRegistry();
        var serializer = new Mock<IMessageSerializer>().Object;
        var clock = new FakeTimeProvider();

        var processor = new StreamProcessor(
            new ConsumeScopeAccessor(),
            NullLogger<StreamProcessor>.Instance,
            typeRegistry,
            streamHandlerRegistry,
            serializer,
            clock);

        await processor.DisposeAsync();

        var headers = new Dictionary<string, object>
        {
            { HeaderKeys.MessageType, HeaderKeys.ByteStream },
            { HeaderKeys.SequenceId, Guid.NewGuid().ToString() },
            { HeaderKeys.PacketNumber, "0" },
        };
        var result = await processor.ProcessAsync(
            messageBytes: new byte[] { 0xAA },
            messageType: typeof(byte[]),
            message: null,
            headers: headers,
            envelope: new Envelope(),
            cancellationToken: CancellationToken.None);

        Assert.Equal(ProcessResult.NotHandled, result);
        Assert.Equal(0, processor.ActiveStreamCount);
    }
}
```

NOTE: `ActiveStreamCount` already exists as `internal int` (line 67). `Envelope` may need a different constructor — read existing `StreamProcessorTests.cs` (or similar) for the canonical arrangement. `HeaderKeys` is a static class with the constants; `PacketNumber` and `SequenceId` and `MessageType` and `ByteStream` are all already-defined keys.

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~StreamProcessorDisposeTests" -m:1
```

Expected: fail — pre-fix, `ProcessAsync` continues running after dispose and `ActiveStreamCount > 0`.

- [ ] **Step 3: Apply the fix**

In `src/ServiceConnect/Services/Processors/StreamProcessor.cs`:

1. Add a `_disposed` field near line 23 (after `private int _streamCount;`):

```csharp
private int _disposed;
```

2. Add an early-return at the start of `ProcessAsync` (after the existing `cancellationToken.ThrowIfCancellationRequested();` at line 74):

```csharp
if (Volatile.Read(ref _disposed) != 0)
{
    return NotHandledTask;
}
```

3. Replace `DisposeAsync` (line 304-307) with:

```csharp
public async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
    {
        return;
    }

    await _cleanupTimer.DisposeAsync().ConfigureAwait(false);

    // Drain any in-flight stream entries; their MessageBusReadStream instances do
    // not implement IDisposable, but clearing the dictionary lets GC reclaim them.
    _activeStreams.Clear();
    Interlocked.Exchange(ref _streamCount, 0);
}
```

NOTE: read `MessageBusReadStream.cs` to confirm it does NOT implement `IDisposable`. If it does, dispose each stream before clearing the dict.

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~StreamProcessor" -m:1
```

Expected: new test passes; existing `StreamProcessor*` tests still pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/Processors/StreamProcessor.cs \
        src/ServiceConnect.UnitTests/Processors/StreamProcessorDisposeTests.cs
git commit -m "$(cat <<'EOF'
fix(stream): DisposeAsync rejects late packets and drains state (M25)

Pre-fix DisposeAsync only disposed the cleanup timer; late-arriving packets
called _activeStreams.GetOrAdd and populated the dictionary indefinitely.
Add a _disposed flag, guard the ProcessAsync entry, and Clear() in dispose.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: M26 — admission cap pre-increment / decrement

**Files:**
- Modify: `src/ServiceConnect/Services/Processors/StreamProcessor.cs:112-145`.
- Create: `src/ServiceConnect.UnitTests/Processors/StreamProcessorAdmissionCapTests.cs`.

**Background.** Current code calls `_activeStreams.GetOrAdd(sequenceId, factory)` then conditionally `TryRemove`-rolls-back. A concurrent packet for the same `sequenceId` arriving between the GetOrAdd and the TryRemove sees the rejected stream as live. Reorder so the counter is the gate; only call `GetOrAdd` once admission is confirmed.

- [ ] **Step 1: Write the test**

Create `src/ServiceConnect.UnitTests/Processors/StreamProcessorAdmissionCapTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class StreamProcessorAdmissionCapTests
{
    [Fact]
    public async Task ConcurrentInserts_ExceedingCap_LeaveExactlyCapEntries()
    {
        // Saturate the cap with concurrent admissions. Post-fix:
        //   _activeStreams.Count == MaxActiveStreams, no leaked rejected state.
        var typeRegistry = new Mock<IMessageTypeRegistry>().Object;
        var streamHandlerRegistry = new StreamHandlerRegistry();
        var serializer = new Mock<IMessageSerializer>().Object;
        var clock = new FakeTimeProvider();

        var processor = new StreamProcessor(
            new ConsumeScopeAccessor(),
            NullLogger<StreamProcessor>.Instance,
            typeRegistry,
            streamHandlerRegistry,
            serializer,
            clock);

        const int admissions = 1500;  // > MaxActiveStreams (1000)
        var tasks = new List<Task>(admissions);
        for (var i = 0; i < admissions; i++)
        {
            var seqId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, object>
            {
                { HeaderKeys.MessageType, HeaderKeys.ByteStream },
                { HeaderKeys.SequenceId, seqId },
                { HeaderKeys.PacketNumber, "0" },
            };
            tasks.Add(processor.ProcessAsync(
                messageBytes: new byte[] { 0xAA },
                messageType: typeof(byte[]),
                message: null,
                headers: headers,
                envelope: new Envelope(),
                cancellationToken: CancellationToken.None));
        }
        await Task.WhenAll(tasks);

        // The cap is 1000 (MaxActiveStreams). Each succeeded packet is the FIRST packet
        // of its stream; nothing completes, nothing evicts. Post-fix the dictionary
        // contains exactly 1000 entries — no leaked rejections.
        Assert.Equal(1000, processor.ActiveStreamCount);
    }
}
```

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~StreamProcessorAdmissionCapTests" -m:1
```

Expected: pre-fix the test may pass (the existing TryRemove path eventually rolls back) but with race-window flakiness. The post-fix structure makes this rock-solid; if the test flakes pre-fix, that is itself the regression signal. If the test ALWAYS passes pre-fix, run it 100 times and look for the bounded-corner-case briefly observable rejected-state — but the simpler regression guard is just: post-fix the count is exactly 1000 every run.

- [ ] **Step 3: Apply the reorder**

In `src/ServiceConnect/Services/Processors/StreamProcessor.cs`, replace the admission block (lines 112-145, the `bool rejected = false; var state = _activeStreams.GetOrAdd(...)` through the `if (rejected) { ... }` rollback) with:

```csharp
// Pre-admission counter check: increment first; if over cap, decrement and reject
// without ever speculating on the dictionary. This eliminates the M26 residual race
// where a concurrent packet for the same sequenceId could observe the rejected
// stream state during the GetOrAdd → TryRemove rollback gap.
ActiveStreamState state;
bool admissionAttempted = false;
if (!_activeStreams.TryGetValue(sequenceId, out var existing))
{
    var newCount = Interlocked.Increment(ref _streamCount);
    if (newCount > MaxActiveStreams)
    {
        Interlocked.Decrement(ref _streamCount);
        _logger.LogWarning("Active stream cap {Cap} reached; rejecting new stream {SequenceId}", MaxActiveStreams, sequenceId);
        return NotHandledTask;
    }

    var fresh = new ActiveStreamState(new MessageBusReadStream(sequenceId), _timeProvider.GetUtcNow());
    var actual = _activeStreams.GetOrAdd(sequenceId, fresh);
    admissionAttempted = true;
    if (!ReferenceEquals(actual, fresh))
    {
        // Lost the absent-key race to another thread: roll back our slot reservation
        // since we didn't end up materialising a new entry.
        Interlocked.Decrement(ref _streamCount);
    }
    state = actual;
}
else
{
    state = existing;
}

// admissionAttempted is informational: useful for tests/logs to distinguish the
// "this thread provisioned the entry" path from "this thread reused an existing entry".
_ = admissionAttempted;
```

NOTE: the `admissionAttempted` variable is named for clarity but unused in production code; keep or remove per local style. The key invariant: `_streamCount` is now the gate; the dictionary insert is unconditional once admission is confirmed; no rejected-state ever appears in `_activeStreams`.

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~StreamProcessor" -m:1
```

Expected: 1/1 new pass; all existing `StreamProcessor*` tests still pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/Processors/StreamProcessor.cs \
        src/ServiceConnect.UnitTests/Processors/StreamProcessorAdmissionCapTests.cs
git commit -m "$(cat <<'EOF'
fix(stream): admission cap pre-increment / decrement reorder (M26)

Pre-fix the admission flow speculatively GetOrAdd'd the entry, then
TryRemove-rolled-back if the counter exceeded the cap. A concurrent
packet for the same sequenceId arriving in that window briefly observed
the rejected entry as live. Reorder: increment first; if over cap,
decrement and reject without touching the dictionary. The dictionary
insert is now gated on a successful counter bump.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Smaller — `MessageBusReadStream.Read` IsComplete + Write null-guard

**Files:**
- Modify: `src/ServiceConnect/Services/MessageBusReadStream.cs:62-103, 106-123`.
- Create: `src/ServiceConnect.UnitTests/Services/MessageBusReadStreamGuardTests.cs`.

- [ ] **Step 1: Write tests**

Create `src/ServiceConnect.UnitTests/Services/MessageBusReadStreamGuardTests.cs`:

```csharp
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class MessageBusReadStreamGuardTests
{
    [Fact]
    public void Write_NullData_ThrowsArgumentNullException()
    {
        var stream = new MessageBusReadStream("seq");
        Assert.Throws<ArgumentNullException>(() => stream.Write(data: null!, packetNumber: 0));
    }
}
```

The Read fragility item (IsComplete false-positive) is a defensive hardening with no current trigger; we add the guard but don't write a test that fakes the false-positive (would require reflection / internals shuffling). Document the guard inline.

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageBusReadStreamGuardTests" -m:1
```

Expected: `Write_NullData_ThrowsArgumentNullException` fails (pre-fix `data.Length` throws `NullReferenceException`, not `ArgumentNullException`).

- [ ] **Step 3: Apply the Write null-guard**

In `src/ServiceConnect/Services/MessageBusReadStream.cs`, prepend at the start of `Write` (line 62):

```csharp
public void Write(byte[] data, long packetNumber)
{
    ArgumentNullException.ThrowIfNull(data);
    if (packetNumber < 0)
    {
        // ... rest unchanged
```

- [ ] **Step 4: Apply the Read defensive re-check**

In `src/ServiceConnect/Services/MessageBusReadStream.cs:Read` (line 106-123), after the initial `IsComplete()` check, add a defensive re-check before the iteration:

```csharp
public byte[] Read()
{
    if (!IsComplete())
    {
        throw new InvalidOperationException("Stream is not yet complete.");
    }

    // Defensive re-check: capture LastPacketNumber once and verify the snapshot is
    // still consistent with the iteration bounds. A future change that introduced
    // false-positive IsComplete() returns would otherwise produce a silently
    // truncated Read result. Throwing here surfaces the inconsistency loudly.
    var lastSnapshot = LastPacketNumber;
    if (lastSnapshot < 0)
    {
        throw new InvalidOperationException("Stream LastPacketNumber became unset between IsComplete and Read.");
    }

    // Pre-size MemoryStream to avoid internal buffer doubling.
    var totalBytes = Interlocked.Read(ref _totalBytesWritten);
    using var ms = new MemoryStream(totalBytes > 0 ? (int)totalBytes : 0);
    for (long i = 0; i <= lastSnapshot; i++)
    {
        if (_packets.TryGetValue(i, out var packet))
        {
            ms.Write(packet, 0, packet.Length);
        }
    }
    return ms.ToArray();
}
```

Also apply the same `lastSnapshot` capture to `ReadSequence` (line 127 onwards) for consistency — replace `for (long i = 0; i <= LastPacketNumber; i++)` with the captured snapshot.

- [ ] **Step 5: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageBusReadStream" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/Services/MessageBusReadStream.cs \
        src/ServiceConnect.UnitTests/Services/MessageBusReadStreamGuardTests.cs
git commit -m "$(cat <<'EOF'
fix(stream): MessageBusReadStream null-guard on Write + defensive Read re-check

Write now throws ArgumentNullException for null data (was
NullReferenceException). Read captures LastPacketNumber once and surfaces
a clear error if the snapshot becomes invalid between IsComplete and the
iteration loop — defense-in-depth against any future regression that
introduces a false-positive IsComplete return.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Smaller — `ConsumeContext` volatile publication

**Files:**
- Modify: `src/ServiceConnect/Services/ConsumeContext.cs:64-97`.
- Create: `src/ServiceConnect.UnitTests/Services/ConsumeContextVolatileTests.cs`.

**Background.** Cached fields `_messageId` (string?), `_messageIdCached` (bool), `_correlationId` (Guid?) are plain. Apply the standard double-checked-publication pattern: `volatile bool` flag + plain payload field, with payload write before flag write (release barrier), payload read after flag read (acquire barrier).

`_correlationId` is `Guid?` (Nullable<Guid>); `volatile` is invalid on it. Pattern: introduce an explicit `volatile bool _correlationIdCached` and treat the `_correlationId` field as plain, written before the bool flag.

- [ ] **Step 1: Write the test**

Create `src/ServiceConnect.UnitTests/Services/ConsumeContextVolatileTests.cs`:

```csharp
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ConsumeContextVolatileTests
{
    [Fact]
    public void MessageId_RepeatedReads_ReturnSameCachedValue()
    {
        // Smoke test: the cached value must be stable across concurrent reads.
        // Lacking a way to fake a torn read, this regression-pins the cache
        // semantics — the value resolved on first read is the value returned
        // forever after.
        var headers = new Dictionary<string, object> { { HeaderKeys.MessageId, Guid.NewGuid().ToString() } };
        var ctx = new ConsumeContext( /* construct via existing pattern */ );
        // ... hydrate ctx with the headers ...

        var first = ctx.MessageId;
        var second = ctx.MessageId;

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }
}
```

NOTE: read existing `ConsumeContext*` tests (they exist — `src/ServiceConnect.UnitTests/Services/ConsumeContextTests.cs` or similar) for the canonical construction pattern. The constructor signature is non-trivial; copy from there.

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConsumeContextVolatileTests" -m:1
```

Expected: probably passes pre-fix (the cached-value-is-stable invariant holds even with non-volatile fields under x86's strong memory model). The fix is for ARM / weakly-ordered architectures and provides the formal memory-model guarantee.

- [ ] **Step 3: Apply the volatile publication pattern**

In `src/ServiceConnect/Services/ConsumeContext.cs:64-97`, replace:

```csharp
// Cached backing fields — HeaderDecoder.Decode + Guid.TryParse are called only once
// per ConsumeContext instance regardless of how many times the properties are read.
private string? _messageId;
private bool _messageIdCached;
private Guid? _correlationId;

/// <inheritdoc />
public string? MessageId
{
    get
    {
        if (!_messageIdCached)
        {
            _messageId = Headers.TryGetValue(HeaderKeys.MessageId, out var value)
                ? HeaderDecoder.Decode(value) : null;
            _messageIdCached = true;
        }

        return _messageId;
    }
}

/// <inheritdoc />
public Guid CorrelationId
{
    get
    {
        _correlationId ??= Headers.TryGetValue(HeaderKeys.CorrelationId, out var value)
                && Guid.TryParse(HeaderDecoder.Decode(value), out var id)
                ? id : Guid.Empty;

        return _correlationId.Value;
    }
}
```

with:

```csharp
// Cached backing fields — HeaderDecoder.Decode + Guid.TryParse are called only once
// per ConsumeContext instance regardless of how many times the properties are read.
//
// Memory-model contract: each cached payload field is plain; the volatile bool flag
// publishes it. Writers MUST write the payload before the flag; readers MUST check
// the flag before reading the payload. The flag's release/acquire semantics
// guarantee a non-torn payload read on every architecture (incl. weakly-ordered ARM).
// A racing reader may run the resolution twice (idempotent — string compare /
// Guid.TryParse on the same input), but never observes a torn write.
private string? _messageId;
private volatile bool _messageIdCached;
private Guid _correlationIdValue;
private volatile bool _correlationIdCached;

/// <inheritdoc />
public string? MessageId
{
    get
    {
        if (_messageIdCached) return _messageId;
        var value = Headers.TryGetValue(HeaderKeys.MessageId, out var raw)
            ? HeaderDecoder.Decode(raw) : null;
        _messageId = value;
        _messageIdCached = true;  // volatile write — release barrier publishes _messageId
        return value;
    }
}

/// <inheritdoc />
public Guid CorrelationId
{
    get
    {
        if (_correlationIdCached) return _correlationIdValue;
        var value = Headers.TryGetValue(HeaderKeys.CorrelationId, out var raw)
                && Guid.TryParse(HeaderDecoder.Decode(raw), out var id)
            ? id : Guid.Empty;
        _correlationIdValue = value;
        _correlationIdCached = true;  // volatile write — release barrier publishes _correlationIdValue
        return value;
    }
}
```

The semantic change: `_correlationIdValue` is `Guid` (non-nullable) instead of `Guid?`, with `Guid.Empty` as the resolved-but-absent sentinel — the existing implementation already returns `Guid.Empty` in that case via `_correlationId.Value`, so callers see no behavior difference.

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ConsumeContext" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/ConsumeContext.cs \
        src/ServiceConnect.UnitTests/Services/ConsumeContextVolatileTests.cs
git commit -m "$(cat <<'EOF'
fix(consume-context): volatile-published cached id fields

_messageIdCached and _correlationIdCached become volatile bool flags;
payload fields are written before the flag (release barrier), read after
the flag (acquire barrier). Standard double-checked-publication pattern;
guarantees non-torn reads on weakly-ordered architectures.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: Smaller — `MessageTypeExchangeName` strip assembly version

**Files:**
- Modify: `src/ServiceConnect/Services/MessageTypeExchangeName.cs:43`.
- Create: `src/ServiceConnect.UnitTests/Services/MessageTypeExchangeNameVersionStableTests.cs`.

**Background.** `AssemblyQualifiedName` includes version, culture, and PublicKeyToken. Producers and consumers built against different assembly versions get different exchange names — silent message loss. Replace with `FullName + ", " + Assembly.GetName().Name`. v8 deployment-visible breaking change (existing exchanges named under v7 hash need migration); document in release notes.

- [ ] **Step 1: Write the test**

Create `src/ServiceConnect.UnitTests/Services/MessageTypeExchangeNameVersionStableTests.cs`:

```csharp
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class MessageTypeExchangeNameVersionStableTests
{
    public sealed class TypeInThisAssembly { }

    [Fact]
    public void From_TypeName_DependsOnFullNameAndAssemblyName_NotVersion()
    {
        // We can't easily synthesise a same-Type-different-version scenario here, but we
        // can prove the implementation does NOT use AssemblyQualifiedName by computing the
        // expected hash directly from FullName + ", " + Assembly.GetName().Name and
        // asserting equality.
        var type = typeof(TypeInThisAssembly);
        var expected = ComputeExpected(type);

        var actual = MessageTypeExchangeName.From(type);

        Assert.Equal(expected, actual);
    }

    private static string ComputeExpected(Type type)
    {
        var sanitized = type.FullName!.Replace(".", string.Empty);
        var suffixSource = $"{type.FullName}, {type.Assembly.GetName().Name}";
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(suffixSource), hash);
        var sb = new System.Text.StringBuilder(sanitized.Length + 1 + 8);
        sb.Append(sanitized).Append('_');
        for (var i = 0; i < 4; i++)
            sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
```

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageTypeExchangeNameVersionStable" -m:1
```

Expected: fail — pre-fix the implementation uses `AssemblyQualifiedName`, so the hash doesn't match the helper's `FullName + ", " + AssemblyName` expectation.

- [ ] **Step 3: Apply the fix**

In `src/ServiceConnect/Services/MessageTypeExchangeName.cs:43`, change:

```csharp
var suffixSource = type.AssemblyQualifiedName ?? full;
```

to:

```csharp
// Drop AssemblyQualifiedName (which includes version + culture + PKT) so producers
// and consumers built against different assembly versions of the same logical type
// derive the same exchange name. Falls back to FullName alone if Assembly metadata
// is unavailable.
var assemblyName = type.Assembly.GetName().Name;
var suffixSource = assemblyName is null ? full : $"{full}, {assemblyName}";
```

Also update the XML doc comment on the method (lines 25-28):

```csharp
/// <returns>
/// The flattened type name (dots stripped) suffixed with an underscore and an
/// eight-hex-character SHA-256 prefix derived from the full type name plus the
/// assembly simple name (version-stable). Pre-v8 the suffix was derived from
/// the assembly-qualified name and changed across version bumps.
/// </returns>
```

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MessageTypeExchangeName" -m:1
```

Expected: 1/1 new pass; existing `MessageTypeExchangeName*` tests need migration if they hard-code the v7 hash for known types — read them and adjust.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect/Services/MessageTypeExchangeName.cs \
        src/ServiceConnect.UnitTests/Services/MessageTypeExchangeNameVersionStableTests.cs \
        # any pre-existing MessageTypeExchangeName tests with hard-coded hashes
git commit -m "$(cat <<'EOF'
fix(transport)!: exchange-name hash drops assembly version

Pre-fix the suffix hash used AssemblyQualifiedName — producers and
consumers built against different assembly versions of the same logical
type derived different exchange names, silently losing messages across
version skew. Hash now uses FullName + assembly simple name. v8
deployment-visible breaking change: existing exchanges named under the
v7 hash will need migration. See release notes.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(`!` for deployment-visible breaking change.)

---

## Task 13: Smaller — `HandlerProcessor` routing slip pair

**Files:**
- Modify: `src/ServiceConnect/Services/Processors/HandlerProcessor.cs:181-186, plus XML doc on the method around line 149`.
- Create: `src/ServiceConnect.UnitTests/Processors/HandlerProcessorRoutingSlipTests.cs`.

**Background.** Two related changes:
1. `IsKnownQueue` rejects cross-service destinations. Replace with format validation only (the existing `IsValidRoutingSlipDestination` check at line 175-179 already covers most format concerns; remove the `IsKnownQueue` check at line 181-186 entirely).
2. Document on `HandlerProcessor` (XML doc at top of class or on `ForwardRoutingSlipAsync`) that the slip is dropped from the in-flight forward path on any handler throw and the slip data is preserved in the message envelope (relied on for DLQ retries).

- [ ] **Step 1: Write tests**

Create `src/ServiceConnect.UnitTests/Processors/HandlerProcessorRoutingSlipTests.cs`:

```csharp
// This test focuses only on the IsKnownQueue→format-only behavioral change.
// The "slip preserved in DLQ" behavior is verified by the existing DLQ E2E
// path; the doc-only change is verified via the spec reviewer reading the
// updated XML doc on the class.

using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class HandlerProcessorRoutingSlipTests
{
    [Fact]
    public async Task ForwardRoutingSlip_DestinationNotInLocalConfig_DoesNotThrow()
    {
        // Pre-fix: IsKnownQueue threw for any destination not in queueConfig.
        // Post-fix: only format validation — well-formed cross-service queue names
        // are allowed.
        // ... arrange a HandlerProcessor with a queue config that does NOT contain
        //     "remote-service-q", build a routing slip with that destination, dispatch,
        //     assert no InvalidOperationException ...
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has*wildcard")]
    [InlineData("has\nnewline")]
    public async Task ForwardRoutingSlip_MalformedDestination_StillThrows(string badDestination)
    {
        // Format validation must still reject obviously-bad destinations.
        // ... assert InvalidOperationException ...
    }
}
```

NOTE: read the existing `HandlerProcessor*` test setup in `src/ServiceConnect.UnitTests/Processors/HandlerProcessorTests.cs` (or similar) to find the canonical arrangement. The HandlerProcessor's `ProcessAsync` requires non-trivial wiring (consume context pool, scope accessor, queue config, bus, etc.); copy from there.

If the existing test suite's HandlerProcessor harness is too heavy, the smaller items can be tested by surfacing `ForwardRoutingSlipAsync` as `internal static` and invoking it directly — but only if that's already the existing pattern.

- [ ] **Step 2: Run pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HandlerProcessorRoutingSlipTests" -m:1
```

Expected: `ForwardRoutingSlip_DestinationNotInLocalConfig_DoesNotThrow` fails — the `IsKnownQueue` check throws.

- [ ] **Step 3: Remove the IsKnownQueue check**

In `src/ServiceConnect/Services/Processors/HandlerProcessor.cs:181-186`, remove:

```csharp
if (!ConsumeContext.IsKnownQueue(trimmed, queueConfig))
{
    throw new InvalidOperationException(
        $"Routing-slip destination '{trimmed}' is not a recognized queue. " +
        "Configure queue mappings to allow this destination.");
}
```

The `IsValidRoutingSlipDestination` format check above (line 175-179) is sufficient: it enforces non-empty, length-bounded, no AMQP-control-chars. Cross-service routing slips can now target any well-formed queue name.

- [ ] **Step 4: Update the XML doc on the routing-slip silent-drop contract**

Add or update an XML doc block on `ForwardRoutingSlipAsync` (line 149):

```csharp
/// <summary>
/// Forwards the routing slip's next-step destinations after all handlers complete
/// successfully.
/// </summary>
/// <remarks>
/// <para>
/// <b>Slip drop on handler throw.</b> If any handler threw during dispatch, the
/// caller throws an <see cref="AggregateException"/> BEFORE this method runs; the
/// in-flight slip-forward is therefore skipped on partial-failure dispatches. The
/// slip data remains in the message envelope (the <c>RoutingSlip</c> header is
/// not stripped during dispatch), so DLQ-routed messages and manual retries still
/// carry the slip and can resume the chain after the failure is resolved.
/// </para>
/// <para>
/// <b>Cross-service destinations.</b> v8 removed the <c>IsKnownQueue</c> check;
/// destinations that are not in the local <see cref="IQueueConfiguration"/> are
/// allowed as long as they pass <see cref="IsValidRoutingSlipDestination"/> (format,
/// length, no AMQP control characters). RabbitMQ routes via the alternate-exchange /
/// mandatory-return path if the queue does not exist downstream.
/// </para>
/// </remarks>
private static async Task ForwardRoutingSlipAsync( /* ... existing signature ... */ )
```

- [ ] **Step 5: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~HandlerProcessor" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect/Services/Processors/HandlerProcessor.cs \
        src/ServiceConnect.UnitTests/Processors/HandlerProcessorRoutingSlipTests.cs
git commit -m "$(cat <<'EOF'
fix(handler)!: routing slip allows cross-service destinations + doc drop-on-throw

Drop the IsKnownQueue check in ForwardRoutingSlipAsync — it blocked the
legitimate cross-service slip pattern. The pre-existing
IsValidRoutingSlipDestination check (non-empty, length-bounded, no AMQP
control chars) is sufficient. Document the slip-drop-on-throw contract:
slip is skipped from in-flight forward when any handler throws but is
preserved in the message envelope for DLQ retry.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

(`!` for behavior change — accepts destinations previously rejected.)

---

## Task 14: Phase 11 release notes

**File:** `website/src/content/docs/releases.mdx`.

- [ ] **Step 1: Insert the section**

Find the prior phase entry (`### InMemory persistence`); insert immediately after:

```mdx
### Processor cleanups (Aggregator, Stream, Handler, ProcessManagerTimeout)

**Bug fixes**

- **Saga timeout dispatch is at-most-once when the lease holds.** `ProcessManagerTimeoutService` now re-checks the lease deadline after `SendAsync` returns; if the lease expired during the send, the row is left for the lease-expiry sweep to reclaim instead of `Remove`-ing under a peer poller. Net effect: at worst one duplicate send (already documented at-least-once timeout semantics), no double-`Remove` racing a peer's reclaim.
- **`ConsumeScopeAccessor` scopes are per-instance.** Pre-fix the AsyncLocal was a static field shared across every accessor in the AppDomain; two `Bus` instances dispatching concurrently saw each other's scopes. The class is registered as a DI singleton, so the field's lifetime is unchanged.
- **`StreamProcessor` admission cap closed under concurrency.** The pre-existing speculative `GetOrAdd` + `TryRemove`-rollback path briefly admitted a rejected stream into `_activeStreams`; concurrent packets for the same `sequenceId` could observe the rejected entry. Reorder: the `Interlocked` counter is the gate; the dictionary insert happens only on a successful counter bump.
- **`StreamProcessor.DisposeAsync` rejects late packets.** Pre-fix only the cleanup timer was disposed; late-arriving packets still populated `_activeStreams` indefinitely. Add a `_disposed` flag, guard `ProcessAsync`, and drain the dictionary on dispose.
- **`MessageBusReadStream.SetLastPacketNumber` validates before AND after the CAS.** A concurrent `Write` that read the pre-CAS sentinel could TryAdd an out-of-range packet; the post-CAS validation pass now catches it.
- **`MessageBusReadStream.Write` null-guards `data`.** `ArgumentNullException` instead of `NullReferenceException`.
- **`MessageBusReadStream.Read` captures `LastPacketNumber` once.** Defense-in-depth: a future regression introducing a false-positive `IsComplete` would surface immediately rather than producing a silently truncated read.
- **`ConsumeContext` cached fields use the volatile-publication pattern.** `_messageIdCached` / `_correlationIdCached` are `volatile bool`; payload fields are written before the flag (release barrier) and read after (acquire barrier). Guarantees non-torn reads on weakly-ordered architectures.
- **`HandlerProcessor` allows cross-service routing slips.** The `IsKnownQueue` check that rejected destinations not in the local queue config is removed; format validation (`IsValidRoutingSlipDestination`) remains. RabbitMQ's alternate-exchange / mandatory-return is the right error surface for unknown destinations.

**Breaking API changes**

- **`IBusConfiguration.ExceptionHandler` is async.** Signature changes from `Action<Exception>?` to `Func<Exception, CancellationToken, ValueTask>?`. Migration shim:

```csharp
// before (v7)
cfg.ExceptionHandler = ex => Log.Error(ex);
// after (v8)
cfg.ExceptionHandler = (ex, _) => { Log.Error(ex); return ValueTask.CompletedTask; };
```

The dispatcher `await`s the handler and passes the shutdown cancellation token. Slow user code no longer blocks the consumer thread or throttles RabbitMQ prefetch. Handler-thrown exceptions are logged at warning level and not propagated.

**Deployment-visible changes**

- **`MessageTypeExchangeName` hash drops `AssemblyQualifiedName`.** The eight-hex-character suffix is now derived from `type.FullName + ", " + assembly.GetName().Name` — version-stable across assembly bumps. Pre-v8 the hash included version + culture + PublicKeyToken; producers and consumers built against different assembly versions of the same logical type derived different exchange names. **Migration:** existing exchanges named under the v7 hash will need to be re-declared (or re-bound) on first v8 deployment. Apps using a single exchange-naming source (one repo, one assembly version per environment) will see no observable change; apps with version skew will see new exchange names appear.

**Contract clarifications (non-breaking)**

- **`IProcessManagerFinder.FindDataAsync<T>` MUST return a fresh `Data` reference per call.** Both built-in persistors comply (InMemory clones via `DeepClone`; Mongo via BSON deserialize). Third-party persistors that cache rows internally MUST clone before returning. The `ProcessManagerProcessor.UpdateData` retry path depends on this so handler-mutated `Data` doesn't leak across retries.
- **`HandlerProcessor` drops the routing slip from in-flight forward on any handler throw.** The slip data is preserved in the message envelope (the `RoutingSlip` header is not stripped); DLQ-routed messages and manual retries can still resume the chain.
```

- [ ] **Step 2: Build the website**

```bash
npm --prefix website run build 2>&1 | tail -3
```

Expected: clean.

- [ ] **Step 3: Commit**

```bash
git add website/src/content/docs/releases.mdx
git commit -m "$(cat <<'EOF'
docs(website): phase 11 release notes — processor cleanups

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 15: Reference + learn doc updates

**Files:**
- `website/src/content/docs/reference/extension-points/...` (`ExceptionHandler` async migration).
- `website/src/content/docs/reference/handlers/...` (AsyncLocal context guarantees + slip semantics).
- `website/src/content/docs/reference/process-managers/...` (saga retry contract + timeout dispatch contract).
- `website/src/content/docs/learn/messaging-patterns/...` (cross-service routing slips + slip-drop-on-throw).

- [ ] **Step 1: Locate relevant pages**

```bash
grep -rln "ExceptionHandler\|ConsumeScopeAccessor\|RoutingSlip\|FindDataAsync\|ProcessManagerTimeout" website/src/content/docs/ 2>/dev/null
```

- [ ] **Step 2: Update the ExceptionHandler reference page**

Add a "v8 async migration" section with the migration shim (paste the same `// before / // after` block from Task 14). Document the cancellation-token contract.

- [ ] **Step 3: Update the saga / process-manager reference**

Add a section: **"`FindDataAsync<T>` fresh-copy contract."** Paste the XML doc paragraph from Task 3 in narrative form. Add a section: **"Timeout dispatch is at-most-once when the lease holds."** Reference Phase 8's lease-margin guard + Phase 11's post-send re-check.

- [ ] **Step 4: Update the routing-slip learn page**

Add or update a section: **"Routing slip on handler failure."** Paste the slip-drop-on-throw paragraph (slip skipped from in-flight forward, preserved in message envelope). Add a section: **"Cross-service routing slips."** Paste the `IsKnownQueue` removal note (alternate-exchange routing for unknown destinations).

- [ ] **Step 5: Update the handler reference**

Add or update a section: **"Per-instance AsyncLocal context."** State that two `Bus` instances in the same AppDomain do not share `ConsumeScopeAccessor` state (M23 fix).

- [ ] **Step 6: Build + commit**

```bash
npm --prefix website run build 2>&1 | tail -3
git add website/src/content/docs/
git commit -m "$(cat <<'EOF'
docs(website): phase 11 reference + learn page updates

ExceptionHandler async migration. FindDataAsync<T> fresh-copy contract.
Routing slip drop-on-throw + cross-service destinations. Per-instance
ConsumeScopeAccessor.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

If a page named in the spec doesn't exist, use the closest topical page — don't fabricate page hierarchies. Document any gaps in the implementer's report.

---

## Task 16: Examples README updates

**Files:**
- `examples/Aggregator/README.md`.
- `examples/ProcessManager/README.md`.
- `examples/Streaming/README.md`.

- [ ] **Step 1: Read each README**

```bash
ls examples/
cat examples/Aggregator/README.md
cat examples/ProcessManager/README.md
cat examples/Streaming/README.md
```

- [ ] **Step 2: Update `examples/Aggregator/README.md`**

If the README references aggregator timeout / batch-size semantics, ensure the wording is accurate post-Phase 9 (the `_resetTimerLock` work). No new edits required if the README is silent on these. If silent, add a one-paragraph note about the v9 / v11 changes (timer is single-tracked under a lock; `GetSnapshotAsync` releases the lock during clone so concurrent inserts proceed).

- [ ] **Step 3: Update `examples/ProcessManager/README.md`**

Add a one-paragraph note on saga retry semantics: "Handler that throws → `ProcessManagerProcessor.UpdateData` is skipped; retry re-fetches the row via `FindDataAsync<T>`, which the persistor MUST return a fresh copy of so the prior handler's mutation does not leak. Both built-in persistors comply." Add a one-paragraph note on timeout dispatch: "At-most-once while the lease holds; pre-send and post-send lease-margin checks skip dispatch if a peer poller is about to reclaim."

- [ ] **Step 4: Update `examples/Streaming/README.md`**

Add a one-paragraph note on stream lifecycle: "v11 hardened the admission cap (counter-gated, no speculative entry) and added a dispose flag (late packets after `DisposeAsync` are dropped). Cross-service routing slips are also now allowed if the slip's next-step destination is well-formed."

- [ ] **Step 5: Commit**

```bash
git add examples/Aggregator/README.md examples/ProcessManager/README.md examples/Streaming/README.md
git commit -m "$(cat <<'EOF'
docs(examples): phase 11 README updates

Aggregator timer semantics (Phase 9 lock, Phase 10/11 snapshot-clone),
saga retry semantics + timeout at-most-once contract, stream lifecycle
+ admission cap + dispose flag, cross-service routing slips.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 17: Final verification gate

- [ ] **Step 1: Per-csproj build clean**

```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Focused unit-test pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
  --filter "FullyQualifiedName~Aggregator|Stream|Handler|ProcessManagerTimeout|ConsumeContext|ConsumeScope|MessageDispatcher|MessageBusReadStream|MessageTypeExchangeName" \
  -m:1
```

Expected: all pass.

- [ ] **Step 3: E2E test (Mongo regression — Docker required)**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj \
  --filter "FullyQualifiedName~MongoDbProcessManagerFinderFreshDataTests" -m:1
```

Expected: 1/1 pass.

- [ ] **Step 4: Astro build**

```bash
npm --prefix website run build 2>&1 | tail -3
```

Expected: clean.

- [ ] **Step 5: Grep verifications**

```bash
# M27: ExceptionHandler signature is async everywhere
grep -rn "Action<Exception>" src/ServiceConnect.Interfaces/ src/ServiceConnect/ 2>/dev/null
# Expected: zero hits.

# M23: ConsumeScopeAccessor static field is gone
grep -n "static.*AsyncLocal" src/ServiceConnect/Services/ConsumeScopeAccessor.cs
# Expected: zero hits.

# M24: SetLastPacketNumber has both pre-CAS and post-CAS validation
grep -c "exceeds.*LastPacketNumber" src/ServiceConnect/Services/MessageBusReadStream.cs
# Expected: 2 (pre-CAS validation + post-CAS validation).

# M25: StreamProcessor has _disposed flag
grep -n "_disposed" src/ServiceConnect/Services/Processors/StreamProcessor.cs
# Expected: at least 2 hits (declaration + read in ProcessAsync).

# M26: pre-increment counter pattern
grep -B1 -A3 "Interlocked.Increment(ref _streamCount)" src/ServiceConnect/Services/Processors/StreamProcessor.cs
# Expected: paired Decrement on rejection path; counter is the gate.

# M27 dispatcher awaits handler
grep -n "await handler(" src/ServiceConnect/Services/MessageDispatcher.cs
# Expected: at least 1 hit.

# Routing slip IsKnownQueue removed
grep -n "IsKnownQueue" src/ServiceConnect/Services/Processors/HandlerProcessor.cs
# Expected: zero hits.

# AssemblyQualifiedName removed from exchange name
grep -n "AssemblyQualifiedName" src/ServiceConnect/Services/MessageTypeExchangeName.cs
# Expected: zero hits.
```

- [ ] **Step 6: Final code review (optional)**

If running the final review: dispatch `superpowers:code-reviewer` over Phase 11 commits (from `e77272a2` through HEAD). Briefing template — same as Phases 8/9/10.

If skipping: ensure each per-task review caught Critical/Important issues; document in commit history.

---

## Phase 11 done

All findings closed. The phase ships:

- M16 timeout double-dispatch window narrowed (post-send lease re-check).
- M21 `FindDataAsync<T>` fresh-copy contract documented + per-persistor regression tests.
- M23 `ConsumeScopeAccessor` per-instance scoping.
- M24 `MessageBusReadStream.SetLastPacketNumber` validate-before-and-after-CAS.
- M25 `StreamProcessor.DisposeAsync` late-packet protection.
- M26 admission cap pre-increment / decrement (no speculative dictionary insert).
- M27 async `ExceptionHandler` (breaking).
- Smaller: `MessageBusReadStream` null-guard + Read defensive snapshot; `ConsumeContext` volatile publication; `MessageTypeExchangeName` version-stable hash; `HandlerProcessor` cross-service slip + drop-on-throw doc.
- Release notes + reference + learn + examples README updates.

Move to Phase 12 (Interfaces hygiene + HealthChecks polish) — the user's pattern is "phase complete; continue to phase N+1?".
