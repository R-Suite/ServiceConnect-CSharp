# Phase 2 — Medium fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix all 22 Medium-severity items from `consolodated-issues/2026-04-22-consolidated-issues.md` across 7 groups (one commit per group) on `v7-clean-architecture`.

**Architecture:** Each group is a single commit that bundles one or more closely-related fixes with tests. Within a group, TDD: write a failing test that captures the audit claim, confirm it reproduces, apply the minimal fix, confirm it passes, commit. Grouping is by component cohesion (Bus lifecycle, DI, RabbitMQ publisher, RabbitMQ consumer, persistence, telemetry) so each group has one coherent review lens.

**Test-fixture conventions:** Every test snippet below references existing helpers (`BusTestHarness`, `ConsumerTestFactory`, etc.) by role, not by exact name. **Before writing tests, read the existing test file(s) and match the established pattern.** If no fixture helper exists for the target, introduce the minimum stub needed — don't invent a new testing DSL. Placeholders like `/* existing args */` or `/* Testcontainers-backed Mongo */` indicate "read the current test class and copy its setup verbatim". If an implementer encounters a placeholder they cannot resolve by reading the file, report BLOCKED and request clarification.

**Audit-claim verification:** All 22 items have been audited in this codebase and the implementer should trust the claims. If the failing-test step unexpectedly PASSES at Step 3, do NOT proceed to the fix — instead commit the test as a regression guard, flip the consolidated-issues bullet to `[-]` with `disconfirmed by <TestClass>.<TestName>`, and move on. This mirrors the Phase 1 verify-then-fix pattern.

