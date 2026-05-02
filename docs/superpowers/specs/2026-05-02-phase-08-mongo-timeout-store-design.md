# Phase 08 — Mongo timeout-store + lease guards (design)

**Status:** approved 2026-05-02. Source phase doc: [`consolidated-issues/phases/phase-08-mongo-timeout-store.md`](../../../consolidated-issues/phases/phase-08-mongo-timeout-store.md).

**Goal:** Restore lease-expiry semantics across `MongoDbTimeoutStore` and `InMemoryTimeoutStore`, align Mongo indexes with the actual due query, and harden cancellation handling between `UpdateMany` and `FindAsync`. v8 (major-version) — breaking changes are explicitly permitted.

**Projects affected:**
- `ServiceConnect.Persistence.MongoDb` (timeout store, options)
- `ServiceConnect.Persistence.InMemory` (timeout store, new options class)
- `ServiceConnect.Interfaces` (XML doc on `ITimeoutStore` documenting the cross-persistor contract)
- `ServiceConnect.UnitTests` (per-backend tests)
- `website/src/content/docs/releases.mdx`, `website/src/content/docs/reference/...`, `website/src/content/docs/learn/operations/...`

**Branch / starting point:** `v7-clean-architecture`, current HEAD `7b5aa681`.

---

## 1. Findings in scope

### High

| ID | Summary | Pointer (current line numbers verified 2026-05-02) |
|---|---|---|
| H7 | Mongo `RemoveDispatchedTimeoutAsync` / `ReleaseDispatchedTimeoutAsync` lease filters miss `LockExpiresAt > utcNow` | [`MongoDbTimeoutStore.cs:183-217, :220-253`](../../../src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs) |
| H8 | Mongo owned-row read-back filter misses `LockExpiresAt > utcNow` | [`MongoDbTimeoutStore.cs:163-168`](../../../src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs) |
| H9 | InMemory `Remove`/`Release` accept expired leases — cross-persistor parity gap | [`InMemoryTimeoutStore.cs:138-215`](../../../src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs) |
| H31 | Mongo timeout-store indexes don't support the actual due query under load | [`MongoDbTimeoutStore.cs:332-358`](../../../src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs) |

### Medium

| ID | Summary | Pointer |
|---|---|---|
| M33 | Mongo `StartSessionAsync` fallback only catches `NotSupportedException` (other exception types fail the whole poll) | [`MongoDbTimeoutStore.cs:118-128`](../../../src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs) |
| M34 | Cancellation between `UpdateMany` and `FindAsync` orphans leases until the reaper runs | [`MongoDbTimeoutStore.cs:99-180`](../../../src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs) |
| M35 | Candidate timeout sort has no tie-breaker; under heavy bursts some rows can be starved | [`MongoDbTimeoutStore.cs:141-144`](../../../src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs) |

### Smaller

| Summary | Pointer |
|---|---|
| InMemory `LockLeaseDuration` hard-coded constant — diverges from Mongo configurable | [`InMemoryTimeoutStore.cs:14`](../../../src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs) |
| InMemory `InsertTimeoutAsync` allows `Guid.Empty` Id (Mongo rejects) | [`InMemoryTimeoutStore.cs:31-55`](../../../src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs) |
| InMemory mutates `entry.Data.LockExpiresAt` in place during `GetTimeoutsBatchAsync` | [`InMemoryTimeoutStore.cs:84-88`](../../../src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs) |
| InMemory `SortedSet.Remove` not asserted to return true (latent comparer-drift catch) | [`InMemoryTimeoutStore.cs:166-167`](../../../src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs) |
| `ReapStaleLeasesAsync` flagged as not restoring `Locked` semantics | [`MongoDbTimeoutStore.cs:262-285`](../../../src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs) — **verification: code already correct** |

## 2. Out of scope (routed elsewhere)

- **M16** (`ProcessManagerTimeoutService.PollOnceAsync` double-dispatch) — Phase 11. H7's fix narrows the M16 window because `Remove` no longer succeeds with an expired lease, but does not fully close it. Cross-link in plan.
- All other Mongo persistence concerns (aggregator, process-manager, serializer) — Phase 9.
- All other InMemory persistence concerns (aggregator, process-manager, cache provider) — Phase 10.

## 3. Design decisions

Four design questions were resolved during brainstorming on 2026-05-02:

| # | Question | Decision |
|---|---|---|
| Q1 | Test approach | **A.** Per-backend tests, no parity harness. Mongo: filter-shape assertions via `filter.Render(...).ToJson()`. InMemory: behaviour assertions on real in-process state. Cross-persistor contract documented once on `ITimeoutStore`. |
| Q2 | H31 index migration | **A.** Add new `(Time, Locked, LockExpiresAt)` composite AND drop legacy `(Locked, Time)`. Use `DropOneAsync` with `MongoCommandException` code 27 (`IndexNotFound`) swallowed for idempotency. Major-version breaking change. |
| Q3 | M34 cancellation handling | **B.** Best-effort release in `finally` after `UpdateMany` succeeds. The release uses `CancellationToken.None` so it isn't preempted. Release exceptions are swallowed and logged at Warning. |
| Q4 | InMemory configurability | **C.** Replace legacy `(string, string, TimeProvider?)` ctor with new `(InMemoryPersistenceOptions, TimeProvider?)` ctor. Mirrors Mongo's options shape. Update all DI registrations and test/example call sites. |

## 4. Per-fix behaviour spec

### 4.1 H7 — Mongo `Remove`/`Release` lease filters

**Files:** `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs`.

`RemoveDispatchedTimeoutAsync` and `ReleaseDispatchedTimeoutAsync` currently apply the lease guard only on `(Locked, LockedBy)`. Add the lease-validity predicate:

```csharp
var utcNow = _timeProvider.GetUtcNow();
filter &= Builders<TimeoutData>.Filter.Eq(x => x.Locked, true)
        & Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, owner)
        & Builders<TimeoutData>.Filter.Gt(x => x.LockExpiresAt, utcNow);
```

The `_timeProvider.GetUtcNow()` call sits *after* `cancellationToken.ThrowIfCancellationRequested()` so a cancelled caller doesn't read the clock unnecessarily.

**Behaviour:** An expired-but-not-yet-reaped lease no longer satisfies the filter. The existing zero-match branch already throws `ConcurrencyException`; the message stays the same.

### 4.2 H8 — Mongo owned-row read-back filter

**Files:** `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs`.

In `GetTimeoutsBatchAsync`, the read-back filter currently is:

```csharp
var ownedFilter = Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, sessionId)
                & Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);
```

Add `Gt(LockExpiresAt, utcNow)` — uses the same `utcNow` already captured at the top of the method. This is defensive: the reaper could theoretically clear our lease in the gap between `UpdateMany` and `FindAsync`. Catching that case here keeps the read-back honest.

### 4.3 H9 — InMemory `Remove`/`Release` lease check

**Files:** `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs`.

Current check (line ~154 in `RemoveDispatchedTimeoutAsync`, ~193 in `ReleaseDispatchedTimeoutAsync`):

```csharp
if (!found || !entry!.Data.Locked || entry.Data.LockedBy != owner)
{
    throw new ConcurrencyException(...);
}
```

Tighten:

```csharp
var utcNow = _timeProvider.GetUtcNow();
if (!found
    || !entry!.Data.Locked
    || entry.Data.LockedBy != owner
    || entry.Data.LockExpiresAt is null
    || entry.Data.LockExpiresAt <= utcNow)
{
    throw new ConcurrencyException(...);
}
```

Mirrors Mongo's contract exactly: an expired lease is no longer a valid identity.

### 4.4 H31 — Mongo index migration

**Files:** `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs`.

In `EnsureTimeoutIndexAsync`:

1. **Add** new composite index:
   ```csharp
   new CreateIndexModel<TimeoutData>(
       Builders<TimeoutData>.IndexKeys
           .Ascending(x => x.Time)
           .Ascending(x => x.Locked)
           .Ascending(x => x.LockExpiresAt));
   ```
2. **Drop** legacy `(Locked, Time)` via:
   ```csharp
   try
   {
       await collection.Indexes.DropOneAsync("Locked_1_Time_1", cancellationToken).ConfigureAwait(false);
   }
   catch (MongoCommandException ex) when (ex.Code == 27)
   {
       // IndexNotFound — already dropped, or never existed (fresh DB). Idempotent.
   }
   ```

The other two existing indexes — `(LockedBy, Locked)` for the read-back path, `(LockExpiresAt)` for the reaper — are kept. They serve real query paths.

