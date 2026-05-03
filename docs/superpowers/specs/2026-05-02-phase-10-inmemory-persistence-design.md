# Phase 10 — InMemory persistence (design)

**Status:** approved 2026-05-02. Source phase doc: [`consolidated-issues/phases/phase-10-inmemory-persistence.md`](../../../consolidated-issues/phases/phase-10-inmemory-persistence.md).

**Goal:** Sweep the InMemory persistence project. The headline finding (H11) is that the saga finder shares its provider with a publicly-injectable `IKeyValueStore`, so user code can corrupt saga state. Plus ~15 smaller items across `CacheProvider`, `InMemoryAggregatorPersistor`, `InMemoryProcessManagerFinder`, and `DeepClone`. v8 (major) — breaking changes are explicitly permitted.

**Projects affected:**
- `ServiceConnect.Persistence.InMemory` (all sub-folders)
- `ServiceConnect.UnitTests` (Mongo-free; in-process unit tests)
- `website/src/content/docs/releases.mdx`, `website/src/content/docs/reference/...`

**Branch / starting point:** `v7-clean-architecture`, current HEAD post-Phase-9.

---

## 1. Findings in scope

### High

| ID | Summary | Pointer |
|---|---|---|
| H11 | `InMemoryProcessManagerFinder` shares its provider with a publicly-injectable `IKeyValueStore` — user code can break saga invariants | [`InMemoryProcessManagerFinder.cs:99-138`](../../../src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs), [`InMemoryPersistenceExtensions.cs:36-39`](../../../src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs) |

### Smaller (CacheProvider)

| Summary | Pointer |
|---|---|
| `Get<TKey,TValue>` returns `default!` on miss — fragile API; doesn't distinguish absent vs null | [`CacheProvider.cs:82-95`](../../../src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs) |
| `TryPurgeItem` calls `Remove` which throws `ObjectDisposedException` on dispose race | [`CacheProvider.cs:305-317`](../../../src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs) |
| `Add` reads `GetUtcNow` twice (non-monotonic test `TimeProvider`) | [`CacheProvider.cs:50-52`](../../../src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs) |
| `Clear` race window — entries added between `Keys.ToList()` and `_cache.Clear()` lost without event | [`CacheProvider.cs:148`](../../../src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs) |
| Timer callback can re-install a timer post-`Dispose` | [`CacheProvider.cs:295`](../../../src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs) |
| `Update` is silent no-op on missing key while caller advances `Version` | [`CacheProvider.cs:224-240`](../../../src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs) |

### Smaller (InMemoryAggregatorPersistor)

| Summary | Pointer |
|---|---|
| `GetSnapshotAsync` holds the lock for `DeepClone.Clone` JSON round-trips | [`InMemoryAggregatorPersistor.cs:106-129`](../../../src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs) |
| Permanently caches "no accessor" mappings — programmer error becomes silent "no match" + `ConcurrencyException` | [`InMemoryAggregatorPersistor.cs:36`](../../../src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs) |
| `RemoveSnapshotAsync` removes by ephemeral `entry.Id`; verify Mongo `_id` parity | [`InMemoryAggregatorPersistor.cs:204`](../../../src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs) |
| `Contains+Get` TOCTOU repeated 5× | Subsumed by Q2's `TryGet` migration |

### Smaller (InMemoryProcessManagerFinder)

| Summary | Pointer |
|---|---|
| Polymorphic-T fallback path silently rewrites stored generic type | [`InMemoryProcessManagerFinder.cs:96-140, :257-263`](../../../src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs) |
| Returned `MemoryData<T>.Id = Guid.Empty` (parity gap with Mongo) | [`InMemoryProcessManagerFinder.cs:111, :134`](../../../src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs) |
| `Expression.Property` ignores declaring type for explicit-interface impls | [`InMemoryProcessManagerFinder.cs:156`](../../../src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs) |
| String-ctor variant builds its own state; production callers using it would see a separate saga store from the DI singleton | [`InMemoryProcessManagerFinder.cs:21-22`](../../../src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs) |

### Smaller (DeepClone)