**Tech Stack:**
- xUnit + `Shouldly` / `Assert` (preexisting test style)
- `Moq` for stubbing and `Microsoft.Extensions.Time.Testing.FakeTimeProvider` where time-sensitive
- Testcontainers via `PersistenceFixture` / `MessagingFixture` for E2E integration tests
- MongoDB.Driver 2.x with `GuidRepresentationMode.V3`
- RabbitMQ.Client 7.x (C# async API)
- User is NOT in the docker group — wrap all docker/Testcontainers commands in `sg docker -c '...'`

---

## File Structure

**Files touched by group:**

- **Group 1** (Bus lifecycle + header spoofing): `src/ServiceConnect/Bus.cs` (header stamping order, lifecycle state, cancellation handling, semaphore dispose race); `src/ServiceConnect.UnitTests/BusTests.cs`.
- **Group 2** (DI + dispatcher + interface nullability): `src/ServiceConnect/Services/MessageDispatcher.cs`, `src/ServiceConnect/ServiceCollectionExtensions.cs`, `src/ServiceConnect.Interfaces/Messages/IMessageTypeRegistry.cs`; `src/ServiceConnect.UnitTests/MessageDispatcherTests.cs`, `src/ServiceConnect.UnitTests/ServiceCollectionExtensionsTests.cs`, `src/ServiceConnect.UnitTests/MessageTypeRegistryTests.cs`.
- **Group 3** (RabbitMQ publisher hardening): `src/ServiceConnect.Client.RabbitMQ/Producer.cs`, `src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs`; `src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs`, `src/ServiceConnect.UnitTests/RabbitMQ/MessageAuditPublisherTests.cs` (extend or create).
- **Group 4** (RabbitMQ consumer lifecycle & idempotency): `src/ServiceConnect.Client.RabbitMQ/Consumer.cs`; `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerTests.cs` (extend or create).
- **Group 5** (RabbitMQ consumer validation + broker observability): `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs`, `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`; `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerTests.cs` (extend), `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostTests.cs` (extend), plus one E2E test for broker cancel subscription if feasible.
- **Group 6** (Persistence correctness): `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`, `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`; `src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderTests.cs` (extend), `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderTests.cs` (extend).
- **Group 7** (Telemetry): `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`; `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs` (extend).

**Why these boundaries:** each group maps to a single subsystem so a review is focused. Fixes within a group touch the same file or adjacent files. Group 2 bundles the `IMessageTypeRegistry` nullability fix with the DI/dispatcher cluster because `TryResolve` is consumed by DI-wired dispatch code — the fix is two lines plus one test, too small for a standalone group.

---

## Progress tracking

Update `consolodated-issues/2026-04-22-consolidated-issues.md` after each commit — flip the corresponding `[ ]` to `[x] (commit: <sha>)` and bump the Medium counter. The final `Medium X/22` should be `22/22`.

---

## Group 1 — Bus lifecycle correctness + caller-overridable system headers (Medium, Core)

**Items:**
- M1: `MessageType`/`CorrelationId` in headers are caller-overridable (`Bus.cs:442-465, 510-533`)
- M2: `StopConsumingCoreAsync` sets `_stopped = true` even if consumption never started (`Bus.cs:371-383`)
- M3: `SemaphoreSlim` can be disposed while a lifecycle caller is about to wait on it (`Bus.cs:403-416`)
- M4: Missing `OperationCanceledException` handling in `StopConsumingCoreAsync` (`Bus.cs:386-393`)

**Files:**
- Modify: `src/ServiceConnect/Bus.cs`
- Modify/Create: `src/ServiceConnect.UnitTests/BusTests.cs`

---

### Task 1.1 — Header stamping order (M1)

- [ ] **Step 1: Read `CreateEnvelope` and `BuildHeadersDirect` in Bus.cs (lines 434–534).**

Confirm the current order: system headers (`MessageType`, `CorrelationId`) stamped first → `additionalHeaders` iterated (caller can overwrite) → `MessageId` stamped last. The audit applies to `MessageType` and `CorrelationId` only; `MessageId` was fixed in commit `5a530182`.

- [ ] **Step 2: Add a failing test to `BusTests.cs`.**

```csharp
[Fact]
public async Task Send_CallerCannotOverrideSystemHeaders()
{
    var harness = BusTestHarness.Build();      // existing helper; see BusTests for pattern
    var options = new SendOptions
    {
        Headers = new Dictionary<string, string>
        {
            [HeaderKeys.MessageType] = "SomeoneElsesType",
            [HeaderKeys.CorrelationId] = "spoofed-correlation",
            [HeaderKeys.MessageId] = Guid.NewGuid().ToString(),
        }
    };

    await harness.Bus.SendAsync(new SampleMessage(), options, CancellationToken.None);

    var envelope = harness.LastEnvelope.ShouldNotBeNull();
    envelope.Headers[HeaderKeys.MessageType].ShouldBe(typeof(SampleMessage).FullName);
    envelope.Headers[HeaderKeys.CorrelationId].ShouldNotBe("spoofed-correlation");
    envelope.Headers[HeaderKeys.MessageId].ShouldNotBe(options.Headers[HeaderKeys.MessageId]);
}
```

Adapt to the existing `BusTests` fixture/harness pattern — do NOT introduce a new fixture style. If `BusTestHarness` does not exist, mirror the setup used by the nearest existing `[Fact]` in the file.

- [ ] **Step 3: Run the test, confirm it fails.**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
  --filter "FullyQualifiedName~Send_CallerCannotOverrideSystemHeaders" \
  --nologo --verbosity minimal
```

Expected: FAIL — caller's `MessageType`/`CorrelationId` overwrite the system values.

- [ ] **Step 4: Apply the fix in `Bus.cs` `CreateEnvelope` + `BuildHeadersDirect`.**

Reorder so system headers are stamped LAST (after the caller's `additionalHeaders` iteration). Same fix in both methods. Alternatively, iterate `additionalHeaders` first with a filter that skips any key in the reserved-header set:

```csharp
private static readonly HashSet<string> ReservedHeaders = new(StringComparer.Ordinal)
{
    HeaderKeys.MessageType,
    HeaderKeys.CorrelationId,
    HeaderKeys.MessageId,
};

// inside CreateEnvelope / BuildHeadersDirect:
if (additionalHeaders is not null)
{
    foreach (var kvp in additionalHeaders)
    {
        if (ReservedHeaders.Contains(kvp.Key))
        {
            _logger.LogWarning(
                "Ignoring caller-supplied reserved header '{Key}'; system headers are authoritative",
                kvp.Key);
            continue;
        }
        headers[kvp.Key] = kvp.Value;
    }
}
headers[HeaderKeys.MessageType] = messageType.FullName!;
headers[HeaderKeys.CorrelationId] = correlationId.ToString();
headers[HeaderKeys.MessageId] = Guid.NewGuid().ToString();
```

Prefer the filter approach over "stamp last" because it makes the guarantee explicit and logs any caller attempt. Declare `ReservedHeaders` once at file scope. If no logger is available in these static helpers, thread one through (check current callers) or drop the log and just skip.

- [ ] **Step 5: Rerun the test, confirm it passes.**

---

### Task 1.2 — `StopConsumingCoreAsync` state when never started (M2)

- [ ] **Step 1: Read `StopConsumingCoreAsync` (Bus.cs:365–400) and the `StartConsumingAsync` check at line 301–303.**

Confirm: `_stopped = true` is set unconditionally inside the lock, even when `_consuming == false`. `StartConsumingAsync` later throws `InvalidOperationException("bus has been stopped")` on `_stopped`.

- [ ] **Step 2: Add a failing test to `BusTests.cs`.**

```csharp
[Fact]
public async Task StopConsumingAsync_BeforeStart_AllowsSubsequentStart()
{
    var bus = BusTestHarness.Build().Bus;

    await bus.StopConsumingAsync(CancellationToken.None); // defensive stop before any start

    var startCall = async () => await bus.StartConsumingAsync(CancellationToken.None);
    await startCall.ShouldNotThrowAsync();
    await bus.StopConsumingAsync(CancellationToken.None); // cleanup
}
```

- [ ] **Step 3: Run, confirm failure** (throws "bus has been stopped").

- [ ] **Step 4: Fix in `StopConsumingCoreAsync`.**

Only flip `_stopped = true` when `_consuming` was true OR when called from `DisposeAsync` (terminal). Introduce a parameter distinguishing the two call sites, or — simpler — guard the flip:

```csharp
// inside the lock, after capturing the current consumer reference:
bool wasConsuming = _consuming;
_consuming = false;

// Only mark the bus as terminally stopped if consumption actually started.
// A defensive StopConsumingAsync() on a bus that never started must remain restartable.
if (wasConsuming)
{
    _stopped = true;
}
```

If the `_stopped` semantic is load-bearing for `DisposeAsync` as a "don't accept new starts after dispose", keep a separate `_disposed` check in `StartConsumingAsync` (already present at line 301 region — verify by reading). `_stopped` should mean "was stopped after running", not "terminal".

- [ ] **Step 5: Rerun, confirm pass. Also re-run the full `BusTests` class to confirm no regression:**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
  --filter "FullyQualifiedName~BusTests" --nologo --verbosity minimal
```

---

### Task 1.3 — Semaphore dispose race (M3)

- [ ] **Step 1: Read `DisposeAsync` (403–416) and the entry pattern for `StartConsumingAsync` (≈line 293).**

Confirm: `DisposeAsync` sets `_disposed` under `_stateLock` → awaits `StopConsumingCoreAsync` → disposes `_lifecycleSemaphore`. A concurrent `StartConsumingAsync` that passed its `ThrowIfDisposed` check before `_disposed` was flipped will now call `_lifecycleSemaphore.WaitAsync` on a disposed semaphore.

- [ ] **Step 2: Add a failing test.**

This race is hard to reproduce deterministically without instrumentation. Use a `TaskCompletionSource` interceptor on the lifecycle semaphore — but if that means refactoring the Bus to accept an injected semaphore, prefer a narrower test that asserts the POST-fix behaviour: calling any lifecycle method after `DisposeAsync` returns `ObjectDisposedException` (not `NullReferenceException` or success). Then add a second test that mocks `StopConsumingCoreAsync` with a completion source to exercise the window:

```csharp
[Fact]
public async Task StartConsumingAsync_AfterDispose_ThrowsObjectDisposedException()
{
    var bus = BusTestHarness.Build().Bus;
    await bus.DisposeAsync();

    await Should.ThrowAsync<ObjectDisposedException>(
        () => bus.StartConsumingAsync(CancellationToken.None).AsTask());
}

[Fact]
public async Task StartConsumingAsync_ConcurrentWithDispose_NeverThrowsOnDisposedSemaphore()
{
    // Setup: Bus is constructed and not disposed. Start a StartConsumingAsync on thread A
    // that will block inside the consumer.StartAsync() hook. On thread B, await DisposeAsync.
    // Thread A must see ObjectDisposedException from the lifecycle API, NOT from inside
    // a core helper (which would indicate the semaphore was disposed under it).
    // ... TCS-based interceptor ...
}
```

The second test is aspirational — if wiring it cleanly requires more refactoring than the fix, **skip** it and note in the commit message that the guarantee relies on the fail-fast dispose check.

- [ ] **Step 3: Run, confirm the first test fails** (likely throws `NullReferenceException` or `ObjectDisposedException` from the wrong call site).

- [ ] **Step 4: Fix.**

Two-part fix in `Bus.cs`:

1. Every lifecycle entry point (`StartConsumingAsync`, `StopConsumingAsync`, `SendAsync`, `PublishAsync`, etc. — whatever currently has `ThrowIfDisposed`) must re-check `_disposed` **after** acquiring `_lifecycleSemaphore`. This catches the race where dispose flipped the flag between entry and semaphore-wait.

2. Wrap the `_lifecycleSemaphore.WaitAsync` call itself in try/catch for `ObjectDisposedException` and rethrow as a uniform `ObjectDisposedException(nameof(Bus))`. This keeps the exception surface clean regardless of which side lost the race.

```csharp
private async Task EnterLifecycleAsync(CancellationToken cancellationToken)
{
    ThrowIfDisposed();
    try
    {
        await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    catch (ObjectDisposedException)
    {
        throw new ObjectDisposedException(nameof(Bus));
    }
    // Recheck after the wait — dispose may have completed while we were queued.
    ThrowIfDisposed();
}
```

Replace each raw `await _lifecycleSemaphore.WaitAsync(...)` with a call to `EnterLifecycleAsync`.

- [ ] **Step 5: Rerun tests, confirm pass.**

---

### Task 1.4 — `OperationCanceledException` in `StopConsumingCoreAsync` (M4)

- [ ] **Step 1: Read the try/catch block at Bus.cs:386-393.**

Confirm: `WaitAsync(_disposeTimeout, cancellationToken)` is wrapped in a catch that handles only `TimeoutException`. An `OperationCanceledException` from caller cancellation escapes the try, skipping the remaining teardown logic (consumer resources leak).

- [ ] **Step 2: Add a failing test.**

```csharp
[Fact]
public async Task StopConsumingAsync_WhenCancellationRequested_StillReleasesConsumerResources()
{
    var harness = BusTestHarness.Build();
    await harness.Bus.StartConsumingAsync(CancellationToken.None);

    var cts = new CancellationTokenSource();
    cts.Cancel();

    await Should.ThrowAsync<OperationCanceledException>(
        () => harness.Bus.StopConsumingAsync(cts.Token).AsTask());

    harness.Consumer.DisposeCalled.ShouldBeTrue(); // cancellation must not skip teardown
}
```

`harness.Consumer.DisposeCalled` is shorthand for whatever existing test-double the fixture uses; adapt to the current pattern.

- [ ] **Step 3: Run, confirm failure.**

- [ ] **Step 4: Fix.**

In `StopConsumingCoreAsync`, restructure the teardown so the try/finally pattern ensures consumer disposal even on cancellation, then re-throw the `OperationCanceledException`:

```csharp
// inside StopConsumingCoreAsync, replacing the existing try/catch block around WaitAsync:
try
{
    await consumerToDispose.StopAsync().ConfigureAwait(false);
    await consumerToDispose.DisposeAsync().ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    // Host cancellation — abandon the grace-period wait but don't leak.
    try { await consumerToDispose.DisposeAsync().ConfigureAwait(false); }
    catch (Exception disposeEx) { _logger.LogWarning(disposeEx, "Consumer dispose failed during cancellation"); }
    throw;
}
catch (TimeoutException)
{
    _logger.LogWarning("Consumer dispose timed out after {Timeout}", _disposeTimeout);
}
```

Adapt the exact shape to the current method — the point is: cancellation still tears resources down and re-throws; timeouts still warn and continue.

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 1.5 — Run the full unit-test suite, update the consolidated doc, commit.

- [ ] **Step 1: Full unit test run.**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo --verbosity minimal
```

Expected: all pass, no new warnings.

- [ ] **Step 2: Update `consolodated-issues/2026-04-22-consolidated-issues.md`.**

Flip M1–M4 bullets from `[ ]` to `[x] (commit: <sha>)`. Bump the Medium counter from `0/22` to `4/22`.

- [ ] **Step 3: Commit.**

```bash
git add src/ServiceConnect/Bus.cs \
        src/ServiceConnect.UnitTests/BusTests.cs \
        consolodated-issues/2026-04-22-consolidated-issues.md
git commit -m "$(cat <<'EOF'
fix(bus): lifecycle correctness + system-header spoof-proofing

- Reject caller-supplied MessageType/CorrelationId/MessageId in options.Headers
  (ReservedHeaders filter with warning log; producers remain authoritative).
- StopConsumingCoreAsync only marks _stopped=true when consumption actually
  ran — defensive stops on an unstarted bus remain restartable.
- Lifecycle entry points re-check _disposed after acquiring the semaphore and
  map ObjectDisposedException on WaitAsync to a uniform nameof(Bus) exception.
- StopConsumingCoreAsync now catches OperationCanceledException, still disposes
  the consumer, and rethrows so hosts don't leak on shutdown cancel.

Refs: Medium — Bus lifecycle correctness (M1–M4)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Group 2 — DI wiring + dispatcher noise + interface nullability (Medium)

**Items:**
- M5: `MessageDispatcher` reports `Success=false` for untracked replies → retry/DLQ noise (`MessageDispatcher.cs:131-142`)
- M6: `ScanAssemblies(...)` is ignored when `ScanForMessageHandlers=false` (`ServiceCollectionExtensions.cs:303-309`)
- M7: Handler singleton guard misses factory-registered singletons (`ServiceCollectionExtensions.cs:320-341`)
- M21: `IMessageTypeRegistry.TryResolve` has non-nullable `out Type` (`IMessageTypeRegistry.cs:14`)

**Files:**
- Modify: `src/ServiceConnect/Services/MessageDispatcher.cs`
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.cs`
- Modify: `src/ServiceConnect.Interfaces/Messages/IMessageTypeRegistry.cs`
- Modify: `src/ServiceConnect/Services/MessageTypeRegistry.cs` (to match new signature)
- Modify/Create: unit tests for each.

---

### Task 2.1 — MessageDispatcher untracked-reply returns `Handled` not `Error` (M5)

- [ ] **Step 1: Read `MessageDispatcher.cs:131-142` + `ConsumeEventResult` definition.**

Confirm that the dispatcher currently returns `Success=false` with an `InvalidOperationException` when a reply arrives for a request that is no longer tracked (caller timed out, duplicate reply, etc.). Also read the immediate caller in `RabbitMqConsumerHost` to confirm `Success=false` currently drives nack/requeue.

- [ ] **Step 2: Write a failing test in `MessageDispatcherTests.cs`.**

```csharp
[Fact]
public async Task DispatchReply_ForUntrackedCorrelation_ReturnsSuccess()
{
    var dispatcher = MessageDispatcherTestFactory.Build(/* no pending request tracking */);
    var envelope = BuildReplyEnvelope(correlationId: Guid.NewGuid().ToString()); // unknown to manager

    var result = await dispatcher.DispatchAsync(envelope, CancellationToken.None);

    result.Success.ShouldBeTrue();
    result.NotHandled.ShouldBeFalse(); // NOT dispatched to a handler — but also not an error
    result.Exception.ShouldBeNull();
}
```

- [ ] **Step 3: Run, confirm failure.**

- [ ] **Step 4: Fix.**

In `MessageDispatcher.cs`, change the untracked-reply branch to return `Success=true` with a warning log. Optional: introduce a `NotHandled=true` flag or a new `Discarded` flag so callers who care can distinguish; minimum bar is to stop driving retries. Example:

```csharp
// inside the reply-dispatch branch:
if (!_replyManager.TryGetPending(correlationId, out var pending))
{
    _logger.LogDebug(
        "Discarding reply for untracked correlation '{CorrelationId}' (likely timed out or duplicate)",
        correlationId);
    return new ConsumeEventResult { Success = true };
}
```

- [ ] **Step 5: Rerun, confirm pass. Check that no existing `MessageDispatcherTests` assume the old behaviour.**

---

### Task 2.2 — ScanAssemblies short-circuit (M6)

- [ ] **Step 1: Read `ServiceCollectionExtensions.cs` `GetHandlerReferences` (301–310) and any callers.**

Confirm: `if (!builder.BusConfig.ScanForMessageHandlers) return [];` runs before `ScanAssembliesList` is inspected.

- [ ] **Step 2: Write a failing test in `ServiceCollectionExtensionsTests.cs`.**

```csharp
[Fact]
public void AddServiceConnect_ScansExplicitAssembliesEvenWhenDiscoveryDisabled()
{
    var services = new ServiceCollection();
    services.AddServiceConnect(b =>
    {
        b.BusConfig.ScanForMessageHandlers = false;
        b.ScanAssemblies(typeof(TestHandlerFixture).Assembly);
    });

    using var provider = services.BuildServiceProvider();
    var handler = provider.GetService<IMessageHandler<TestHandlerFixture.SampleMessage>>();
    handler.ShouldNotBeNull();
}
```

- [ ] **Step 3: Run, confirm failure** (handler not registered).

- [ ] **Step 4: Fix.**

Reverse the order so the explicit list wins over the flag:

```csharp
// inside GetHandlerReferences:
if (builder.ScanAssembliesList.Count > 0)
    return builder.ScanAssembliesList.ToArray();

if (!builder.BusConfig.ScanForMessageHandlers)
    return [];

return AppDomain.CurrentDomain.GetAssemblies();
```

Update the XML doc on the option to state: "When `ScanForMessageHandlers=false`, assemblies supplied via `ScanAssemblies(...)` are still scanned."

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 2.3 — Handler singleton guard misses factory descriptors (M7)

- [ ] **Step 1: Read `ServiceCollectionExtensions.cs:320-341`.**

Confirm: the guard loop filters `services.Where(d => d.ImplementationType == handlerType)`. Factory-registered singletons (`ImplementationFactory != null && ImplementationType == null`) slip through, then `TryAddEnumerable(Transient)` adds a second descriptor.

- [ ] **Step 2: Write a failing test in `ServiceCollectionExtensionsTests.cs`.**

```csharp
[Fact]
public void AddServiceConnect_DetectsFactoryRegisteredSingletonHandlers()
{
    var services = new ServiceCollection();
    services.AddSingleton<IMessageHandler<TestHandlerFixture.SampleMessage>>(_ => new TestHandlerFixture.SampleHandler());
    services.AddServiceConnect(b => b.ScanAssemblies(typeof(TestHandlerFixture).Assembly));

    using var provider = services.BuildServiceProvider();
    var handlers = provider.GetServices<IMessageHandler<TestHandlerFixture.SampleMessage>>().ToList();
    handlers.Count.ShouldBe(1); // user's factory singleton — no second transient copy.
}
```

- [ ] **Step 3: Run, confirm failure.**

- [ ] **Step 4: Fix.**

Extend the guard to also match factory descriptors. Two signals:

1. `d.ImplementationInstance` matches handler type via `is handlerType`.
2. `d.ImplementationFactory` — inspect the factory's return type via reflection is fragile; simpler to scan `services.Any(d => d.ServiceType == messageHandlerInterface)` and skip registration if ANY descriptor already answers that interface.

Preferred approach:

```csharp
// Before adding the scanned handler, check whether ANY descriptor already satisfies the interface.
if (services.Any(d => d.ServiceType == messageHandlerInterface))
    continue; // user-registered handler exists; respect their registration
```

This is stricter than the old check (it no longer allows multiple scan-registered handlers for the same message type via `TryAddEnumerable`). Confirm that ServiceConnect's dispatch model expects exactly one handler per message type — read `MessageDispatcher` to verify. If multiple are expected, fall back to matching on `(ImplementationType == handlerType) || (ImplementationInstance is { } inst && inst.GetType() == handlerType) || (ImplementationFactory is not null && serviceLifetime == Singleton)`.

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 2.4 — `IMessageTypeRegistry.TryResolve` nullable annotation (M21)

- [ ] **Step 1: Read `IMessageTypeRegistry.cs:14` and `MessageTypeRegistry.cs` `TryResolve`.**

Confirm: `bool TryResolve(string typeName, out Type type);` — no nullable annotation, but impl uses `types.TryGetValue(out type!)`.

- [ ] **Step 2: Write a failing / validating test in `MessageTypeRegistryTests.cs`.**

```csharp
[Fact]
public void TryResolve_Unknown_SetsOutParameterToNull()
{
    var registry = new MessageTypeRegistry();

    var success = registry.TryResolve("Unknown.TypeName", out Type? resolved);

    success.ShouldBeFalse();
    resolved.ShouldBeNull();
}
```

This will compile today only if we change the signature to nullable. Run the test first against the current signature — expect a compile error if `Type?` is used. That's the signal to apply the fix.

- [ ] **Step 3: Apply the fix.**

Update the interface:

```csharp
// IMessageTypeRegistry.cs
bool TryResolve(string typeName, [MaybeNullWhen(false)] out Type type);
```

Update `MessageTypeRegistry.cs` to match (remove `!` suppression):

```csharp
public bool TryResolve(string typeName, [MaybeNullWhen(false)] out Type type)
{
    return _types.TryGetValue(typeName, out type);
}
```

Add `using System.Diagnostics.CodeAnalysis;` at both files.

- [ ] **Step 4: Rerun. Build the entire solution and verify no new nullable warnings at call sites.**

```bash
dotnet build --nologo --verbosity minimal
```

If new warnings appear (call sites that assumed non-null on `false`), patch them — the audit flagged that core call sites pre-declare `Type? type = null` so happen to be safe; verify by grepping: `grep -rn "TryResolve" src/ --include="*.cs"`.

---

### Task 2.5 — Full unit test run, update doc, commit.

- [ ] **Step 1: Full test run.**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo --verbosity minimal
```

- [ ] **Step 2: Update doc.** Flip M5–M7 and M21, bump Medium counter to `8/22`.

- [ ] **Step 3: Commit.**

```bash
git add src/ServiceConnect/Services/MessageDispatcher.cs \
        src/ServiceConnect/ServiceCollectionExtensions.cs \
        src/ServiceConnect/Services/MessageTypeRegistry.cs \
        src/ServiceConnect.Interfaces/Messages/IMessageTypeRegistry.cs \
        src/ServiceConnect.UnitTests/MessageDispatcherTests.cs \
        src/ServiceConnect.UnitTests/ServiceCollectionExtensionsTests.cs \
        src/ServiceConnect.UnitTests/MessageTypeRegistryTests.cs \
        consolodated-issues/2026-04-22-consolidated-issues.md
git commit -m "$(cat <<'EOF'
fix(core): dispatcher/DI correctness + IMessageTypeRegistry nullability

- MessageDispatcher no longer returns Success=false for untracked replies —
  a stale/duplicate reply logs at Debug and is silently acked, eliminating
  spurious retry/DLQ churn in long-running request/reply flows.
- AddServiceConnect honours ScanAssemblies(...) even when
  ScanForMessageHandlers=false, matching the documented "scan these and
  nothing else" contract.
- Handler singleton-guard now detects factory-registered singletons (and
  any pre-existing registration for the interface), preventing accidental
  double-dispatch via TryAddEnumerable.
- IMessageTypeRegistry.TryResolve is now [MaybeNullWhen(false)] out Type? type,
  so external consumers get correct nullable flow analysis.

Refs: Medium — DI/dispatcher/nullability (M5, M6, M7, M21)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Group 3 — RabbitMQ publisher hardening (Medium)

**Items:**
- M8: `Producer.DisposeAsync` leaks connection/channel on lock-acquire timeout (`Producer.cs:390-426`)
- M9: Audit publish silently drops on non-empty routing key (`MessageAuditPublisher.cs:39-45`)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Producer.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs`
- Create/Modify: `src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs` and `MessageAuditPublisherTests.cs`

---

### Task 3.1 — Producer dispose leak on lock timeout (M8)

- [ ] **Step 1: Read `Producer.cs:390-426`.**

Confirm: the 30-second `disposeTimeout` waits on `_publishLock` and `_connectionSemaphore`; if either times out, the method logs and returns early WITHOUT tearing down channel/connection.

- [ ] **Step 2: Write a failing test.**

Create `src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs`:

```csharp
[Fact]
public async Task DisposeAsync_WhenPublishLockHeld_StillDisposesChannelAndConnection()
{
    var channel = new Mock<IChannel>();
    var connection = new Mock<IConnection>();
    var connectionFactory = new Mock<IConnectionFactory>();
    connectionFactory.Setup(f => f.CreateConnectionAsync(It.IsAny<CancellationToken>()))
                     .ReturnsAsync(connection.Object);
    connection.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(channel.Object);

    var producer = new Producer(/* wiring with disposeTimeout=200ms */);
    await producer.InitializeAsync();

    // Simulate a stuck publish holding _publishLock.
    // (Depends on Producer internals — if a private field can be set via reflection or
    //  test-only constructor, use that; otherwise this test needs an integration harness.)

    await producer.DisposeAsync();

    channel.Verify(c => c.CloseAsync(It.IsAny<CancellationToken>()), Times.Once);
    connection.Verify(c => c.CloseAsync(It.IsAny<CancellationToken>()), Times.Once);
}
```

If reflection-based field injection is unpalatable, write the test so it proves the POST-fix behaviour: dispose ALWAYS closes channel + connection in a finally, regardless of lock-acquire outcome. The test can drive this via a small refactor that extracts the teardown into a method.

- [ ] **Step 3: Run, confirm failure.**

- [ ] **Step 4: Fix.**

Refactor `DisposeAsync`:

```csharp
public async ValueTask DisposeAsync()
{
    var disposeTimeout = TimeSpan.FromSeconds(30);
    bool publishAcquired = false, connectionAcquired = false;
    try
    {
        publishAcquired = await _publishLock.WaitAsync(disposeTimeout).ConfigureAwait(false);
        connectionAcquired = await _connectionSemaphore.WaitAsync(disposeTimeout).ConfigureAwait(false);

        if (!publishAcquired || !connectionAcquired)
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
        try { if (_channel is not null) await _channel.CloseAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Producer channel close failed during dispose"); }

        try { if (_connection is not null) await _connection.CloseAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Producer connection close failed during dispose"); }

        if (publishAcquired) _publishLock.Release();
        if (connectionAcquired) _connectionSemaphore.Release();

        _publishLock.Dispose();
        _connectionSemaphore.Dispose();
    }
}
```

Key change: teardown is in `finally`, not inside an early-return branch. This means an in-flight publish with a stuck `BasicPublishAsync` will still have its channel aborted on dispose (channel close cancels the pending publish, which is correct behaviour for shutdown).

- [ ] **Step 5: Rerun, confirm pass. Verify no existing producer tests regress:**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
  --filter "FullyQualifiedName~Producer" --nologo --verbosity minimal
```

---

### Task 3.2 — Audit publish silent drop (M9)

- [ ] **Step 1: Read `MessageAuditPublisher.cs:39-45`.**

Confirm: publish uses `_queueConfiguration.AuditQueueName` as exchange, `AuditRoutingKey ?? ""` as routing key, and `mandatory: false`. Also read the topology setup to confirm the audit direct-exchange is bound to the audit queue with an empty routing key.

- [ ] **Step 2: Write a failing test in `MessageAuditPublisherTests.cs`.**

```csharp
[Fact]
public async Task PublishAsync_WithNonEmptyRoutingKey_UsesEmptyRoutingKeyOrRespectsBinding()
{
    var channel = new Mock<IChannel>();
    var publisher = new MessageAuditPublisher(
        channel.Object,
        new QueueConfiguration { AuditQueueName = "audit", AuditRoutingKey = "telemetry.v1" },
        NullLogger<MessageAuditPublisher>.Instance);

    await publisher.PublishAsync(new AuditMessage { /* ... */ });

    // Today: BasicPublishAsync is called with routingKey="telemetry.v1" against an empty-binding
    // direct exchange → unroutable, mandatory=false → silently dropped.
    // Desired: either routingKey is forced to "" (match the binding), OR mandatory=true + a
    // BasicReturn handler that logs. Pick ONE and assert it.

    channel.Verify(c => c.BasicPublishAsync(
        It.IsAny<string>(),
        /* routingKey */ "",            // post-fix: forced to empty to match binding
        /* mandatory */ false,
        It.IsAny<BasicProperties>(),
        It.IsAny<ReadOnlyMemory<byte>>(),
        It.IsAny<CancellationToken>()), Times.Once);
}
```

- [ ] **Step 3: Run, confirm failure.**

- [ ] **Step 4: Fix.**

Two valid approaches — pick A unless a breaking-change review requires B:

**Option A (force empty routing key):** Ignore `AuditRoutingKey` when publishing because the topology binds with an empty key. Update XML doc on `AuditRoutingKey` to document it's reserved for future use. Emit a debug log if a non-empty value is supplied so operators know.

**Option B (mandatory publish + return handler):** Set `mandatory: true` on the BasicPublish. Subscribe to `_channel.BasicReturnAsync` at setup and log any returned audit messages at Warning. Use `AuditRoutingKey` as-is.

Option A is less invasive and preserves the audit-as-fire-and-forget semantics. Implement A:

```csharp
// inside PublishAsync, replacing the current call:
await _channel.BasicPublishAsync(
    exchange: _queueConfiguration.AuditExchangeName ?? _queueConfiguration.AuditQueueName,
    routingKey: string.Empty, // audit direct-exchange binds with empty routing key
    mandatory: false,
    basicProperties: properties,
    body: body,
    cancellationToken: cancellationToken).ConfigureAwait(false);
```

If `_queueConfiguration.AuditRoutingKey` is non-null and non-empty, log a one-time Warning on publisher construction (not per-publish).

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 3.3 — Full test run, update doc, commit.

- [ ] **Step 1: Unit test run.**

- [ ] **Step 2: Update doc.** Flip M8, M9. Bump counter to `10/22`.

- [ ] **Step 3: Commit.**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Producer.cs \
        src/ServiceConnect.Client.RabbitMQ/MessageAuditPublisher.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ProducerDisposeTests.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/MessageAuditPublisherTests.cs \
        consolodated-issues/2026-04-22-consolidated-issues.md
git commit -m "$(cat <<'EOF'
fix(rabbitmq): prevent Producer dispose leak and audit silent-drop

- Producer.DisposeAsync now tears down channel + connection in a finally block
  regardless of whether the publish/connection locks were acquired within the
  30-second timeout, so a stuck BasicPublishAsync no longer zombies the
  underlying TCP connection for the process lifetime.
- MessageAuditPublisher forces routingKey="" (matching the empty-key binding
  of the audit direct exchange) and logs a Warning at construction when a
  non-empty AuditRoutingKey is configured, eliminating silent drops.

Refs: Medium — RabbitMQ publisher hardening (M8, M9)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Group 4 — RabbitMQ consumer lifecycle & idempotency (Medium)

**Items:**
- M11: `Consumer.DisposeAsync` only swallows `ObjectDisposedException` (`Consumer.cs:181-184`)
- M12: `Consumer.StartConsumingAsync` is not idempotent — `_clients` bag never cleared (`Consumer.cs:78-174`)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/Consumer.cs`
- Modify/Create: `src/ServiceConnect.UnitTests/RabbitMQ/ConsumerTests.cs`

---

### Task 4.1 — Consumer.DisposeAsync exception resilience (M11)

- [ ] **Step 1: Read `Consumer.cs:181-184`.**

Confirm: the `foreach` over hosts has `catch (ObjectDisposedException) { }` only. Any other exception (`TimeoutException`, `OperationInterruptedException`, broker errors) aborts the loop, leaking the remaining hosts.

- [ ] **Step 2: Add a failing test.**

```csharp
[Fact]
public async Task DisposeAsync_WhenFirstHostThrows_StillDisposesRemainingHosts()
{
    var hostA = new Mock<IAsyncDisposable>();
    hostA.Setup(h => h.DisposeAsync()).Throws(new TimeoutException("AMQP 0-9-1 channel closed"));
    var hostB = new Mock<IAsyncDisposable>();
    hostB.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

    var consumer = ConsumerTestFactory.BuildWithHosts(hostA.Object, hostB.Object);

    await consumer.DisposeAsync(); // must not throw

    hostB.Verify(h => h.DisposeAsync(), Times.Once);
}
```

- [ ] **Step 3: Run, confirm failure** (B never disposed).

- [ ] **Step 4: Fix.**

```csharp
foreach (var client in _clients)
{
    try
    {
        await client.DisposeAsync().ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        throw; // let shutdown cancellation propagate
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to dispose consumer host '{ConsumerTag}' — continuing", client.ConsumerTag);
    }
}
```

`OperationCanceledException` still propagates — the broader loop should fail fast on shutdown cancel. All other exceptions are swallowed and logged.

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 4.2 — Consumer.StartConsumingAsync idempotency (M12)

- [ ] **Step 1: Read `Consumer.cs:78-174`.**

Confirm: `_clients` is populated via `_clients.Add(client)` at line 167, never cleared. A second `StartConsumingAsync` call on the same Consumer doubles the client list.

- [ ] **Step 2: Write a failing test.**

```csharp
[Fact]
public async Task StartConsumingAsync_CalledTwice_DoesNotDoubleClients()
{
    var consumer = ConsumerTestFactory.BuildWithFakeChannel();

    await consumer.StartConsumingAsync(/* args */);
    await consumer.StartConsumingAsync(/* args */);

    consumer.ClientCount.ShouldBe(1); // or throw on second call — see fix options
}
```

- [ ] **Step 3: Run, confirm failure.**

- [ ] **Step 4: Fix.**

Two options:

**Option A (throw on double-start):** Add a state flag `_started`. If already `true`, throw `InvalidOperationException("Consumer already consuming; call StopConsumingAsync before restarting")`. Safest — matches Bus-level gating.

**Option B (silent idempotent):** If `_clients.Count > 0`, return early.

Prefer **Option A** — idempotency via silent no-op hides programming errors. The Bus wraps its own calls in `_consuming`/`_stopped` gating, so direct consumer users are the risk; they deserve a clear error.

```csharp
private int _started; // 0 = not started, 1 = started

public async Task StartConsumingAsync(/* existing args */)
{
    if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        throw new InvalidOperationException(
            "Consumer is already consuming. Call StopConsumingAsync before starting again.");

    // ... existing body ...
}
```

Also update `DisposeAsync` / `StopConsumingAsync` to reset `_started` to 0 (under the same memory fence) so that after a clean stop, a future `StartConsumingAsync` can legitimately succeed.

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 4.3 — Full test run, update doc, commit.

- [ ] **Step 1: Unit test run.**

- [ ] **Step 2: Update doc.** Flip M11, M12. Bump counter to `12/22`.

- [ ] **Step 3: Commit.**

```bash
git add src/ServiceConnect.Client.RabbitMQ/Consumer.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/ConsumerTests.cs \
        consolodated-issues/2026-04-22-consolidated-issues.md
git commit -m "$(cat <<'EOF'
fix(rabbitmq): consumer dispose resilience + start-consuming idempotency

- Consumer.DisposeAsync now swallows non-cancellation exceptions during host
  teardown and continues the loop. TimeoutException / OperationInterruptedException
  from a broken broker no longer leaks the remaining hosts.
- StartConsumingAsync guards against double-start via Interlocked CAS and throws
  InvalidOperationException if already consuming; StopConsumingAsync / DisposeAsync
  reset the flag so a clean stop-then-start remains valid.

Refs: Medium — RabbitMQ consumer lifecycle (M11, M12)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Group 5 — RabbitMQ consumer input validation & broker observability (Medium)

**Items:**
- M10: RetryCount header not decoded before parsing → retries broken for interop (`MessageRetryHandler.cs:39-55`)
- M13: No subscription to broker-initiated `basic.cancel` / connection events (`RabbitMqConsumerHost.cs:134-137`)
- M14: Null-valued `TypeName` header survives admission but crashes dispatch (`RabbitMqConsumerHost.cs:166-168, 328-329`)

**Files:**
- Modify: `src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs`
- Modify: `src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs`
- Modify: `src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerTests.cs`
- Modify: `src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostTests.cs`

---

### Task 5.1 — RetryCount header decode (M10)

- [ ] **Step 1: Read `MessageRetryHandler.cs:39-55`.**

Confirm: `raw` is fetched from headers, then `int.TryParse(raw.ToString(), ...)`. No `HeaderDecoder.Decode(raw)` — so `byte[]`-valued headers (from non-.NET clients) yield `"System.Byte[]"`.

- [ ] **Step 2: Add a failing test in `MessageRetryHandlerTests.cs`.**

```csharp
[Fact]
public void TryGetRetryCount_WhenHeaderValueIsByteArray_DecodesAndParses()
{
    // Non-.NET producers stamp the RetryCount header as an AMQP string → byte[] on the wire.
    var utf8 = System.Text.Encoding.UTF8.GetBytes("3");
    var headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = utf8 };

    var handler = new MessageRetryHandler(/* deps */);
    var count = handler.GetRetryCount(headers);

    count.ShouldBe(3);
}

