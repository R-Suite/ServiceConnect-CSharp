# Phase 11 — Processor cleanups (design)

**Author:** brainstorming session 2026-05-03
**Branch:** `v7-clean-architecture`
**Source backlog:** [`consolidated-issues/phases/phase-11-processor-cleanups.md`](../../../consolidated-issues/phases/phase-11-processor-cleanups.md)

---

## 1. Goal

Sweep the dispatch processors (`AggregatorProcessor`, `StreamProcessor`, `HandlerProcessor`, `ProcessManagerTimeoutService`) plus the surrounding `MessageDispatcher` / `ConsumeContext` / `MessageTypeExchangeName` / `MessageBusReadStream` / `ConsumeScopeAccessor` correctness defects flagged by the consolidated bug-finding audit. Phase 11 closes the remaining concurrency races, contract ambiguities, and one synchronous-throttle bug in this slice of the core project.

## 2. Verified scope

The 2026-04-28 audit listed 12 Mediums + ~9 smaller items for Phase 11. The 2026-05-03 verification audit reclassified each against the current source (Phases 1-10 already shipped). Final scope:

### 2.1 Dropped — already fixed by Phase 7-10 work

- **M17, M18, M19, M20** — `AggregatorProcessor` flush task race, post-clear timer install, sliding-window indefinite slide, count-after-insert. The Phase 9-era `_resetTimerLock` / `_activeFlushes` work closed these.
- **M22** — `HandlerProcessor` AsyncLocal pooled-context leak. Already pushed via the headers dictionary, scoped by `Popper`.
- `ConsumeContextPool.RentalHandle` TOCTOU — already gated by `_token` + `EnsureActive(_token)` on every getter.
- `MessageTypeRegistry.TryResolve` stale snapshot — version-CAS loop closed the window.
- `StreamProcessor` ~100 MB on missing type-name — `TryRemove` now precedes the type-name check.

### 2.2 In scope — 7 Mediums + 6 smaller items

**Mediums:**

| ID | Subject | File:line (current) |
| --- | --- | --- |
| M16 | Timeout double-dispatch via `SendAsync`→`RemoveDispatchedTimeoutAsync` gap | `ProcessManagerTimeoutService.cs:120-136` |
| M21 | Saga `UpdateData` depends on persistor returning a fresh `Data` reference | `ProcessManagerProcessor.cs:135` |
| M23 | `ConsumeScopeAccessor._current` is a static `AsyncLocal` shared across bus instances | `ConsumeScopeAccessor.cs:13` |
| M24 | `MessageBusReadStream.SetLastPacketNumber` validates `_packets.Keys` after CAS | `MessageBusReadStream.cs:31-59` |
| M25 | `StreamProcessor.DisposeAsync` only disposes cleanup timer; late packets still populate `_activeStreams` | `StreamProcessor.cs:304-307` |
| M26 | `StreamProcessor` admission cap residual race: speculative `GetOrAdd` then `TryRemove` rollback | `StreamProcessor.cs:120-145` |
| M27 | `ExceptionHandler` invoked synchronously on consumer thread — sync user code throttles dispatch | `MessageDispatcher.cs:188` |

**Smaller:**

- `MessageBusReadStream.Read` fragile if `IsComplete` ever returns false-positive (`MessageBusReadStream.cs:106-123`).
- `MessageBusReadStream.Write` does not null-guard `data` (`MessageBusReadStream.cs:62-103`).
- `ConsumeContext._messageId` / `_messageIdCached` / `_correlationId` cache fields not `volatile` (`ConsumeContext.cs:66-68`).
- `MessageTypeExchangeName` hash includes `AssemblyQualifiedName` (version-pinning across services) (`MessageTypeExchangeName.cs:43-54`).
- `HandlerProcessor` does not forward routing slip when any handler throws (silent slip drop) (`HandlerProcessor.cs:98-107`).
- `HandlerProcessor.ForwardRoutingSlipAsync` `IsKnownQueue` check blocks cross-service routing slips (`HandlerProcessor.cs:181-186`).