**Migration:** Existing v7 deployments upgrading to v8 will issue the `DropOneAsync` once on first startup and have it succeed idempotently on subsequent runs. Documented in release notes.

### 4.5 M33 — Broaden `StartSessionAsync` fallback

**Files:** `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs`.

Current:
```csharp
catch (NotSupportedException) { /* fall through */ }
```

Change to:
```csharp
catch (NotSupportedException)
{
    // Standalone mongods / older servers don't support sessions; fall back.
}
catch (MongoException ex)
{
    _logger.LogWarning(ex, "MongoDB session establishment failed; falling back to unsessioned poll.");
}
```

`OperationCanceledException` is NOT caught — caller-token cancellation propagates per Phase 3 discipline.

### 4.6 M34 — Best-effort release on cancellation

**Files:** `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs`.

Restructure the UpdateMany→FindAsync window:

```csharp
var leaseClaimed = false;
try
{
    if (session is not null)
    {
        await collection.UpdateManyAsync(session, batchFilter, lockUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    else
    {
        await collection.UpdateManyAsync(batchFilter, lockUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    leaseClaimed = true;

    // ... read-back FindAsync ...
}
catch (OperationCanceledException)
{
    if (leaseClaimed)
    {
        try
        {
            var releaseFilter = Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, sessionId)
                              & Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);
            var releaseUpdate = Builders<TimeoutData>.Update
                .Set(x => x.Locked, false)
                .Set(x => x.LockedBy, Guid.Empty)
                .Set(x => x.LockExpiresAt, null);
            // CancellationToken.None: the release itself must not be preempted by the
            // same token that triggered this catch.
            await collection.UpdateManyAsync(releaseFilter, releaseUpdate, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort lease release after cancellation failed for session {SessionId}; reaper will reclaim.", sessionId);
        }
    }
    throw;
}
```

The existing outer `catch (MongoException ex) { throw new PersistenceException(...); }` catch stays. The OCE catch is **inside** that try so cancellation isn't accidentally wrapped in `PersistenceException`.

### 4.7 M35 — Candidate sort tie-breaker

**Files:** `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs`.

Change:
```csharp
Builders<TimeoutData>.Sort.Ascending(x => x.Time)
```
to:
```csharp
Builders<TimeoutData>.Sort
    .Ascending(x => x.Time)
    .Ascending(x => x.Id)
```

Mongo can use `(Time, Locked, LockExpiresAt)` as a prefix and apply an in-memory tie-break for the bounded `_batchSize`. Cost is negligible. Eliminates starvation when many timeouts share a `Time`.

### 4.8 Smaller — `InMemoryPersistenceOptions` (breaking)

**Files (new):** `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceOptions.cs`.
**Files (modified):** `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs`, `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs` (DI registration), all test sites and example call sites.

```csharp
namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Configuration for the in-memory persistence stores.
/// </summary>
public sealed class InMemoryPersistenceOptions
{
    /// <summary>
    /// Lease duration for timeouts claimed by <see cref="ITimeoutStore.GetTimeoutsBatchAsync"/>.
    /// Mirrors <see cref="MongoDbPersistenceOptions.TimeoutLockLeaseDuration"/>.
    /// </summary>
    public TimeSpan LockLeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
}
```

`InMemoryTimeoutStore` ctor changes:

```csharp
public InMemoryTimeoutStore(InMemoryPersistenceOptions options, TimeProvider? timeProvider = null)
    : this(options, new InMemoryPersistenceState(timeProvider), timeProvider) { }

internal InMemoryTimeoutStore(InMemoryPersistenceOptions options, InMemoryPersistenceState state, TimeProvider? timeProvider = null)
{
    ArgumentNullException.ThrowIfNull(options);
    if (options.LockLeaseDuration <= TimeSpan.Zero)
    {
        throw new ArgumentOutOfRangeException(
            nameof(options), options.LockLeaseDuration,
            $"{nameof(InMemoryPersistenceOptions.LockLeaseDuration)} must be positive.");
    }
    _state = state ?? throw new ArgumentNullException(nameof(state));
    _timeProvider = timeProvider ?? TimeProvider.System;
    _lockLeaseDuration = options.LockLeaseDuration;
}
```

The legacy `(string="", string="", TimeProvider?=null)` ctor is **removed**. All call sites (DI registration, tests, examples) update to pass `InMemoryPersistenceOptions`. Migration is documented in release notes.