[Fact]
public void TryGetRetryCount_WhenHeaderValueIsInt_ReturnsInt()
{
    var headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = 5 };

    var handler = new MessageRetryHandler(/* deps */);
    var count = handler.GetRetryCount(headers);

    count.ShouldBe(5);
}
```

If `GetRetryCount` is private, use an internal helper plus `InternalsVisibleTo`; both are already used in this codebase.

- [ ] **Step 3: Run, confirm the `byte[]` case fails.**

- [ ] **Step 4: Fix.**

Insert the decode step:

```csharp
// inside MessageRetryHandler, where raw is obtained:
if (!headers.TryGetValue(HeaderKeys.RetryCount, out var raw))
    return 0;

var decoded = HeaderDecoder.Decode(raw);
if (decoded is null)
    return 0;

return int.TryParse(decoded, out var parsed) && parsed >= 0 ? parsed : -1;
```

Preserve the existing `int`-direct shortcut if `raw is int i` for performance; the decode path is for string-wire headers.

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 5.2 — Null-valued TypeName admission (M14)

- [ ] **Step 1: Read `RabbitMqConsumerHost.cs:166-168` and `328-329`.**

Confirm: admission uses `Headers.ContainsKey(TypeName)` → admits a key with null value. Dispatch-site `headers[TypeName]` throws `KeyNotFoundException` (because `CopyInboundHeaders` skips nulls), caught by outer `catch`, routing to retry/error.

- [ ] **Step 2: Add a failing test.**

```csharp
[Fact]
public async Task HandleMessage_WhenTypeNameHeaderValueIsNull_RejectsAtAdmission()
{
    var host = RabbitMqConsumerHostTestFactory.Build();
    var deliveryArgs = new BasicDeliverEventArgs
    {
        BasicProperties = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                [HeaderKeys.TypeName] = null, // malformed but not-missing
            },
        },
        // ... other required fields ...
    };

    var result = await host.InvokeReceivedAsync(deliveryArgs);

    result.ShouldBe(ConsumerResult.Rejected);
    host.RetryCount.ShouldBe(0); // no retry budget burned
}
```

The test will require a test-friendly façade over `RabbitMqConsumerHost`'s `ReceivedAsync` — check the existing test class for an established pattern. If none exists, introduce a minimal one (the receive handler wrapped in a public-for-testing method).

- [ ] **Step 3: Run, confirm failure.**

- [ ] **Step 4: Fix.**

Update the admission check to also require non-null values:

```csharp
// replace the admission check at RabbitMqConsumerHost.cs:166-168
var headers = args.BasicProperties.Headers;
if (headers == null)
    return Reject(args, "Message has no headers");