## 3. Design decisions

Five decisions taken during brainstorming. Each has alternatives considered + rationale for the chosen path.

### 3.1 Q1 — M21 saga `UpdateData` mutation contract → **B**

Defect: `ProcessManagerProcessor.UpdateData` works correctly only if `IPersistenceData<T>.Data` is a fresh copy per `FindDataAsync` call. If a handler mutates `Data` then throws, the retry path's second `FindDataAsync` reads partially-mutated state when the persistor caches the row.

**Decision: B — document the contract.** Both built-in persistors already comply for free:
- InMemory: `FindMatchingItem<T>` returns `new MemoryData<T> { Id = typed.Id, Data = DeepClone.Clone(typed.Data), Version = typed.Version }` (verified Phase 10).
- Mongo: BSON deserialization produces a fresh CLR object per query (no caching at the driver level for these queries).

Pin the contract via XML doc on `IProcessManagerFinder.FindDataAsync<T>` and add one regression test per persistor asserting `FindDataAsync` returns distinct `Data` references on consecutive calls.

Alternatives rejected:
- **A (deep-clone in `ProcessManagerProcessor`):** pays JSON round-trip on every saga handler call when both built-ins already comply.
- **C (snapshot/reconciliation):** too invasive; introduces a state-restoration path for a contract that's effectively already honored.

### 3.2 Q2 — M27 async `ExceptionHandler` → **B (breaking)**

Defect: `IBusConfiguration.ExceptionHandler` is `Action<Exception>?`; the dispatcher invokes it synchronously, so slow user code blocks the consumer thread and throttles RabbitMQ prefetch.

**Decision: B — replace with async signature.** Change to `Func<Exception, CancellationToken, ValueTask>?`. v8 release allows breaking changes; a single canonical signature is cleaner than parallel sync+async properties. Migration shim documented in release notes:

```csharp
// before (v7)
cfg.ExceptionHandler = ex => Log.Error(ex);
// after (v8)
cfg.ExceptionHandler = (ex, _) => { Log.Error(ex); return ValueTask.CompletedTask; };
```

Alternatives rejected:
- **A (additive sibling):** parallel properties are easy to misuse; pick one signature.
- **C (sync signature + Task.Run wrapper):** no API change but loses ordering / cancellation hooks; eats handler-thrown exceptions; observably worse semantics.

### 3.3 Q3 — M26 admission-cap residual race → **C (full reorder)**

Defect: residual TOCTOU between `_activeStreams.GetOrAdd` (speculative insert) and the rollback `TryRemove` after the over-cap check. A concurrent packet for the same `sequenceId` briefly observes and writes into the rejected stream.

**Decision: C — pre-increment-then-decrement.** Reorder so the `Interlocked` counter is the gate, not the post-hoc check:

```csharp
var nextCount = Interlocked.Increment(ref _streamCount);
if (nextCount > _maxStreams)
{
    Interlocked.Decrement(ref _streamCount);
    return /* rejected */;
}
// counter bump succeeded; only now do we GetOrAdd
var stream = _activeStreams.GetOrAdd(sequenceId, ...);
```

Eliminates the speculative dictionary insert entirely. Standard concurrent-admission idiom; not much more code than the current shape.

Alternatives rejected:
- **B (document the bounded race):** Phase 11 is the natural moment to fix concurrency races properly; documenting a real race we know how to close is a worse outcome.
- **A (reorder counter check first, keep speculative GetOrAdd):** halfway house; the speculative insert remains.

### 3.4 Q4a — `HandlerProcessor` routing slip silent drop on throw → **B (current behavior, document)**

Defect: if any handler throws, the `AggregateException` propagates *before* `ForwardRoutingSlipAsync` runs, dropping the slip. DLQ-routed messages permanently lose the slip.

**Decision: B — keep current behavior.** Slip semantics are step-by-step coordination; advancing the slip on partial failure breaks the invariant. Document explicitly that:
1. The slip is dropped from the in-flight forward path on any handler throw.
2. The slip is preserved in the message envelope, so DLQ-routed messages and manual retries still carry the slip data.