| Summary | Pointer |
|---|---|
| Uses `TypeNameHandling.Auto` (deserialization-gadget surface, same-process only) | [`DeepClone.cs:23`](../../../src/ServiceConnect.Persistence.InMemory/DeepClone.cs) |

## 2. Out of scope

- H9 + InMemory timeout smaller items → Phase 8 (shipped).
- Mongo persistence concerns → Phases 8 + 9 (shipped).

## 3. Design decisions

| # | Question | Decision |
|---|---|---|
| Q1 | H11 fix shape | **A.** Give the saga store its own private `CacheProvider`. Mirrors aggregator wiring. Total isolation. |
| Q2 | `Get<>` API | **B.** Replace `Get<>` with `TryGet<>(key, out value)`. v8 breaking. Migrate all internal callers; fixes the 5× `Contains+Get` TOCTOU at the same time. |
| Q3 | DeepClone `TypeNameHandling.Auto` | **A.** Document in-process-only constraint via class XML doc. No code change. |
| Q4 | InMemory string-ctor | **A.** Remove the string-ctor entirely. Mirrors Phase 8's `InMemoryTimeoutStore` removal. |
| Q5 | Smaller correctness items | **Bundle "fail loudly" semantics.** Polymorphic-T mismatch → `InvalidOperationException`. `Update` missing key → `KeyNotFoundException`. `MemoryData<T>.Id` → stable `Guid.NewGuid()` at insert. `_aggregatorAccessors` → no negative cache; throw on first miss. `Expression.Property` → use `MakeMemberAccess` with `PropertyInfo` carrying declaring type. |

## 4. Per-fix behaviour spec

### 4.1 H11 — Private `CacheProvider` for saga finder

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs`.
- Modify: `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs` (every `_state.Provider.*` reference).

`InMemoryPersistenceState` gains a `SagaProvider` field:

```csharp
internal sealed class InMemoryPersistenceState : IDisposable
{
    private int _disposed;
    private readonly IDisposable? _ownedProvider;
    private readonly IDisposable? _ownedSagaProvider;

    public InMemoryPersistenceState(TimeProvider? timeProvider = null)
    {
        var cp = new CacheProvider(timeProvider);
        Provider = cp;
        _ownedProvider = cp;

        // H11 (Phase 10): dedicated saga store. Public IKeyValueStore consumers see
        // Provider only; saga state lives in this private SagaProvider where no
        // user code can reach it. Mirrors how the aggregator persistor isolates
        // its own internal state from the public provider.
        var sagaCp = new CacheProvider(timeProvider);
        SagaProvider = sagaCp;
        _ownedSagaProvider = sagaCp;
    }

    public ICacheProvider Provider { get; }
    public ICacheProvider SagaProvider { get; }
    // ... existing fields unchanged ...

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ownedProvider?.Dispose();
        _ownedSagaProvider?.Dispose();
        SyncRoot.Dispose();
    }
}
```

The internal test-seam ctor `(ICacheProvider provider)` gains a sibling form. Either (a) add `(ICacheProvider provider, ICacheProvider sagaProvider)` overload, or (b) accept both in the existing seam. Pick whichever fits existing test usage.

`InMemoryProcessManagerFinder` switches every `_state.Provider.*` reference to `_state.SagaProvider.*` (per the grep earlier: lines 99, 184, 191, 235, 243, 257, 296, 302, 315).

DI registration in `InMemoryPersistenceExtensions` is unchanged: `IKeyValueStore`/`ICacheProvider` still resolve `Provider`. `SagaProvider` stays internal — accessed only via `_state` from the saga finder.

### 4.2 Q2 — `TryGet<>` replaces `Get<>`

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/Cache/ICacheProvider.cs`.
- Modify: `src/ServiceConnect.Persistence.InMemory/Cache/IKeyValueStore.cs` (if it exposes `Get<>`).
- Modify: `src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs`.
- Modify all internal callers: `InMemoryAggregatorPersistor`, `InMemoryProcessManagerFinder`.

Interface:

```csharp
// before
TValue Get<TKey, TValue>(TKey key);

// after
bool TryGet<TKey, TValue>(TKey key, out TValue? value);
```

Implementation:

```csharp
public bool TryGet<TKey, TValue>(TKey key, out TValue? value)
{
    if (!_cache.TryGetValue(key!, out var cacheItem))
    {
        value = default;
        return false;
    }

    if (cacheItem.RelativeExpiry.HasValue && _slidingTime.TryGetValue(key!, out var sliding))
    {
        sliding.Slide();
    }

    value = (TValue?)cacheItem.Value;
    return true;
}
```

Migrate the 5 `Contains+Get` TOCTOU sites in the aggregator (lines 84-89, 113, 142, 202, 224 per the phase doc) into single `TryGet` calls. Migrate saga finder's get-by-key paths (lines 101, 243, 302).

### 4.3 Q5 — Bundled correctness fixes

**`CacheProvider.Update` throws `KeyNotFoundException`:**

In `Update<TKey,TValue>`, replace the silent no-op when the key is absent with:

```csharp
if (!_cache.ContainsKey(key!))
{
    throw new KeyNotFoundException(
        $"Cannot Update key '{key}' — key not present. Use Add to insert new keys.");
}
```

The aggregator's optimistic-concurrency loop now sees a deterministic exception when the key disappeared mid-update.

**`InMemoryProcessManagerFinder` polymorphic-T throws on type mismatch:**

The current fallback path silently coerces a stored `MemoryData<TStored>` into `MemoryData<T>`. Replace with:

```csharp
if (storedData is not MemoryData<T> typed)
{
    throw new InvalidOperationException(
        $"Saga store contains data of type '{storedData?.GetType().FullName}' but " +
        $"caller asked for '{typeof(MemoryData<T>).FullName}'. The saga store does " +
        "not support polymorphic data type substitution.");
}
return typed;
```

**`MemoryData<T>.Id` stamped at insert:**

`MemoryData<T>.Id` already exists. At insert (line 191 area), set:

```csharp
var memoryData = new MemoryData<T>
{
    Data = data,
    Version = InitialVersion,
    Id = Guid.NewGuid(),  // NEW — stable across reads, parity with Mongo's _id
};
_state.SagaProvider.Add(key, memoryData);
```

The `Id` round-trips on read because `MemoryData<T>` is the stored value and is returned directly via `TryGet` (no copy step that would discard it).

**Aggregator `_aggregatorAccessors` cache-only-positives:**

Don't cache the "no accessor" result. On first miss, throw immediately:

```csharp
if (!_aggregatorAccessors.TryGetValue(messageType, out var accessor))
{
    accessor = ResolveAccessor(messageType);
    if (accessor is null)
    {
        throw new InvalidOperationException(
            $"No aggregator accessor configured for message type '{messageType.FullName}'. " +
            "Register the aggregator before publishing messages of this type.");
    }
    _aggregatorAccessors[messageType] = accessor;
}
```

**`Expression.Property` declaring-type for explicit-interface impls:**

Replace `Expression.Property(left, propertyName)` with `Expression.MakeMemberAccess(left, propInfo)` using the actual `PropertyInfo` from reflection (which carries the correct `DeclaringType`). Resolve `propInfo` once when building the predicate; store it alongside the property name in `mapping.PropertiesHierarchy` (or in the cached predicate metadata).

**Aggregator `entry.Id` parity:** Verify the existing code already stamps a stable Guid at insert; if not, fix to mirror the saga pattern. Document in implementation report.

### 4.4 Q3 — DeepClone documentation

**File:** `src/ServiceConnect.Persistence.InMemory/DeepClone.cs`.

Add class-level XML doc:

```csharp
/// <summary>
/// In-process deep cloning via Newtonsoft.Json round-trip. Used by the InMemory
/// persistence components to clone saga state, aggregator entries, and timeout
/// headers so callers cannot mutate stored data after retrieval.
/// </summary>
/// <remarks>
/// <para>
/// **Security boundary:** This implementation uses
/// <see cref="Newtonsoft.Json.TypeNameHandling.Auto"/> to round-trip polymorphic
/// CLR types (e.g. <c>Dictionary&lt;string, object&gt;</c> headers).
/// <c>TypeNameHandling.Auto</c> is a known deserialization-gadget surface —
/// feeding untrusted JSON through it could load arbitrary types via the
/// <c>$type</c> field.
/// </para>
/// <para>
/// **Use only for in-process trusted data.** Do not extend this helper to
/// deserialise external input, configuration, network payloads, or any value
/// originating outside the current process.
/// </para>
/// </remarks>
internal static class DeepClone
{
    // ... existing implementation unchanged ...
}
```

### 4.5 Q4 — Remove `InMemoryProcessManagerFinder` string-ctor

**File:** `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs:21-22`.

Delete:

```csharp
public InMemoryProcessManagerFinder(string connectionString, string databaseName)
    : this(new ProcessManagerPredicateCache(), new InMemoryPersistenceState(TimeProvider.System)) { }
```

Production callers must use DI; tests use the internal `(ProcessManagerPredicateCache, InMemoryPersistenceState)` ctor. Mirrors Phase 8's `InMemoryTimeoutStore` ctor removal.

Update any test/sample that constructs via the string-ctor; surface in implementation report.

### 4.6 CacheProvider mechanical fixes

**File:** `src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs`.

- **`Add` double `GetUtcNow`:** Read once into a local; reuse for both the comparison and the diff calculation.
- **`Clear` race comment:** Add a short comment acknowledging best-effort consistency under concurrent adds (matches `ConcurrentDictionary.Clear` semantics).
- **`TryPurgeItem` dispose race:** Wrap the `Remove` call in `try { ... } catch (ObjectDisposedException) { }` so the timer-callback path doesn't escape during dispose unwind.
- **Timer re-install post-Dispose:** Inside the timer callback, before re-installing a follow-up timer, check `Volatile.Read(ref _disposed) != 0 → return`.

## 5. Tests

All InMemory tests are unit tests in `src/ServiceConnect.UnitTests/`. No Testcontainers / E2E required.

### 5.1 H11 partition tests

**New file `src/ServiceConnect.UnitTests/Persistence/InMemoryPersistenceStatePartitionTests.cs`:**
- `SagaProvider_IsDistinctInstance_FromPublicProvider`
- `UserKeysAddedToProvider_NotVisibleInSagaProvider`
- `SagaKeysWritten_NotVisibleViaPublicKeyValueStore`
- `BothProviders_DisposedTogether`

### 5.2 `TryGet` migration tests

Extend `CacheProviderTests.cs` (or create):
- `TryGet_KeyAbsent_ReturnsFalseAndDefault`
- `TryGet_KeyPresent_ReturnsTrueAndValue`
- `TryGet_KeyPresentWithNullValue_ReturnsTrueAndNull`
- `TryGet_SlidingExpiry_RefreshesOnRead`

Aggregator + saga finder existing tests automatically exercise the migrated `TryGet` calls.

### 5.3 Q5 correctness fixes

- `Update_KeyAbsent_ThrowsKeyNotFoundException`
- `Update_KeyPresent_ReplacesValueWithoutThrowing`
- `FindData_StoredTypeMismatchesT_ThrowsInvalidOperation`
- `FindData_StoredTypeMatchesT_ReturnsMatch`
- `InsertData_StampsStableId`
- `Id_RoundTripsAcrossUpdate`
- `Insert_UnregisteredMessageType_ThrowsInvalidOperationOnFirstCall`
- `Insert_UnregisteredMessageType_ThrowsAgainOnSubsequentCall`
- `FindData_ExplicitInterfaceImpl_BuildsCorrectPredicate`

### 5.4 Aggregator lock-hold refactor

- `GetSnapshotAsync_ReturnsClonesIndependentOfStorage`
- `GetSnapshotAsync_AllowsConcurrentInsert` (timing-based; preferred over a lock-state probe)

### 5.5 CacheProvider mechanical fixes