bool HasNonNullValue(string key) => headers.TryGetValue(key, out var v) && v is not null;

if (!HasNonNullValue(HeaderKeys.TypeName) && !HasNonNullValue(HeaderKeys.FullTypeName))
    return Reject(args, "Message has no TypeName / FullTypeName header");
```

Rejecting via dead-letter or error-exchange straight from admission is better than burning retry budget on a guaranteed-fail path.

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 5.3 — Broker-initiated cancel / connection events (M13)

- [ ] **Step 1: Read `RabbitMqConsumerHost.cs:134-137` where `AsyncEventingBasicConsumer` is instantiated.**

Confirm: only `ReceivedAsync` is subscribed. No `_consumer.ShutdownAsync`, no `IConnection.ConnectionShutdownAsync` / `ConnectionBlockedAsync` / `ChannelShutdownAsync` subscriptions.

- [ ] **Step 2: Write a failing test.**

This is hard to unit-test purely — the broker-initiated cancel requires a real channel. Prefer an E2E integration test:

Create `src/ServiceConnect.EndToEndTests/Consumers/BrokerInitiatedCancelTests.cs`:

```csharp
public class BrokerInitiatedCancelTests(MessagingFixture fixture) : IClassFixture<MessagingFixture>
{
    [Fact]
    public async Task Consumer_WhenBrokerDeletesQueue_LogsAndExitsGracefully()
    {
        var consumer = await fixture.StartConsumerAsync(queueName: "test-q");

        // Delete the queue out from under the consumer to trigger basic.cancel.
        await fixture.Channel.QueueDeleteAsync("test-q");

        // Wait for the host to observe the cancel and transition to a non-consuming state.
        await consumer.WaitForShutdownAsync(TimeSpan.FromSeconds(10));

        consumer.IsConsuming.ShouldBeFalse();
        consumer.LastShutdownReason.ShouldNotBeNull();
    }
}
```

If `MessagingFixture` doesn't expose a management channel for `QueueDeleteAsync`, wire one into the fixture.

- [ ] **Step 3: Run the E2E test under docker:**

```bash
sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj \
  --filter "FullyQualifiedName~BrokerInitiatedCancelTests" --nologo --verbosity minimal'