Verify the DLQ path preserves slip headers; if not, fix that as part of the work item.

Alternatives rejected:
- **A (forward in finally):** advances slip despite handler failure; recipients see a "next step" the sender thinks failed.
- **C (reroute to failure queue):** breaks the slip's contract.

### 3.5 Q4b — `HandlerProcessor.ForwardRoutingSlipAsync` `IsKnownQueue` block → **B (format validation)**

Defect: `IsKnownQueue` rejects any destination not in the local queue config, blocking the legitimate cross-service slip pattern.

**Decision: B — format validation only.** Replace `IsKnownQueue` with a check that the destination is a non-empty, well-formed queue name. Allow cross-service destinations. RabbitMQ routes via the alternate-exchange / mandatory-return path if the queue doesn't exist downstream — that's the right error surface.

Alternatives rejected:
- **A (drop check entirely):** loses the smoke-screen against typos.
- **C (configurable):** adds config surface for what should just be the right default.

## 4. Architecture

No cross-cutting redesign. Two threads:

- **Concurrency-correctness fixes** (M16, M23, M24, M25, M26) — discrete races in specific components. Each lands as: small surface change + targeted concurrency test that fails pre-fix, MRES-gated for determinism (no `Thread.Sleep`).
- **Contract / API changes** (M21, M27, slip pair) — XML doc updates + interface changes where applicable + behavioral test pinning the new contract.

**v8 breakage budget:** M27 is the only public-API breaking change. `MessageTypeExchangeName` hash change is a deployment-visible breaking change (existing exchanges need migration). Everything else is internal or doc-only.

**No new abstractions, no new files in production code.** All work lands in existing files: `ProcessManagerTimeoutService.cs`, `ProcessManagerProcessor.cs`, `ConsumeScopeAccessor.cs`, `MessageBusReadStream.cs`, `StreamProcessor.cs`, `MessageDispatcher.cs`, `HandlerProcessor.cs`, `ConsumeContext.cs`, `MessageTypeExchangeName.cs`, `IProcessManagerFinder.cs`, `IBusConfiguration.cs` (or wherever `ExceptionHandler` lives). New test files per concurrency-test affinity (one per fix that needs a non-trivial harness).

## 5. Per-finding fix shapes

### 5.1 M16 — `ProcessManagerTimeoutService` SendAsync→Remove gap

Lease-guard from Phase 8 catches expiring leases at fetch time, but `SendAsync` (line 122-126) runs unguarded. **Fix:** wrap `SendAsync(...)` so that if the lease-deadline is exceeded by the time send returns, the dispatch aborts WITHOUT calling `RemoveDispatchedTimeoutAsync`. The unremoved row is later re-acquired by Phase 8's lease-expiry sweep on next poll. Keep the existing `_timeProvider` injection as the wall-clock source.

Test: two pollers, slow `IBus.SendAsync` mock that exceeds the lease deadline; assert `SendAsync` is invoked at-most-once for the same `TimeoutId` across both pollers.

### 5.2 M21 — `IProcessManagerFinder.FindDataAsync<T>` fresh-copy contract

Add XML doc on the interface method:

```csharp
/// <returns>
/// The persistence wrapper carrying a <b>fresh</b> <see cref="IPersistenceData{T}.Data"/>
/// reference per call. Callers may freely mutate <c>Data</c>; subsequent calls must
/// observe the previously-stored state, not the in-flight mutation. Implementors that
/// cache rows internally MUST clone before returning.
/// </returns>
```

Add regression tests:
- `InMemoryProcessManagerFinder_FindDataAsync_ReturnsFreshDataPerCall`
- `MongoDbProcessManagerFinder_FindDataAsync_ReturnsFreshDataPerCall`

Each test inserts a row, retrieves twice, asserts `!ReferenceEquals(first.Data, second.Data)`.

### 5.3 M23 — `ConsumeScopeAccessor` static→instance