### 4.9 Smaller — Reject `Guid.Empty` Id in InMemory `InsertTimeoutAsync`

```csharp
public Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(timeoutData);
    if (timeoutData.Id == Guid.Empty)
    {
        throw new ArgumentException("TimeoutData.Id must not be Guid.Empty.", nameof(timeoutData));
    }
    cancellationToken.ThrowIfCancellationRequested();
    // ... existing body ...
}
```

Mirrors Mongo's existing `InsertTimeoutAsync` rejection.

### 4.10 Smaller — InMemory in-place mutation comment

`GetTimeoutsBatchAsync` mutates `entry.Data.Locked/LockedBy/LockExpiresAt` in place. The mutation happens under `EnterWriteLock`; `_state.TimeoutsById` and `_state.TimeoutIndex` both hold the same `entry` reference, so in-place mutation is intentional — the index is the single source of truth. Add a comment documenting this invariant; no behavioural change.

### 4.11 Smaller — `SortedSet.Remove` assertion

In `RemoveDispatchedTimeoutAsync`:

```csharp
_state.TimeoutsById.Remove(id);
var removed = _state.TimeoutIndex.Remove(entry!);
Debug.Assert(removed, "TimeoutIndex.Remove returned false; comparer drift between insert and remove.");
```

Catches the latent case where `Time` or `Id` were ever mutated post-insert (today they're not, but the assert documents the invariant).

### 4.12 Smaller — `ReapStaleLeasesAsync` (verification)

Current code at `MongoDbTimeoutStore.cs:262-285` sets `Locked=false, LockedBy=Guid.Empty, LockExpiresAt=null` — semantically correct. The original finding flagged "without restoring `Locked` semantics" but verification confirms the field is set. **No change. Document as no-op in spec self-review.**

### 4.13 Cross-persistor contract on `ITimeoutStore`

**Files:** `src/ServiceConnect.Interfaces/Persistence/ITimeoutStore.cs` (or wherever the interface lives).

Add an XML doc block on the interface (or the `Remove`/`Release` methods) documenting the lease contract:

```xml
/// <remarks>
/// Lease semantics (consistent across all <see cref="ITimeoutStore"/> implementations):
/// <list type="bullet">
/// <item>A worker passing a non-null <c>lockOwner</c> must hold an unexpired lease for the row.</item>
/// <item>A reaper (or the natural lease-expiry path) wins any race with a worker; the worker observes <see cref="ConcurrencyException"/>.</item>
/// <item>An expired-but-not-yet-reaped lease is treated as already invalidated — <c>Remove</c> and <c>Release</c> with such a lease throw <see cref="ConcurrencyException"/>.</item>
/// </list>
/// </remarks>
```

Both persistors reference this contract from their own XML docs.

## 5. Tests

Per Q1's decision, per-backend tests with documented contract — no parity harness.

### 5.1 Mongo — filter-shape tests

New file `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreLeaseFilterTests.cs` (or extend existing — pick whichever keeps file size manageable). Tests:

- `RemoveDispatchedTimeout_FilterIncludesLockExpiresAtPredicate` — render the filter passed to `DeleteOneAsync`; assert JSON contains `"LockExpiresAt"` with `$gt`.
- `ReleaseDispatchedTimeout_FilterIncludesLockExpiresAtPredicate` — same on `UpdateOneAsync` filter.
- `OwnedReadBackFilter_IncludesLockExpiresAtPredicate` — render the filter passed to `FindAsync` after `UpdateMany`; assert `$gt` on `LockExpiresAt`.

The existing `BuildDueTimeoutFilter_IncludesExpiredLeasesForRecovery` test stays as a regression guard for the OR-branch shape.

### 5.2 Mongo — index migration tests

- `EnsureTimeoutIndex_CreatesTimeLockedLockExpiresAtComposite` — assert the `CreateManyAsync` call's `IEnumerable<CreateIndexModel<TimeoutData>>` contains a model whose key spec renders to `{ "Time": 1, "Locked": 1, "LockExpiresAt": 1 }`.
- `EnsureTimeoutIndex_DropsLegacyLockedTimeIndex` — assert `DropOneAsync("Locked_1_Time_1")` is invoked exactly once per call.
- `EnsureTimeoutIndex_SwallowsIndexNotFoundOnDrop` — `DropOneAsync` throws `MongoCommandException` code 27; surrounding call doesn't propagate.
- `EnsureTimeoutIndex_PropagatesOtherDropErrors` — `DropOneAsync` throws `MongoCommandException` code 13 (Unauthorized); surrounding call propagates as `PersistenceException`.

### 5.3 Mongo — M33 fallback test

- `GetTimeoutsBatch_BroadensSessionFallback_LogsAndContinuesOnMongoConfigurationException` — `StartSessionAsync` throws `MongoConfigurationException`; assert the unsessioned path runs (UpdateMany without session, FindAsync without session) AND a Warning is logged. Use the canonical logger-capture pattern (`Mock<ILogger<MongoDbTimeoutStore>>` + `IsEnabled(true)` + `InvocationAction` + `DynamicInvoke`).

### 5.4 Mongo — M34 cancellation orphan tests

- `GetTimeoutsBatch_CancelAfterUpdateMany_ReleasesLeaseBestEffort` — `UpdateManyAsync` succeeds (capture `sessionId`); `FindAsync` throws OCE; assert the second `UpdateManyAsync` is invoked with a filter scoped to that `sessionId` and `CancellationToken.None`.
- `GetTimeoutsBatch_CancelBeforeUpdateMany_DoesNotAttemptRelease` — `UpdateManyAsync` throws OCE on the first call; assert no second `UpdateManyAsync`.
- `GetTimeoutsBatch_CancelDuringBestEffortRelease_SwallowsAndPropagatesOriginalOce` — release `UpdateManyAsync` also throws; assert original OCE propagates and Warning is logged.

### 5.5 Mongo — M35 tie-breaker test

- `GetTimeoutsBatch_CandidateSort_IncludesIdTieBreaker` — capture the `FindOptions<TimeoutData, Guid>.Sort`; render to BSON; assert it contains `{ "Time": 1, "Id": 1 }`.

### 5.6 InMemory — H9 behaviour tests

Extend `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreLeaseTests.cs`:

- `RemoveDispatchedTimeout_ExpiredLease_ThrowsConcurrencyException` — insert, lease via `GetTimeoutsBatchAsync`, advance `FakeTimeProvider` past lease duration, call `Remove(id, lockOwner)`, assert `ConcurrencyException`.
- `ReleaseDispatchedTimeout_ExpiredLease_ThrowsConcurrencyException` — same shape.
- `RemoveDispatchedTimeout_ValidLease_Succeeds` — regression guard.
- `ReleaseDispatchedTimeout_ValidLease_Succeeds` — same.
- `RemoveDispatchedTimeout_NoOwner_RemovesUnconditionally` — regression guard for `lockOwner is null` path.

### 5.7 InMemory — smaller-item tests

- `InsertTimeout_GuidEmpty_ThrowsArgumentException` — parity with Mongo.
- `InsertTimeout_ValidGuid_Succeeds` — regression guard.
- `Constructor_RequiresOptions` — `ArgumentNullException` on null options.
- `Constructor_NonPositiveLeaseDuration_ThrowsArgumentOutOfRange` — covers `TimeSpan.Zero` and negatives.
- `LockLeaseDuration_HonoursOptions` — insert + lease, advance time by `(options.LockLeaseDuration - 1s)`, Get returns empty (lease still held); advance by `1s + epsilon`, Get returns the row (lease expired).

### 5.8 Test discipline

- Per-csproj `dotnet test` only with `-m:1`.
- Logger-capture pattern (`Mock<ILogger>` + `IsEnabled(true)` + `InvocationAction` + `DynamicInvoke`) where logs are asserted.
- Pre-fix run for each behavioural test must demonstrate failure; document in commit message if the failure mode is genuinely new vs. an existing test that already locked it in.
- TDD ordering: write failing test → run pre-fix → apply fix → run post-fix → commit.

## 6. Rollout

**Single PR, multiple commits.** Same pattern as Phases 5/6/7. The phase doc's PR1/PR2/PR3 split is informative for ordering but not required.

Commit order:

1. **Spec** — this document (after self-review).
2. **Plan** — `docs/superpowers/plans/2026-05-02-phase-08-mongo-timeout-store.md`.
3. **H7 + H8** — Mongo lease-filter predicates (`Remove`/`Release`/owned read-back). Filter-shape tests.
4. **H9** — InMemory `Remove`/`Release` reject expired leases. Behaviour tests.
5. **H31** — Add `(Time, Locked, LockExpiresAt)` composite, drop `(Locked, Time)`. Index-shape and drop-idempotency tests.
6. **M33** — Broaden `StartSessionAsync` fallback to `MongoException`. Logger-capture test.
7. **M35** — Add `(Time ASC, Id ASC)` candidate sort. Sort-shape test.
8. **M34** — Best-effort lease release on cancellation. Three tests.
9. **Smaller — `InMemoryPersistenceOptions` (breaking)** — Replace legacy ctor with options-based ctor. Update DI registration and all call sites. Constructor-validation tests.
10. **Smaller — InMemory `Guid.Empty` rejection + `SortedSet.Remove` assert + in-place-mutation comment** — Bundled mechanical changes.
11. **Cross-persistor contract XML doc on `ITimeoutStore`** — Document once on the interface; reference from both persistors.
12. **Phase 8 release notes** — `website/src/content/docs/releases.mdx`.
13. **API reference + learn updates** — `reference/process-managers/`, `reference/configuration/`, `learn/operations/`. Update `examples/ProcessManager/README.md` if any documented behaviour shifts.
14. **Final verification gate + code review** — Per-csproj builds clean; focused test filter passes; final code-reviewer sweep over BASE..HEAD.

**Estimated commit count:** 14-18 (depending on how the smaller items batch and any review-loop fix-up commits).

**Build/test discipline:**

- Per-csproj only:
  - `dotnet build src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj -m:1`
  - `dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj -m:1`
  - `dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1` (after `ITimeoutStore` doc change)
- Test filter: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Timeout|FullyQualifiedName~Lease|FullyQualifiedName~InMemoryPersistence|FullyQualifiedName~MongoDbPersistence" -m:1`.
- Whole-solution `dotnet build`/`dotnet test`/`dotnet format` is forbidden (cgroup wrapper still active; CLAUDE.md unchanged).

## 7. Risk assessment

| Change | Risk | Mitigation |
|---|---|---|
| H31 index migration (drop `(Locked, Time)`) | First-startup-of-v8 issues `DropOneAsync` against existing collections | Idempotent server-side via `IndexNotFound` (code 27) swallow. Documented in release notes. |
| `InMemoryPersistenceOptions` (breaking) | Test/example call sites must update | Surface is narrow (in-memory store mostly used in tests/samples). Release notes call out the migration path. |
| H7/H8/H9 lease-filter tightening | Existing callers relying on expired-lease tolerance break | The previous behaviour was a defect; existing callers were silently incorrect. Cross-persistor contract documented on `ITimeoutStore`. |
| M34 best-effort release | Adds one extra `UpdateManyAsync` round-trip on cancel path | Bounded; only fires when cancellation hits between UpdateMany and FindAsync. Acceptable for prompt recovery. |

## 8. Cross-link — M16

The double-dispatch in `ProcessManagerTimeoutService.PollOnceAsync` (M16, Phase 11) is essentially the same defect surfaced one layer up — the safety-margin guard doesn't close the window because pre-fix `Remove` succeeds even with an expired lease. After H7 lands, M16's window narrows: `Remove` now reliably throws `ConcurrencyException` on an invalidated lease, so `PollOnceAsync` can detect "another worker won the race" and skip the dispatch.

The M16 fix itself remains Phase 11 (it's in core, not the persistor). The plan doc cross-links this both directions.

## 9. Spec self-review

Performed inline 2026-05-02 after writing.

- **Placeholder scan:** No "TBD"/"TODO". Smaller item 4.12 (`ReapStaleLeasesAsync`) is an explicit verification-only entry; outcome is "code already correct, document as no-op." Acceptable.
- **Internal consistency:** Section 4 (per-fix behaviour) and Section 5 (tests) cover the same finding set in the same order. Section 6 (rollout) maps cleanly to Section 4. No contradictions.
- **Scope check:** Single phase, single PR. Findings are bounded to two persistors plus one interface XML doc. Appropriate for a single implementation plan.
- **Ambiguity check:** Q4's "C" decision specifies the legacy ctor is removed — Section 4.8 makes this explicit. Q3's "B" decision specifies the release uses `CancellationToken.None` — Section 4.6 makes this explicit. No remaining ambiguity.

No issues found.