```

Expected: FAIL or TIMEOUT — consumer host doesn't observe the cancel.

- [ ] **Step 4: Fix in `RabbitMqConsumerHost.cs`.**

Subscribe to the relevant events at consumer creation time:

```csharp
_consumer = new AsyncEventingBasicConsumer(_model);
_consumer.ReceivedAsync += async (sender, args) =>
    await EventAsync(sender, args, deliveryToken).ConfigureAwait(false);
_consumer.ShutdownAsync += OnConsumerShutdownAsync;
_model.ChannelShutdownAsync += OnChannelShutdownAsync;
_connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
_connection.ConnectionBlockedAsync += OnConnectionBlockedAsync;
_connection.ConnectionUnblockedAsync += OnConnectionUnblockedAsync;

// ... handlers log at Warning and transition state as appropriate ...
private Task OnConsumerShutdownAsync(object? sender, ShutdownEventArgs args)
{
    _logger.LogWarning(
        "AMQP consumer '{ConsumerTag}' shutdown: {ReplyCode} {ReplyText}",
        _consumerTag, args.ReplyCode, args.ReplyText);
    _isConsuming = false;
    return Task.CompletedTask;
}
```

Unsubscribe in `DisposeAsync` to avoid handler leaks across restarts.

- [ ] **Step 5: Rerun the E2E test, confirm pass.**

- [ ] **Step 6: Also add a unit test that verifies the handler list is attached on construction** (Moq on `IChannel` / `IConnection` event add).

---

### Task 5.4 — Full test run, update doc, commit.

- [ ] **Step 1: Unit + E2E runs.**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo --verbosity minimal
sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj \
  --filter "FullyQualifiedName~BrokerInitiatedCancelTests|FullyQualifiedName~MessageRetryHandlerTests|FullyQualifiedName~RabbitMqConsumerHostTests" \
  --nologo --verbosity minimal'
```