`private static readonly AsyncLocal<IServiceProvider?> _current` → `private readonly AsyncLocal<IServiceProvider?> _current`. `ConsumeScopeAccessor` is registered as a DI singleton, so the ergonomic surface doesn't change.

Test: two `ConsumeScopeAccessor` instances in the same AppDomain, push different scopes concurrently, assert each sees only its own pushed scope.

### 5.4 M24 — `MessageBusReadStream.SetLastPacketNumber` CAS race

Move the validation INSIDE the CAS retry loop:

```csharp
while (true) {
    var current = _lastPacketNumber;
    if (current != UnknownLastPacket) {
        // already set — no-op or throw, preserving existing behavior
        return;
    }
    // validate _packets.Keys against value
    foreach (var k in _packets.Keys) {
        if (k > value) throw /* existing exception */;
    }
    if (Interlocked.CompareExchange(ref _lastPacketNumber, value, current) == current) break;
    // CAS lost — another writer set _lastPacketNumber; retry validates against new state
}
```

Test: `Task.WhenAll(Write(N+1), SetLastPacketNumber(N))` with MRES gating; assert no packet > N survives in `_packets`.

### 5.5 M25 — `StreamProcessor.DisposeAsync` late-packet protection

Add `private int _disposed`. `DisposeAsync` does:
```csharp
if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
await _cleanupTimer.DisposeAsync();
foreach (var (_, stream) in _activeStreams) stream.Dispose();
_activeStreams.Clear();
```

Packet-arrival path checks `Volatile.Read(ref _disposed)` first; if set, drop the packet (matches existing dispose-time semantics — late packets to a dispose-in-flight processor are not buffered).

Test: dispose, then call the packet-arrival method directly; assert `_activeStreams.Count == 0`.

### 5.6 M26 — admission-cap pre-increment / decrement

See §3.3 sketch. The reorder eliminates the speculative `GetOrAdd`; the dictionary insert is gated on a successful counter bump.

Test: 1000 concurrent packets with distinct `sequenceId` and `_maxStreams = 100`; assert `_activeStreams.Count == 100` exactly and the rejection count equals 900.

### 5.7 M27 — async `ExceptionHandler`

Change `IBusConfiguration.ExceptionHandler`:
```csharp
// before
Action<Exception>? ExceptionHandler { get; set; }
// after
Func<Exception, CancellationToken, ValueTask>? ExceptionHandler { get; set; }
```

`MessageDispatcher` await:
```csharp
if (_config.ExceptionHandler is { } handler) {
    try { await handler(ex, _shutdownCts.Token).ConfigureAwait(false); }
    catch (Exception handlerEx) { _logger.LogError(handlerEx, "ExceptionHandler threw."); }
}
```