- `TryPurgeItem_DuringDispose_DoesNotThrow`
- `TimerCallback_AfterDispose_DoesNotReinstall`
- `Add_ReadsTimeProviderOnce`
- `Clear_ConcurrentAdd_BestEffort` (regression-pin the documented contract)

### 5.6 Q4 — string-ctor removal

Compile-fail regression: `grep` for any test using `new InMemoryProcessManagerFinder("...", "...")`; update or remove. Surface in implementation report.

### 5.7 Test discipline

- All new tests in `src/ServiceConnect.UnitTests/Persistence/` (or alongside existing files following the naming convention).
- Per-csproj `dotnet test` only with `-m:1`.
- Test filter: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemory|FullyQualifiedName~CacheProvider|FullyQualifiedName~DeepClone" -m:1`.
- Pre-fix verification per finding: each behavioural test must demonstrably fail against pre-fix code (or be a regression guard — note in commit message which kind).

## 6. Rollout

**Single PR, multiple commits.** Same pattern as Phases 7/8/9.

Commit order:
1. Spec.
2. Plan.
3. H11 — `SagaProvider` partition + tests.
4. CacheProvider — `TryGet` API replacement + tests.
5. CacheProvider — `Update` throws on missing key + tests.
6. CacheProvider — mechanical fixes (Add double-clock, TryPurgeItem dispose race, timer re-install, Clear race comment) + tests.
7. ProcessManagerFinder — polymorphic-T throws + test.
8. ProcessManagerFinder — `MemoryData<T>.Id` stamped at insert + tests.
9. ProcessManagerFinder — `Expression.Property` declaring-type fix + test.
10. ProcessManagerFinder — remove string-ctor (breaking) + test cleanup.
11. AggregatorPersistor — snapshot-then-clone refactor + test.
12. AggregatorPersistor — `_aggregatorAccessors` cache-only-positives + test.
13. AggregatorPersistor — verify stable Id parity (likely no-op).
14. DeepClone — security boundary documentation.
15. Phase 10 release notes.
16. API reference + learn updates.
17. Final verification gate + code review.

**Estimated commit count:** 15-20 (including review-loop fix-ups).

**Build/test discipline:**
- `dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj -m:1`
- Test filter as in §5.7.
- Whole-solution `dotnet build`/`dotnet test` forbidden.

## 7. Risk assessment

| Change | Risk | Mitigation |
|---|---|---|
| H11 partition | Existing tests reaching into `_state.Provider` to seed saga state break loudly | Update test seams to use `_state.SagaProvider` |
| `TryGet<>` replaces `Get<>` (breaking) | External `IKeyValueStore` consumers using `Get` break loudly | v8 major; release notes; clear migration |
| String-ctor removal | External code using `new InMemoryProcessManagerFinder("","")` breaks | v8 major; matches Phase 8 pattern |
| `Update` throws on missing key | Callers relying on silent no-op break loudly | Pre-fix behaviour was a defect; loud failure correct |
| Polymorphic-T throws | Code depending on the silent rewrite breaks loudly | Pre-fix behaviour was a defect; loud failure correct |
| `_aggregatorAccessors` no negative cache | First-miss throws instead of silent ConcurrencyException | Programmer error becomes diagnostic; net win |

## 8. Spec self-review

Performed inline 2026-05-02.

- **Placeholder scan:** No "TBD"/"TODO". §4.3's "Aggregator entry.Id parity" notes "verify the existing code already stamps a stable Guid at insert; if not, fix" — explicit verification step, not unresolved.
- **Internal consistency:** §4 (per-fix), §5 (tests), §6 (rollout) cover the same finding set in the same order. Q-decisions in §3 map cleanly to §4.
- **Scope check:** Single phase, single project (`ServiceConnect.Persistence.InMemory`), no Mongo / E2E surface. Appropriate for one implementation plan.
- **Ambiguity check:** §4.1's H11 test-seam ctor option (dual-param vs both-in-existing-seam) is the only "either" item; left as implementation choice. §4.3's "stamp Guid.NewGuid() at insert; preserve across reads" is concrete (insert-time write; no copy step would discard).

No issues found.