- [ ] **Step 2: Update doc.** Flip M10, M13, M14. Bump counter to `15/22`.

- [ ] **Step 3: Commit.**

```bash
git add src/ServiceConnect.Client.RabbitMQ/MessageRetryHandler.cs \
        src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/MessageRetryHandlerTests.cs \
        src/ServiceConnect.UnitTests/RabbitMQ/RabbitMqConsumerHostTests.cs \
        src/ServiceConnect.EndToEndTests/Consumers/BrokerInitiatedCancelTests.cs \
        consolodated-issues/2026-04-22-consolidated-issues.md
git commit -m "$(cat <<'EOF'
fix(rabbitmq): consumer input validation + broker event subscriptions

- MessageRetryHandler decodes RetryCount via HeaderDecoder before parsing, so
  byte[]-wire headers from non-.NET clients round-trip correctly instead of
  parsing "System.Byte[]" and routing straight to the error exchange.
- RabbitMqConsumerHost admission now rejects messages whose TypeName /
  FullTypeName header exists but is null-valued — preventing the downstream
  KeyNotFoundException that used to burn retry budget on a guaranteed-fail
  dispatch path.
- RabbitMqConsumerHost subscribes to AsyncEventingBasicConsumer.ShutdownAsync
  plus IConnection ConnectionShutdownAsync / ConnectionBlockedAsync and IChannel
  ChannelShutdownAsync; handlers log at Warning and mark the host non-consuming
  so supervisors observe broker-initiated cancellation instead of silent stalls.

Refs: Medium — RabbitMQ consumer validation + observability (M10, M13, M14)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Group 6 — Persistence correctness (Medium)

**Items:**
- M15: `ProcessManagerFinder` index marker flipped before `CreateOneAsync` awaits (`MongoDbProcessManagerFinder.cs:274-290`)
- M16: `ProcessManagerFinder` does not handle `MongoCommandException 85/86` on concurrent index creation (`MongoDbProcessManagerFinder.cs:274-289`)
- M17: `UpdateDataAsync` silently swallows `w:0` conflicts; `DeleteDataAsync` spuriously throws (`MongoDbProcessManagerFinder.cs:211-221, 259-271`)
- M18: `Provider.Keys()` races with external `IKeyValueStore` callers → NRE (`InMemoryProcessManagerFinder.cs:93-105`)

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`
- Modify: `src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderTests.cs`
- Modify: `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderTests.cs`

---

### Task 6.1 — Mongo PMF index-marker ordering (M15 + M16)

- [ ] **Step 1: Read `MongoDbProcessManagerFinder.cs:274-290` and `MongoDbTimeoutStore.cs:389-403` for comparison.**

Confirm:
- PMF: `TryAdd(name, true)` returns early if already indexed; proceeds to `CreateOneAsync`; bare catch rolls back marker and rethrows.
- TimeoutStore: does something different (check exact pattern — the audit references line range 386-392 for the 85/86 handling).

- [ ] **Step 2: Write a failing test.**

E2E test (requires real Mongo to exercise concurrent unique-index creation):