Cancellation token is the dispatcher's shutdown CTS so a slow handler can short-circuit on shutdown. Catch handler-thrown exceptions and log — don't propagate (the handler is the last line of error handling; throwing from it shouldn't double-fault the dispatch loop).

Test: register a slow async handler (e.g. delays 500ms); assert dispatcher continues processing other messages without 500ms pauses.

### 5.8 Smaller — `MessageBusReadStream.Read` IsComplete fragility

Add an inner-loop `IsComplete()` re-check before iteration; if it flips false the read aborts with a clear error rather than silently skipping packets. Defensive — no current trigger known, but the failure mode is silent data corruption.

### 5.9 Smaller — `MessageBusReadStream.Write` null-guard

`ArgumentNullException.ThrowIfNull(data);` at the top of `Write`.

### 5.10 Smaller — `ConsumeContext` volatile fields

`volatile` is invalid on `Guid?` (a struct), so the fix uses the **standard double-checked publication pattern**: the `bool` flag is `volatile`, and the struct fields are written BEFORE the flag, read AFTER it. The volatile bool's release/acquire semantics provide the memory barrier that makes the prior struct writes visible.

```csharp
private Guid? _messageId;          // plain field — published via the volatile flag
private volatile bool _messageIdCached;
private Guid? _correlationId;      // plain field — published via its own flag
private volatile bool _correlationIdCached;

public Guid? MessageId
{
    get
    {
        if (_messageIdCached) return _messageId;     // volatile read = acquire barrier
        var value = ResolveMessageId();
        _messageId = value;                           // write before publishing
        _messageIdCached = true;                      // volatile write = release barrier
        return value;
    }
}
```

A racing-thread interleaving may produce two parallel `ResolveMessageId()` calls (idempotent — no harm), but no torn reads. This avoids `Lazy<>`'s allocation and `Interlocked.MemoryBarrier`'s heavier-than-needed full fence.

Note: if the existing code already has a separate cached-flag bool per cached field, we just add `volatile` to it. If the existing pattern is `_messageId == null ? Resolve : _messageId` (using null as the not-yet-resolved sentinel), the fix is to introduce explicit cached-flag bools as above — null is a valid resolved value (no message-id present) and shouldn't double as "not yet resolved."

### 5.11 Smaller — `MessageTypeExchangeName` strip assembly version

Replace:
```csharp
var suffixSource = type.AssemblyQualifiedName!;
```
with:
```csharp
var suffixSource = $"{type.FullName}, {type.Assembly.GetName().Name}";
```

Document as a v8 deployment-visible breaking change: exchanges named under the v7 hash will need migration. Provide a migration script / one-paragraph note in release notes.

### 5.12 Smaller — `HandlerProcessor` routing slip pair

**Silent drop on throw:** add XML doc on `HandlerProcessor` explaining the contract; verify the DLQ path preserves the slip in headers (likely already does; if not, fix in this item).

**`IsKnownQueue` → format validation:** replace the `_busConfiguration.QueueMappings.ContainsKey(destination)` check with `string.IsNullOrWhiteSpace(destination) ? throw new... : (allow)`. Cross-service slip works.

Tests:
- `RoutingSlip_PreservedOnDlqRouteAfterHandlerThrow` — handler throws, message hits DLQ, slip header survives.
- `ForwardRoutingSlip_AllowsDestinationNotInLocalConfig` — forward to "remote-service-q" with no local mapping, no throw.

## 6. Testing strategy

Per-csproj unit tests against `ServiceConnect.UnitTests`. Concurrency tests use `ManualResetEventSlim` / `TaskCompletionSource` to control thread interleaving deterministically — no `Thread.Sleep` / timeouts (those are flaky on CI).

| Finding | Test type | Approach |
| --- | --- | --- |
| M16 | Concurrency unit | Two pollers, slow `IBus.SendAsync` mock; assert `SendAsync` invoked at-most-once per `TimeoutId` |
| M21 | Persistor regression × 2 | Per-persistor: `FindDataAsync` twice; assert distinct `Data` references |
| M23 | Concurrency unit | Two `ConsumeScopeAccessor` instances; assert per-instance scopes don't bleed |
| M24 | Concurrency unit | Concurrent `Write(N+1)` + `SetLastPacketNumber(N)`; assert no `>N` packet survives |
| M25 | Sequential unit | Dispose, then call packet-arrival path; assert `_activeStreams.Count == 0` |
| M26 | Concurrency unit | Saturate cap with concurrent inserts; assert `_activeStreams.Count == cap` exactly |
| M27 | Behavioral unit | Slow async handler, measured non-blocking dispatch |
| `MessageBusReadStream.Read` | Sequential unit | Force false-positive `IsComplete`; assert clean error |
| `MessageBusReadStream.Write` null-guard | Sequential unit | Pass `null`; assert `ArgumentNullException` |
| `ConsumeContext` volatile | Concurrency unit (smoke) | Two threads cache-read; assert no torn reads |
| `MessageTypeExchangeName` hash | Unit | Same type from two assembly versions yields identical exchange name |
| `HandlerProcessor` slip-on-throw | Behavioral unit | One handler succeeds, one throws; assert slip preserved in DLQ headers |
| `HandlerProcessor` cross-service slip | Behavioral unit | Forward to queue not in local config; assert no throw |

**Build/test invocation:**
```bash
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
  --filter "FullyQualifiedName~Aggregator|Stream|Handler|ProcessManagerTimeout|ConsumeContext|ConsumeScope|MessageDispatcher|MessageBusReadStream|MessageTypeExchangeName" -m:1
```

**E2E:** none required. M16 / M27 unit tests with MRES-gated mocks cover the races faithfully.

## 7. Rollout

**Single subagent-driven sweep**, 17 tasks (1 spec, 1 plan, 13 fixes, 3 docs). NOT split into 5 PRs (the README's PR-1-5 grouping was for the original 12-Medium scope; with 8 items dropped, splitting is administrative overhead).

Task ordering by risk + sequencing:

1. Spec commit
2. Plan commit
3. M21 — XML doc + 2 persistor regression tests (verify-only)
4. M27 — async `ExceptionHandler` (early — sets v8 breaking-change anchor)
5. M23 — `ConsumeScopeAccessor` static→instance
6. M16 — `ProcessManagerTimeoutService` SendAsync→Remove gap
7. M24 — `MessageBusReadStream.SetLastPacketNumber` CAS race
8. M25 — `StreamProcessor.DisposeAsync` late-packet protection
9. M26 — admission-cap pre-increment / decrement
10. Smaller — `MessageBusReadStream` Read fragility + Write null-guard
11. Smaller — `ConsumeContext` volatile fields
12. Smaller — `MessageTypeExchangeName` strip assembly version
13. Smaller — `HandlerProcessor` routing slip pair
14. Phase 11 release notes (`website/src/content/docs/releases.mdx`)
15. Reference + learn doc updates
16. README updates (`examples/Aggregator`, `examples/ProcessManager`, `examples/Streaming`)
17. Final verification gate

Each task = one commit (or one fix-commit + one cleanup-commit if review surfaces issues), TDD where applicable, subagent-driven implementer + two-stage review per the established Phase 7-10 pattern.

## 8. v8 breaking changes shipped

- `IBusConfiguration.ExceptionHandler`: `Action<Exception>?` → `Func<Exception, CancellationToken, ValueTask>?`. Migration shim in release notes.
- `MessageTypeExchangeName` hash: assembly-version-aware → version-stable. Existing deployments need exchange migration.

## 9. v8 contract clarifications shipped

- `IProcessManagerFinder.FindDataAsync<T>` MUST return a fresh `Data` reference per call. Both built-in persistors comply; document for third-party implementors.
- `HandlerProcessor` drops the routing slip from the in-flight forward path on any handler throw; slip is preserved in the message envelope for DLQ retry. Document on `IRoutingSlip` / handler-processor reference page.
- `HandlerProcessor.ForwardRoutingSlipAsync` allows cross-service destinations; only format validation (non-empty, well-formed name).

## 10. Documentation deliverables

**Website:**
- `website/src/content/docs/releases.mdx` — Phase 11 entry.
- `website/src/content/docs/reference/handlers/` — `IMessageHandler.HandleAsync` semantics; AsyncLocal context guarantees (M23 per-instance scoping).
- `website/src/content/docs/reference/process-managers/` — saga handler retry semantics (M21 fresh-copy contract); timeout dispatch contract (M16 at-most-once).
- `website/src/content/docs/reference/extension-points/` — `ExceptionHandler` async signature + migration sample (M27).
- `website/src/content/docs/learn/messaging-patterns/` — routing slip behavior on handler failure; cross-service routing.

**READMEs:**
- `examples/Aggregator/README.md` — timeout-window semantics already correct after Phase 9; verify no stale wording.
- `examples/ProcessManager/README.md` — saga retry / timeout behavior (M21 + M16).
- `examples/Streaming/README.md` — stream lifecycle and admission cap (M25 + M26).

## 11. Out of scope

- HealthChecks, Interfaces hygiene polish, telemetry adjustments — Phase 12.
- Phase 1-10 fixes are not revisited; M21 verification confirms both persistors honor the contract today, no work needed there beyond the doc + regression tests.
- Whole-solution refactoring: out of scope. Each fix lands in its existing file.
