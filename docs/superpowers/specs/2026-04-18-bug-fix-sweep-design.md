# Bug Fix Sweep — Design

**Date:** 2026-04-18
**Branch:** `improvements-and-fixes`
**Source reviews:**
- `docs/code-review-findings.md` (45 findings)
- `docs/performance-audit-report.md` (32 findings, **out of scope**)

## Goal

Fix the genuine correctness bugs identified in the code review. Performance findings are excluded by user request. Architectural refactors, missing tests for unrelated areas, naming nits, and missing XML docs on configuration classes are also excluded — this sweep is scoped to **bugs only**.

## Out of scope

- All performance findings from `docs/performance-audit-report.md`.
- CLEAN-code refactors (R-009, R-010, R-011, R-014, R-015, R-018, R-019, R-024, R-027).
- Encapsulation/immutability tightening (R-004 already correct via `private set;`; R-006 deferred).
- Style/security polish (R-017, R-021, R-023, R-028, R-031, R-033, R-035, R-038, R-039, R-040, R-041, R-042, R-043).
- Findings determined invalid on verification (R-007, R-029).
- R-003 — `AggregatorProcessor` race window remains, but existing defenses (`_activeFlushes` registered before flush starts and awaited in `DisposeAsync`; per-aggregator semaphore serialising flushes) reduce its impact to a benign extra empty-flush. Not worth additional surgery.

## In scope — 7 fixes

| ID | File | Severity | Type |
|----|------|----------|------|
| R-001 | `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs` | Critical | Bug |
| R-002 | `src/ServiceConnect/Services/Processors/StreamProcessor.cs` | Critical | Bug |
| R-005 | `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs` | Critical | Bug |
| R-016 | `src/ServiceConnect/Bus.cs` | High | Annotation only |
| R-022 | `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs` | Medium | Bug |
| R-026 | `src/ServiceConnect.Interfaces/HeaderDecoder.cs` | Medium | Bug |
| R-030 | `src/ServiceConnect.Interfaces/Options/RequestOptions.cs` | Medium | Documentation |

---

## R-001 — `InMemoryTimeoutStore.ReleaseDispatchedTimeoutAsync` is a no-op

**File:** `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs:101-105`

### Current behaviour

```csharp
public Task ReleaseDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    return Task.CompletedTask;
}
```

### Diagnosis

The original review claimed a "memory leak" because failed dispatches leave timeouts permanently locked. Verification shows that argument is **wrong**: `InMemoryTimeoutStore.GetTimeoutsBatchAsync` does not read or write the `Locked` / `LockedBy` / `LockExpiresAt` fields at all, so there is no lock to leak. The actual problem is that the implementation is silently breaking the `ITimeoutStore` contract — the MongoDB implementation clears these fields, and a future caller relying on the contract (e.g. an InMemory store that adds locking later, or a mixed-store integration test) will silently malfunction.

### Fix

Mirror the MongoDB store's intent: when a release is requested, clear the lock fields under the existing write lock.

```csharp
public Task ReleaseDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    _state.SyncRoot.EnterWriteLock();
    try
    {
        if (_state.TimeoutsById.TryGetValue(id, out var entry))
        {
            entry.Data.Locked = false;
            entry.Data.LockedBy = Guid.Empty;
            entry.Data.LockExpiresAt = null;
        }
    }
    finally { _state.SyncRoot.ExitWriteLock(); }
    return Task.CompletedTask;
}
```

### Tests

- New unit test: insert a `TimeoutData` with `Locked = true`, `LockedBy = someGuid`, `LockExpiresAt = utcNow`. Call `ReleaseDispatchedTimeoutAsync`. Assert all three fields cleared.
- Idempotency: call `ReleaseDispatchedTimeoutAsync` for an unknown `id` — no exception, no mutation.
- Cancellation: call with a pre-cancelled token — assert `OperationCanceledException`.

### Risk

Low. The behaviour is no longer a no-op for callers that pass an existing id, but `GetTimeoutsBatchAsync` ignores the lock fields, so observable polling behaviour is unchanged. Pre-existing semantic gap with MongoDB (no per-poll locking) remains, by design.

---

## R-002 — `StreamProcessor` timer disposal race

**File:** `src/ServiceConnect/Services/Processors/StreamProcessor.cs:8, 185-188`

### Current behaviour

`StreamProcessor` implements `IDisposable`, with `Dispose()` calling `_cleanupTimer.Dispose()`. `ITimer.Dispose()` does not wait for an in-flight callback, so `EvictStaleStreams` may run after `Dispose` returns.

### Diagnosis

The race is real but currently benign — `EvictStaleStreams` only calls `_timeProvider.GetUtcNow()`, iterates `_activeStreams` (a `ConcurrentDictionary`) and logs warnings. None of those references are nulled at dispose. However, any future cleanup logic that touches non-thread-safe state (clearing `_activeStreams`, disposing logger, etc.) would be exposed.