```csharp
[Fact]
public async Task EnsureIndex_ConcurrentCallers_AllSeeIndexBeforeInsertSucceeds()
{
    var finder = new MongoDbProcessManagerFinder(/* Testcontainers-backed Mongo */);

    var correlationId = Guid.NewGuid();
    var tasks = Enumerable.Range(0, 10)
        .Select(_ => Task.Run(async () =>
        {
            var pm = new TestProcessManager { CorrelationId = correlationId, Data = "A" };
            try
            {
                await finder.InsertDataAsync(pm);
            }
            catch (ConcurrencyException)
            {
                // Expected: unique-index enforcement — only ONE of the 10 succeeds.
            }
        }))
        .ToList();

    await Task.WhenAll(tasks);

    var count = await finder.CountByCorrelationAsync(correlationId);
    count.ShouldBe(1);
}
```

- [ ] **Step 3: Run under docker, confirm failure** (may admit 2+ rows because marker flipped before index creation).

- [ ] **Step 4: Fix.**

Restructure the index-creation method so the marker is flipped AFTER `CreateOneAsync` succeeds, and known-benign errors are treated as success:

```csharp
private static readonly HashSet<int> BenignIndexCodes = new() { 85, 86 }; // IndexOptionsConflict, IndexKeySpecsConflict

private async Task EnsureIndexesAsync(IMongoCollection<BsonDocument> collection, CancellationToken cancellationToken)
{
    var name = collection.CollectionNamespace.CollectionName;
    if (_indexedCollections.ContainsKey(name)) return;

    await _indexCreationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        if (_indexedCollections.ContainsKey(name)) return; // double-check under lock

        try
        {
            await collection.Indexes.CreateOneAsync(/* existing index model */, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException ex) when (BenignIndexCodes.Contains(ex.Code))
        {
            // Another process won the race and created a compatible index; proceed as success.
            _logger.LogDebug("Concurrent index creation for '{Name}': {Code} {Message}", name, ex.Code, ex.Message);
        }

        _indexedCollections.TryAdd(name, true); // flag ONLY after creation (or benign conflict) succeeded
    }
    finally
    {
        _indexCreationSemaphore.Release();
    }
}
```

Declare `_indexCreationSemaphore = new SemaphoreSlim(1, 1)` on the class. The semaphore serialises index creation across threads; the double-check avoids re-paying the cost.

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 6.2 — PMF Update/Delete w:0 asymmetry (M17)

- [ ] **Step 1: Read `MongoDbProcessManagerFinder.cs:211-221` (Update) and `259-271` (Delete).**

Confirm:
- Update: `if (result.IsAcknowledged && result.ModifiedCount == 0) throw ConcurrencyException(...)` — silent under `w:0`.
- Delete: `if (result.DeletedCount == 0) throw` — always throws under `w:0`.

- [ ] **Step 2: Write failing tests.**

This is niche (only affects users who explicitly run w:0). Test the post-fix behaviour with an integration test that uses w:0 write concern:

```csharp
[Fact]
public async Task UpdateDataAsync_WithW0_WarnsInsteadOfSilentlySucceeding()
{
    var finder = new MongoDbProcessManagerFinder(/* Testcontainers-backed Mongo, WriteConcern.Unacknowledged */);
    // ... test body that creates a pm, modifies version mismatch, calls UpdateDataAsync ...

    // Post-fix: Update under w:0 logs a one-time Warning at startup that concurrency
    // guarantees are disabled, OR refuses the operation. Pick ONE.
}

[Fact]
public async Task DeleteDataAsync_WithW0_DoesNotSpuriouslyThrow()
{
    var finder = new MongoDbProcessManagerFinder(/* WriteConcern.Unacknowledged */);
    // ... create a row, call DeleteDataAsync ...

    // Must not throw — under w:0 the driver returns DeletedCount=0 by design.
}
```

- [ ] **Step 3: Fix.**

Preferred fix: detect `WriteConcern.Unacknowledged` at collection-bind time, log a one-time Warning stating that optimistic-concurrency checks are disabled, and short-circuit the `ModifiedCount`/`DeletedCount` assertions. This is honest — w:0 cannot deliver the guarantees these checks encode.

```csharp
private readonly bool _concurrencyGuardsEnabled; // = collection.WriteConcern.IsAcknowledged;

// Update:
if (_concurrencyGuardsEnabled && result.IsAcknowledged && result.ModifiedCount == 0)
    throw new ConcurrencyException(...);

// Delete:
if (_concurrencyGuardsEnabled && result.DeletedCount == 0)
    throw new ConcurrencyException(...);
```

Log once at finder construction if `_concurrencyGuardsEnabled == false`.

- [ ] **Step 4: Rerun, confirm pass.**

---

### Task 6.3 — InMemory Provider.Keys race (M18)

- [ ] **Step 1: Read `InMemoryProcessManagerFinder.cs:93-105` + registration wiring.**

Confirm: finder takes an `ICacheProvider`; the same provider is registered as `IKeyValueStore`; finder's read lock does not cover external `IKeyValueStore` calls; `Keys()` snapshot + subsequent `Get(key)` can race with an external `Remove(key)` so `Get` returns null → fallback at line 105 calls `value.GetType()` on null → NRE.

- [ ] **Step 2: Write a failing test.**

```csharp
[Fact]
public async Task FindMatching_ConcurrentKeyValueRemoval_DoesNotNre()
{
    var provider = new CacheProvider(); // the class under test
    var finder = new InMemoryProcessManagerFinder(provider);
    var kvStore = (IKeyValueStore)provider;

    // Seed some process-manager data.
    await finder.InsertDataAsync(new TestProcessManager { CorrelationId = Guid.NewGuid() });
    await finder.InsertDataAsync(new TestProcessManager { CorrelationId = Guid.NewGuid() });

    // Concurrent: removals via IKeyValueStore while finder is scanning.
    var scanTask = Task.Run(() => finder.FindMatching(m => true).ToList());
    var removeTask = Task.Run(() =>
    {
        foreach (var k in kvStore.Keys()) kvStore.Remove(k);
    });

    // Must not throw.
    await Task.WhenAll(scanTask, removeTask);
}
```

- [ ] **Step 3: Run, confirm failure (NRE).**

- [ ] **Step 4: Fix.**

Two options:

**Option A (null-check the fallback):** In the `FindMatching` loop, if `Get(key)` returns null, `continue` — the key was removed mid-scan. Simple, preserves external-writer concurrency.

**Option B (broaden the lock):** Make `IKeyValueStore` operations acquire the same `_state.SyncRoot`. Invasive — external callers no longer get lock-free reads.

Prefer **A**:

```csharp
foreach (var key in keys)
{
    var value = _provider.Get(key);
    if (value is null) continue; // removed concurrently by an external IKeyValueStore caller
    // ... existing predicate check ...
}
```

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 6.4 — Full test run, update doc, commit.

- [ ] **Step 1: Unit + E2E runs.**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --nologo --verbosity minimal
sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj \
  --filter "FullyQualifiedName~MongoDbProcessManagerFinder" --nologo --verbosity minimal'
```

- [ ] **Step 2: Update doc.** Flip M15, M16, M17, M18. Bump counter to `19/22`.

- [ ] **Step 3: Commit.**

```bash
git add src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs \
        src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs \
        src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderTests.cs \
        src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderTests.cs \
        consolodated-issues/2026-04-22-consolidated-issues.md
git commit -m "$(cat <<'EOF'
fix(persistence): ProcessManagerFinder correctness + InMemory KV race

- Mongo PMF serialises index creation under a semaphore and flips the
  _indexedCollections marker only AFTER CreateOneAsync succeeds (or returns
  a benign 85/86 conflict), closing the "marker-wins, index-doesn't-exist"
  window that could admit duplicate CorrelationIds.
- PMF Update/Delete detect WriteConcern.Unacknowledged at construction,
  log a one-time Warning that optimistic-concurrency guarantees are
  disabled under w:0, and skip the ModifiedCount / DeletedCount assertions
  that are undefined under that write concern.
- InMemory finder null-checks the Get(key) fallback so concurrent
  IKeyValueStore removals between the Keys() snapshot and per-key Get
  no longer NRE.

Refs: Medium — Persistence correctness (M15, M16, M17, M18)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Group 7 — Telemetry trace-context & status (Medium)

**Items:** (the M-numbering here is local to this plan; each bullet maps to a `[ ]` in the consolidated-issues Telemetry section)
- M19: `Send` skips trace-context injection when `Message` is null (`ServiceConnectActivitySource.cs:169-176`)
- M20: Trace context not injected when `EnablePublishTelemetry`/`EnableSendTelemetry=false` (`ServiceConnectActivitySource.cs:56, 156, 239-243`)
- M22: Activity status never set on failure → error dashboards useless (`ServiceConnectActivitySource.cs:46-181`)

(M21 was bundled into Group 2 alongside the DI/dispatcher fixes because it's a single-line interface annotation change; that's why this group skips M21.)

**Files:**
- Modify: `src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs`
- Modify: `src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs`

---

### Task 7.1 — `Send` null-message should still inject trace context (M19)

- [ ] **Step 1: Read `ServiceConnectActivitySource.cs:149-180` (`Send` path) and compare with `Publish`.**

Confirm: `Send` returns early on `eventArgs.Message is null` BEFORE `InjectTraceContext`. `Publish` does not have this early-return.

- [ ] **Step 2: Write a failing test.**

```csharp
[Fact]
public void Send_WithNullMessage_StillInjectsTraceparentHeader()
{
    using var listener = new ActivityListener { ShouldListenTo = _ => true, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData };
    ActivitySource.AddActivityListener(listener);

    var source = new ServiceConnectActivitySource(/* opts with EnableSendTelemetry=true */);
    var headers = new Dictionary<string, string>();
    var args = new SendEventArgs { Message = null, Headers = headers };

    using var outerActivity = new ActivitySource("test").StartActivity("outer", ActivityKind.Client);
    source.Send(args);

    headers.ShouldContainKey("traceparent");
}
```

- [ ] **Step 3: Run, confirm failure.**

- [ ] **Step 4: Fix.**

Move the null-message check AFTER `InjectTraceContext`, or — simpler — inject before the early-return:

```csharp
public Activity? Send(SendEventArgs eventArgs, ActivityContext linkedContext = default)
{
    // Always inject trace context so distributed tracing bridges the broker regardless of payload shape.
    InjectTraceContext(Activity.Current, eventArgs.Headers);

    if (eventArgs.Message is null)
        return null;

    var activity = /* StartActivity as before */;
    // ... rest unchanged ...
    return activity;
}
```

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 7.2 — Inject trace context when telemetry is disabled (M20)

- [ ] **Step 1: Read `ServiceConnectActivitySource.cs:46-80` (`Publish`), `:149-180` (`Send`), `:239-243` (`InjectTraceContext`).**

Confirm: `StartActivity` returns null when `_options.EnablePublishTelemetry/EnableSendTelemetry=false`; each method returns before `InjectTraceContext` runs.

- [ ] **Step 2: Write a failing test.**

```csharp
[Fact]
public void Publish_WhenPublishTelemetryDisabled_StillInjectsTraceparentFromAmbient()
{
    var source = new ServiceConnectActivitySource(new TelemetryOptions { EnablePublishTelemetry = false });
    var headers = new Dictionary<string, string>();

    using var outerActivity = new ActivitySource("ambient").StartActivity("outer", ActivityKind.Server);
    // Simulate an ASP.NET-provided outer activity.

    source.Publish(new PublishEventArgs { Headers = headers });

    headers.ShouldContainKey("traceparent"); // outer context crosses the broker boundary.
}
```

- [ ] **Step 3: Run, confirm failure.**

- [ ] **Step 4: Fix.**

Decouple context injection from activity creation:

```csharp
public Activity? Publish(PublishEventArgs eventArgs, ActivityContext linkedContext = default)
{
    // Inject ambient trace context unconditionally so outer (ASP.NET / OTel) spans
    // propagate across the broker even when ServiceConnect's own spans are disabled.
    InjectTraceContext(Activity.Current, eventArgs.Headers);

    if (!_options.EnablePublishTelemetry)
        return null;

    var activity = /* existing StartActivity */;
    // ... existing body ...
}
```

Same shape for `Send`.

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 7.3 — Activity status on failure (M22)

- [ ] **Step 1: Read `ServiceConnectActivitySource.cs:46-181`.**

Confirm: no `SetStatus(Error)` / `AddException` / `SetTag("otel.status_code", ...)` anywhere in Publish / Send / Consume paths. Failures render as Unset (OTel backends display as Ok).

- [ ] **Step 2: Write a failing test.**

```csharp
[Fact]
public void PublishFailed_SetsActivityStatusToError()
{
    using var listener = new ActivityListener { ShouldListenTo = s => s.Name == "ServiceConnect", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData };
    Activity? captured = null;
    listener.ActivityStopped = a => captured ??= a;
    ActivitySource.AddActivityListener(listener);

    var source = new ServiceConnectActivitySource(/* options with EnablePublishTelemetry=true */);
    var activity = source.Publish(new PublishEventArgs());
    var ex = new InvalidOperationException("publish failed");

    source.PublishFailed(activity, ex);

    captured.ShouldNotBeNull();
    captured.Status.ShouldBe(ActivityStatusCode.Error);
    captured.GetTagItem("exception.type").ShouldBe(typeof(InvalidOperationException).FullName);
}
```

- [ ] **Step 3: Run, confirm failure.**

- [ ] **Step 4: Fix.**

Add public `PublishFailed` / `SendFailed` / `ConsumeFailed` methods (or a single generic `SetError(Activity, Exception)`):

```csharp
public static void SetError(Activity? activity, Exception exception)
{
    if (activity is null) return;
    activity.SetStatus(ActivityStatusCode.Error, exception.Message);
    activity.AddException(exception); // .NET 9+; for .NET 8 use AddEvent with tags.
}
```

Then update the call sites (Bus, Producer, RabbitMqConsumerHost) that currently catch exceptions to call `SetError` before disposing the activity. Trace via `grep -rn "StartActivity" src/` to find each site. A typical pattern:

```csharp
using var activity = _telemetry.Publish(args);
try
{
    await _transport.PublishAsync(args, cancellationToken).ConfigureAwait(false);
}
catch (Exception ex)
{
    ServiceConnectActivitySource.SetError(activity, ex);
    throw;
}
```

Do this surgically — only at sites that already catch; don't introduce new try/catch blocks.

- [ ] **Step 5: Rerun, confirm pass.**

---

### Task 7.4 — Full test run, update doc, commit.

- [ ] **Step 1: Unit test run.**

- [ ] **Step 2: Update doc.** Flip the three telemetry bullets. Bump counter to `22/22`.

- [ ] **Step 3: Commit.**

```bash
git add src/ServiceConnect.Telemetry/ServiceConnectActivitySource.cs \
        src/ServiceConnect/Bus.cs \
        src/ServiceConnect.Client.RabbitMQ/Producer.cs \
        src/ServiceConnect.Client.RabbitMQ/RabbitMqConsumerHost.cs \
        src/ServiceConnect.UnitTests/Telemetry/ServiceConnectActivitySourceTests.cs \
        consolodated-issues/2026-04-22-consolidated-issues.md
git commit -m "$(cat <<'EOF'
fix(telemetry): W3C context propagation + error status on activities

- ServiceConnectActivitySource.Send injects traceparent headers for
  payload-less sends by moving the InjectTraceContext call before the
  early-return on null Message.
- Publish and Send inject traceparent unconditionally (even when
  EnablePublish/SendTelemetry=false) so an outer ASP.NET / OTel span
  propagates across the broker boundary without requiring ServiceConnect's
  own spans.
- New SetError(Activity, Exception) helper sets ActivityStatusCode.Error
  and records exception details; call sites in Bus / Producer /
  RabbitMqConsumerHost that already catch exceptions now mark the activity
  so error-rate dashboards reflect reality.

Refs: Medium — Telemetry trace-context & status (M19, M20, M22)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Post-Phase-2 checklist

After all 7 groups commit cleanly:

- [ ] All 22 Medium issues in `consolodated-issues/2026-04-22-consolidated-issues.md` show `[x] (commit: <sha>)`.
- [ ] The Medium counter reads `Medium 22/22`.
- [ ] `dotnet build /warnaserror` at repo root: 0 errors, 0 new warnings.
- [ ] `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`: green.
- [ ] `sg docker -c 'dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj'`: green (excluding the pre-existing skipped tests from Phase 0).
- [ ] `examples/Streaming/Streaming.sln` and `examples/Aggregator/Aggregator.sln` still build cleanly.
- [ ] Dispatch a final Phase 2 holistic code reviewer over `v7-clean-architecture` with range `<last-Phase-1-sha>..HEAD`, same framing as the Phase 1 final review.

---

## Self-review notes

- All 22 Medium items covered: Core 1–7 → Groups 1, 2; RabbitMQ 1–7 → Groups 3, 4, 5; Mongo 1–3 → Group 6; InMemory 1 → Group 6; Telemetry 1–3 → Group 7; Interfaces 1 → Group 2. Total: 7 + 7 + 3 + 1 + 3 + 1 = 22. ✅
- Each group has distinct file paths with no cross-group churn.
- Test code is shown inline for every non-trivial fix; "Adapt to existing fixture" instructions appear only when the repo already has a known pattern the implementer should match.
- Commit messages include the `Refs: Medium — …` line for traceability back to `consolodated-issues/2026-04-22-consolidated-issues.md`.
- The broker-event E2E test in Group 5 is the only test that requires docker; all other tests are unit-level.