### Fix

Switch the type to `IAsyncDisposable` and use `_cleanupTimer.DisposeAsync()`, which waits for the in-flight callback.

```csharp
internal sealed class StreamProcessor : IMessageProcessor, IAsyncDisposable
{
    // …existing fields/methods…

    public ValueTask DisposeAsync() => _cleanupTimer.DisposeAsync();
}
```

### Tests

- Verify existing tests still pass (StreamProcessor is resolved through DI; container will call `DisposeAsync` for `IAsyncDisposable`).
- New unit test: use `FakeTimeProvider` to advance time so the cleanup callback runs concurrently with `DisposeAsync`. Assert `DisposeAsync` completes without exception and the callback has finished before the await returns.

### Risk

**Container registration:** confirm DI registration of `StreamProcessor` does not pin its lifetime as `IDisposable`. The `Microsoft.Extensions.DependencyInjection` container handles `IAsyncDisposable` for scoped/transient services since .NET 6. Worth a `gitnexus_impact` check on `StreamProcessor` to find every callsite that disposes it.

---

## R-005 — `ProcessManagerTimeoutService` swallows `OperationCanceledException`

**File:** `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs:92, 100`

### Current behaviour

Two `catch (Exception ex)` blocks log the error and continue:

- Line 92 — wraps the dispatch (`SendAsync` + `RemoveDispatchedTimeoutAsync`).
- Line 100 — wraps the `ReleaseDispatchedTimeoutAsync` recovery.

If a host shutdown cancels the polling token mid-dispatch, the `OperationCanceledException` is logged as an error and the loop continues processing the remaining batch instead of terminating cleanly.

### Fix

Add an exception filter excluding `OperationCanceledException`:

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
{
    logger.LogError(ex, "Error dispatching timeout {TimeoutId}", timeout.Id);
    // …
}
```

Same change to the inner release catch (line 100). The outer catch at line 107 (`Error polling for process manager timeouts`) is left as-is — `PollLoop` already has `catch (OperationCanceledException) { break; }` at line 123.

### Tests

- New unit test with a fake `ITimeoutStore` whose `RemoveDispatchedTimeoutAsync` throws `OperationCanceledException`. Assert the exception propagates out of `PollOnceAsync` rather than being caught and logged.
- Same test but `ReleaseDispatchedTimeoutAsync` throws OCE — assert propagation.

### Risk

Low. Existing callers of `PollOnceAsync` are `PollLoop` (which catches OCE) and unit tests.

---

## R-016 — `Bus.BuildRoutingSlip` index-1 loop is undocumented

**File:** `src/ServiceConnect/Bus.cs:390-405`

### Current behaviour

The loop starts at `index = 1`, intentionally skipping `destinations[0]`. The first destination is the current send target; the routing slip describes *subsequent* hops.

### Fix

Add a one-line comment immediately above the loop explaining the offset. No behaviour change.

### Tests

None — comment-only.

### Risk

None.

---

## R-022 — `MongoDbTimeoutStore.EnsureTimeoutIndexAsync` doesn't tolerate concurrent index creation

**File:** `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs:175-204`

### Current behaviour

The method creates the timeout indexes on first insert, with `try { … } catch { throw; }` (a no-op catch). In multi-process deployments, two processes hitting `InsertTimeoutAsync` for the first time can race, and the loser receives `MongoCommandException` with code 85 (IndexOptionsConflict) or 86 (IndexKeySpecsConflict).

### Fix

Replace the rethrow-only catch with a code-85/86 filter that treats concurrent creation as success:

```csharp
private async Task EnsureTimeoutIndexAsync(IMongoCollection<TimeoutData> collection)
{
    if (_timeoutIndexEnsured) return;
    try
    {
        var idIndexModel = new CreateIndexModel<TimeoutData>(
            Builders<TimeoutData>.IndexKeys.Ascending(x => x.Id));
        // …other index models unchanged…

        await collection.Indexes.CreateManyAsync(
            [idIndexModel, lockedTimeIndexModel, lockedByIndexModel, lockExpiresAtIndexModel]
        ).ConfigureAwait(false);
        _timeoutIndexEnsured = true;
    }
    catch (MongoCommandException ex) when (ex.Code is 85 or 86)
    {
        // Another process created the same index concurrently — safe to treat as success.
        _timeoutIndexEnsured = true;
    }
}
```

### Tests

- New unit test using a mocked `IMongoIndexManager<TimeoutData>` whose `CreateManyAsync` throws `MongoCommandException` with `Code = 85`. Assert `InsertTimeoutAsync` succeeds.
- Same with `Code = 86`. Assert success.
- Same with `Code = 1` (a different error). Assert it propagates as `PersistenceException`.

### Risk

Low. The change is additive — codes 85/86 are now tolerated; all other errors still propagate.

---

## R-026 — `HeaderDecoder.Decode` uses `Debug.Assert` for type validation

**File:** `src/ServiceConnect.Interfaces/HeaderDecoder.cs:14`

### Current behaviour

```csharp
Debug.Assert(value is null or string, $"Unexpected header value type: {value?.GetType().FullName}");
return value?.ToString();
```

`Debug.Assert` is a no-op in RELEASE builds; an unexpected type silently falls through to `value?.ToString()`, which for value types like `int` returns the type-name string (e.g. `"System.Int32"`) rather than the value's textual form — almost always wrong, and downstream parsers (Guid, long) will fail with confusing errors.

### Fix

Replace the assertion with a runtime guard:

```csharp
public static string? Decode(object? value)
{
    if (value is null) return null;
    if (value is byte[] bytes) return Encoding.UTF8.GetString(bytes);
    if (value is string s) return s;
    throw new ArgumentException($"Unexpected header value type: {value.GetType().FullName}", nameof(value));
}
```

The `[MethodImpl(MethodImplOptions.AggressiveInlining)]` attribute is preserved.

### Tests

- Existing tests cover `null`, `string`, `byte[]`. Confirm they still pass.
- New unit test: pass `(object)42` — assert `ArgumentException` with type name in message.
- New unit test: pass `(object)Guid.NewGuid()` — assert `ArgumentException`.

### Risk

**Behaviour change in RELEASE builds.** Any caller currently passing a non-string/non-byte[] header value would now throw at the boundary rather than producing garbage downstream. A `gitnexus_impact` analysis of `HeaderDecoder.Decode` is required to enumerate all call sites and verify none rely on the silent fallback.

---

## R-030 — `RequestOptions.ExpectedReplyCount = 0` semantics are undocumented

**File:** `src/ServiceConnect.Interfaces/Options/RequestOptions.cs` (property `ExpectedReplyCount`)

### Current behaviour

In `RequestReplyManager.SendRequestMultiAsync` (`src/ServiceConnect/Services/RequestReplyManager.cs:74-86`):

```csharp
int expectedCount = options.ExpectedReplyCount ?? options.EndPoints?.Count ?? -1;
// …
if (expectedCount > 0 && responses.Count >= expectedCount)
    tcs.TrySetResult(null!);
```

When `ExpectedReplyCount = 0`, `expectedCount = 0`, the early-completion check `expectedCount > 0` is never true, and the request waits for the full timeout — then returns whatever responses arrived. This is a defensible behaviour (collect-everything-in-window) but is not documented.

### Fix

Add an XML doc remark on the `ExpectedReplyCount` property describing all three modes:

> When set to a positive value, the call completes as soon as that many replies have arrived (or the timeout elapses, whichever comes first).
> When `0` or negative, the call always waits the full `Timeout` and returns every reply received during the window.
> When `null`, defaults to `EndPoints.Count` if `EndPoints` is set; otherwise behaves as `-1` (timeout-only).

### Tests

None — documentation-only change. Existing behavioural test (or add one) should cover the timeout-only path.

### Risk

None.

---

## Cross-cutting

### Branching

- Continue on `improvements-and-fixes` (already a fix-themed branch with related E2E and process-manager work).

### Per-fix workflow

Each fix is its own commit. For every fix:

1. `gitnexus_impact({target: "<symbol>", direction: "upstream"})` for each modified function/method. Report blast radius. Halt if HIGH/CRITICAL risk surfaces.
2. Apply the change.
3. Add/update tests.
4. Run the relevant unit-test project locally.
5. `gitnexus_detect_changes()` to confirm scope matches expectation.
6. Commit with subject prefix `fix(R-NNN): …`.

### Test execution

- Unit tests: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`.
- E2E tests use Testcontainers; wrap with `sg docker -c '…'` since the user is not in the docker group.
- Full sweep before opening the PR.

### Index freshness

Per project CLAUDE.md, after the final commit run `npx gitnexus analyze --embeddings` (the `--embeddings` flag preserves any previously generated embeddings — confirm via `.gitnexus/meta.json` `stats.embeddings` first). The post-commit hook handles this automatically for git commit/merge events, so this is a fallback if the hook fails.

### Done criteria

- All seven changes committed with R-NNN reference.
- `dotnet test` for unit tests is green.
- E2E suite is green.
- `gitnexus_detect_changes` shows only the expected files.
- Stale review docs at the repo root (`docs/code-review-report.md`, `docs/performance-review-report.md` — currently shown as deleted in `git status`) are either restored or formally removed in a separate housekeeping commit; not part of this sweep.
