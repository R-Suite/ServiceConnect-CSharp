# Phase 10 — InMemory persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sweep the InMemory persistence project. Fix H11 (saga state can be corrupted via the public `IKeyValueStore`) plus ~15 smaller correctness/API/hygiene items across `CacheProvider`, `InMemoryAggregatorPersistor`, `InMemoryProcessManagerFinder`, and `DeepClone`. v8 (major) — breaking changes are explicitly permitted.

**Architecture:** Three coordinated changes: (1) `InMemoryPersistenceState` gains a private `SagaProvider` `CacheProvider`, used exclusively by `InMemoryProcessManagerFinder`; the public `Provider` (registered as `IKeyValueStore`/`ICacheProvider`) is unchanged in role but no longer holds saga state. (2) `Get<TKey,TValue>` is replaced with `TryGet<TKey,TValue>(key, out value)` across `ICacheProvider` and `IKeyValueStore`; all 9 internal callers migrate atomically. (3) Fail-loudly semantics for previously-silent failure paths: `Update` on missing key, polymorphic-T mismatch, missing aggregator accessors. Plus mechanical fixes (TOCTOU, dispose races, double-clock reads), documentation (DeepClone security boundary), and ctor cleanup (string-ctor removal).

**Tech Stack:** .NET multi-target net8.0/net10.0, xUnit + Moq, no Testcontainers (in-process unit tests only), Astro/Starlight for docs.

**Spec:** [`docs/superpowers/specs/2026-05-02-phase-10-inmemory-persistence-design.md`](../specs/2026-05-02-phase-10-inmemory-persistence-design.md).

---

## Build/test safety

This machine has crashed when running unconstrained whole-solution `dotnet build` / `dotnet test`. The wrapper at `~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup (8 cores / 8 GiB / 200 tasks). **Always use per-csproj invocations with `-m:1`.** Test filter for this phase: `--filter "FullyQualifiedName~InMemory|FullyQualifiedName~CacheProvider|FullyQualifiedName~DeepClone"`.

---

## File structure

### Modified — production code

- `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs` — Task 3 (add `SagaProvider`).
- `src/ServiceConnect.Persistence.InMemory/Cache/ICacheProvider.cs` — Task 4 (replace `Get` with `TryGet`).
- `src/ServiceConnect.Persistence.InMemory/Cache/IKeyValueStore.cs` — Task 4 (replace `Get` with `TryGet`).
- `src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs` — Tasks 4 (TryGet impl), 5 (Update throws), 6 (mechanical fixes).
- `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs` — Tasks 3 (use `SagaProvider`), 4 (TryGet migration), 7 (polymorphic-T throws), 8 (Id stamping), 9 (declaring-type fix), 10 (ctor removal).
- `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs` — Tasks 4 (TryGet migration of 5 sites), 11 (snapshot-then-clone), 12 (no negative cache), 13 (verify Id parity).
- `src/ServiceConnect.Persistence.InMemory/DeepClone.cs` — Task 14 (security XML doc).

### Created — tests

- `src/ServiceConnect.UnitTests/Persistence/InMemoryPersistenceStatePartitionTests.cs` — Task 3.
- `src/ServiceConnect.UnitTests/Persistence/CacheProviderTryGetTests.cs` — Task 4.
- `src/ServiceConnect.UnitTests/Persistence/CacheProviderUpdateThrowsTests.cs` — Task 5.
- `src/ServiceConnect.UnitTests/Persistence/CacheProviderMechanicalFixesTests.cs` — Task 6.
- `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderPolymorphicTests.cs` — Task 7.
- `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderIdTests.cs` — Task 8.
- `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderInterfacePropertyTests.cs` — Task 9.
- `src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorLockHoldTests.cs` — Task 11.
- `src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorAccessorTests.cs` — Task 12.

### Modified — tests

- Existing `CacheProviderTests.cs` — keep where applicable.
- Existing `InMemoryAggregatorPersistorTests.cs`, `InMemoryProcessManagerFinderTests.cs`, etc. — migrate to TryGet API; remove string-ctor uses.

### Modified — website

- `website/src/content/docs/releases.mdx` — Task 15.
- `website/src/content/docs/reference/extension-points/persistence/...` — Task 16.

---

## Task 1: Spec

**Already shipped at commit `eae11e26`** (`docs(spec): phase 10 inmemory persistence`). Skip.

---

## Task 2: This plan

```bash
git add docs/superpowers/plans/2026-05-02-phase-10-inmemory-persistence.md
git commit -m "docs(plan): phase 10 implementation plan"
```

(Co-Authored-By: `Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.)

---

## Task 3: H11 — Private `SagaProvider` partition

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs`.
- Modify: `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs` (every `_state.Provider.*`).
- Create: `src/ServiceConnect.UnitTests/Persistence/InMemoryPersistenceStatePartitionTests.cs`.

**Background.** `_state.Provider` is registered as `IKeyValueStore`/`ICacheProvider` and ALSO used by the saga finder. User code consuming `IKeyValueStore` can read/corrupt saga state. Add a separate private `SagaProvider`.

- [ ] **Step 1: Write the failing partition tests**

Create `src/ServiceConnect.UnitTests/Persistence/InMemoryPersistenceStatePartitionTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryPersistenceStatePartitionTests
{
    [Fact]
    public void SagaProvider_IsDistinctInstance_FromPublicProvider()
    {
        using var state = new InMemoryPersistenceState(new FakeTimeProvider());
        Assert.NotSame(state.Provider, state.SagaProvider);
    }

    [Fact]
    public void UserKeysAddedToProvider_NotVisibleInSagaProvider()
    {
        using var state = new InMemoryPersistenceState(new FakeTimeProvider());
        state.Provider.Add("user/foo", new object(), CacheItemPriority.Normal);

        Assert.False(state.SagaProvider.TryGet<string, object>("user/foo", out _));
    }

    [Fact]
    public void SagaKeysAddedToSagaProvider_NotVisibleInPublicProvider()
    {
        using var state = new InMemoryPersistenceState(new FakeTimeProvider());
        state.SagaProvider.Add("saga/foo", new object(), CacheItemPriority.Normal);

        Assert.False(state.Provider.TryGet<string, object>("saga/foo", out _));
    }

    [Fact]
    public void Dispose_DisposesBothProviders()
    {
        var state = new InMemoryPersistenceState(new FakeTimeProvider());
        state.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            state.Provider.Add("k", new object(), CacheItemPriority.Normal));
        Assert.Throws<ObjectDisposedException>(() =>
            state.SagaProvider.Add("k", new object(), CacheItemPriority.Normal));
    }
}
```

NOTE: `TryGet<TKey,TValue>` doesn't exist yet — Task 4 adds it. For this Task, write the tests assuming the post-Task-4 API; running them pre-Task-4 will fail to compile. Either land Task 4 first OR scope these tests around `Contains` for now and migrate in Task 4.

**Practical sequencing:** Land Task 4 (TryGet API) BEFORE Task 3, OR write Task 3's tests using `Contains` and convert in Task 4. Pick the order that fits your worktree state. The plan is laid out with H11 first because it's the headline; pragmatically, Task 4 may need to land first.

If you take Task 4 first, swap Tasks 3 and 4 in your commit order. The rest of the plan is unaffected.

- [ ] **Step 2: Run pre-fix**

Expected: 3-4 tests fail (no `SagaProvider` property exists). The `Dispose` test passes pre-fix because `Provider` is already disposed.

- [ ] **Step 3: Apply the fix to `InMemoryPersistenceState`**

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
        // user code can reach it.
        var sagaCp = new CacheProvider(timeProvider);
        SagaProvider = sagaCp;
        _ownedSagaProvider = sagaCp;
    }

    /// <summary>
    /// Test-seam constructor: accepts externally-supplied providers so tests can
    /// control both behaviours independently.
    /// </summary>
    internal InMemoryPersistenceState(ICacheProvider provider, ICacheProvider sagaProvider)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        SagaProvider = sagaProvider ?? throw new ArgumentNullException(nameof(sagaProvider));
        _ownedProvider = null;
        _ownedSagaProvider = null;
    }

    public ICacheProvider Provider { get; }
    public ICacheProvider SagaProvider { get; }
    public ReaderWriterLockSlim SyncRoot { get; } = new();
    public SortedSet<TimeoutEntry> TimeoutIndex { get; } = new(TimeoutEntryComparer.Instance);
    public Dictionary<Guid, TimeoutEntry> TimeoutsById { get; } = [];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _ownedProvider?.Dispose();
        _ownedSagaProvider?.Dispose();
        SyncRoot.Dispose();
    }
}
```

The existing internal `(ICacheProvider provider)` ctor is replaced with `(ICacheProvider, ICacheProvider)`. Update any test that uses the old single-argument seam.

- [ ] **Step 4: Switch saga finder to `SagaProvider`**

In `InMemoryProcessManagerFinder.cs`, replace every `_state.Provider.*` with `_state.SagaProvider.*`. Per the grep: lines 99, 184, 191, 235, 243, 257, 296, 302, 315.

```bash
grep -n "_state\.Provider\." src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs
```

After this step, the count should be zero.

- [ ] **Step 5: Run focused tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryPersistenceStatePartitionTests" -m:1
```

Expected: 4/4 pass.

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemory|FullyQualifiedName~CacheProvider|FullyQualifiedName~DeepClone" -m:1
```

Expected: all pass (saga-finder tests still work because the SagaProvider behaves identically).

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceState.cs \
        src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs \
        src/ServiceConnect.UnitTests/Persistence/InMemoryPersistenceStatePartitionTests.cs
git commit -m "fix(inmemory)!: partition saga state into private SagaProvider (H11)"
```

(Co-Authored-By trailer; `!` for the architectural change in observable state visibility.)

---

## Task 4: Q2 — Replace `Get<TKey,TValue>` with `TryGet<TKey,TValue>`

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/Cache/ICacheProvider.cs`.
- Modify: `src/ServiceConnect.Persistence.InMemory/Cache/IKeyValueStore.cs`.
- Modify: `src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs:82-95`.
- Modify: `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs` (3 sites: lines 101, 243, 302).
- Modify: `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs` (6 sites: lines 89, 113, 142, 202, 224, 249).
- Create: `src/ServiceConnect.UnitTests/Persistence/CacheProviderTryGetTests.cs`.

**Background.** `Get<>` returns `default!` on miss → can't distinguish absent vs null → forces 5× `Contains+Get` TOCTOU pattern in the aggregator. Replace with `TryGet<>` (v8 breaking).

- [ ] **Step 1: Write failing tests**

Create `src/ServiceConnect.UnitTests/Persistence/CacheProviderTryGetTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class CacheProviderTryGetTests
{
    [Fact]
    public void TryGet_KeyAbsent_ReturnsFalseAndDefault()
    {
        var cache = new CacheProvider(new FakeTimeProvider());
        var found = cache.TryGet<string, string>("missing", out var value);
        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void TryGet_KeyPresent_ReturnsTrueAndValue()
    {
        var cache = new CacheProvider(new FakeTimeProvider());
        cache.Add("k", "v", CacheItemPriority.Normal);
        var found = cache.TryGet<string, string>("k", out var value);
        Assert.True(found);
        Assert.Equal("v", value);
    }

    [Fact]
    public void TryGet_KeyPresentWithNullValue_ReturnsTrueAndNull()
    {
        var cache = new CacheProvider(new FakeTimeProvider());
        cache.Add<string, object?>("k", null, CacheItemPriority.Normal);
        var found = cache.TryGet<string, object?>("k", out var value);
        Assert.True(found);
        Assert.Null(value);
    }

    [Fact]
    public void TryGet_SlidingExpiry_RefreshesOnRead()
    {
        var clock = new FakeTimeProvider();
        var cache = new CacheProvider(clock);
        cache.Add("k", "v", TimeSpan.FromSeconds(10), CacheItemPriority.Normal);

        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.True(cache.TryGet<string, string>("k", out _));

        // After read, sliding expiry resets — advance 8s more (16s since insert) and the key still exists.
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.True(cache.TryGet<string, string>("k", out _));
    }
}
```

- [ ] **Step 2: Run tests pre-fix**

Expected: 4/4 fail to compile (no `TryGet` method exists).

- [ ] **Step 3: Replace `Get` with `TryGet` in `ICacheProvider`**

In `src/ServiceConnect.Persistence.InMemory/Cache/ICacheProvider.cs`, replace:

```csharp
// before
TValue Get<TKey, TValue>(TKey key);

// after
/// <summary>
/// Tries to get a value from the cache for the specified key.
/// </summary>
/// <returns>
/// <see langword="true"/> if the key is present (the stored value, possibly <see langword="null"/>,
/// is written to <paramref name="value"/>); <see langword="false"/> otherwise.
/// Distinguishes "key absent" from "key present with null value" — pre-v8's <c>Get</c> returned
/// <c>default!</c> in both cases.
/// </returns>
bool TryGet<TKey, TValue>(TKey key, out TValue? value);
```

- [ ] **Step 4: Replace `Get` with `TryGet` in `IKeyValueStore`**

In `src/ServiceConnect.Persistence.InMemory/Cache/IKeyValueStore.cs`, same replacement.

- [ ] **Step 5: Implement `TryGet` in `CacheProvider`**

In `src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs`, replace the `Get<TKey,TValue>` body (lines 82-95):

```csharp
// before
public TValue Get<TKey, TValue>(TKey key)
{
    if (!_cache.TryGetValue(key!, out var cacheItem))
    {
        return default!;
    }

    if (cacheItem.RelativeExpiry.HasValue && _slidingTime.TryGetValue(key!, out var sliding))
    {
        sliding.Slide();
    }

    return (TValue)cacheItem.Value!;
}

// after
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

- [ ] **Step 6: Migrate `InMemoryProcessManagerFinder` callers (3 sites)**

Line 101 (in `Keys()` enumeration loop):

```csharp
// before
var value = _state.SagaProvider.Get<string, object>(key.ToString()!);

// after
if (!_state.SagaProvider.TryGet<string, object>(key.ToString()!, out var value))
{
    continue; // key disappeared between Keys() snapshot and read
}
```

Line 243 (in `UpdateDataAsync`):

```csharp
// before
var storedData = _state.SagaProvider.Get<string, object>(key) ?? throw new ConcurrencyException(...);

// after
if (!_state.SagaProvider.TryGet<string, object>(key, out var storedData) || storedData is null)
{
    throw new ConcurrencyException(...);
}
```

Line 302 (in `DeleteDataAsync`): same pattern as line 243.

NOTE: lines 184 and 235 and 296 use `Contains`. Migrate these to `TryGet` too where feasible; the `Contains+Get` pattern can be collapsed. But if the `Contains` site is followed by a non-Get-style branch, leave it as-is.

- [ ] **Step 7: Migrate `InMemoryAggregatorPersistor` callers (6 sites)**

Replace each `Contains+Get` TOCTOU pattern. Example for line 89:

```csharp
// before
if (_provider.Contains(name))
{
    var source = (List<Entry>)_provider.Get<string, object>(name);
    // ... use source
}

// after
if (_provider.TryGet<string, object>(name, out var sourceObj) && sourceObj is List<Entry> source)
{
    // ... use source
}
```

Apply to all 5 `Contains+Get` sites (lines 89, 113, 142, 202, 224). Line 249 in `CountAsync` (or similar) is a single Get without Contains; convert to:

```csharp
// before
return (List<Entry>)_provider.Get<string, object>(name);

// after — caller-side semantics: missing name returns empty list
return _provider.TryGet<string, object>(name, out var listObj) && listObj is List<Entry> list
    ? list
    : new List<Entry>();
```

Adapt to the actual containing method's signature.

- [ ] **Step 8: Build + run tests**

```bash
dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemory|FullyQualifiedName~CacheProvider|FullyQualifiedName~DeepClone" -m:1
```

Expected: build clean (0 errors, 0 warnings); all pass.

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/Cache/ICacheProvider.cs \
        src/ServiceConnect.Persistence.InMemory/Cache/IKeyValueStore.cs \
        src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs \
        src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs \
        src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs \
        src/ServiceConnect.UnitTests/Persistence/CacheProviderTryGetTests.cs
git commit -m "feat(inmemory-cache)!: replace Get<TKey,TValue> with TryGet<TKey,TValue> (Q2)"
```

(Co-Authored-By trailer; `!` for breaking API change.)

---

## Task 5: `CacheProvider.Update` throws `KeyNotFoundException`

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs:224-240`.
- Create: `src/ServiceConnect.UnitTests/Persistence/CacheProviderUpdateThrowsTests.cs`.

- [ ] **Step 1: Write failing tests**

Create `src/ServiceConnect.UnitTests/Persistence/CacheProviderUpdateThrowsTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class CacheProviderUpdateThrowsTests
{
    [Fact]
    public void Update_KeyAbsent_ThrowsKeyNotFoundException()
    {
        var cache = new CacheProvider(new FakeTimeProvider());
        var ex = Assert.Throws<KeyNotFoundException>(() =>
            cache.Update("missing", "value"));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void Update_KeyPresent_ReplacesValueWithoutThrowing()
    {
        var cache = new CacheProvider(new FakeTimeProvider());
        cache.Add("k", "v1", CacheItemPriority.Normal);
        cache.Update("k", "v2");

        Assert.True(cache.TryGet<string, string>("k", out var value));
        Assert.Equal("v2", value);
    }
}
```

- [ ] **Step 2: Run tests pre-fix**

Expected: `Update_KeyAbsent_ThrowsKeyNotFoundException` fails — pre-fix the call is silent no-op.

- [ ] **Step 3: Apply the fix**

In `src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs`, find the `Update<TKey,TValue>` method (around line 224). Replace the silent no-op:

```csharp
public void Update<TKey, TValue>(TKey key, TValue value)
{
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    if (!_cache.ContainsKey(key!))
    {
        // Pre-Phase-10 this was a silent no-op — which let aggregator's optimistic-
        // concurrency loop advance Version against a phantom row. Throw so the caller
        // can react deterministically.
        throw new KeyNotFoundException(
            $"Cannot Update key '{key}' — key not present. Use Add to insert new keys.");
    }

    // existing update logic — preserve the in-place CacheItem replacement that retains
    // the original priority, sliding window, and timer state.
    // ... existing body ...
}
```

The exact existing body should be preserved; only the early-return-on-missing is replaced with a throw.

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~CacheProviderUpdateThrowsTests|FullyQualifiedName~InMemoryAggregator" -m:1
```

Expected: pass. The aggregator's existing optimistic-update loop should still work because it always either (a) Adds first then Updates, or (b) the caller's loop catches the exception and retries.

If a pre-existing aggregator test relies on the silent no-op semantics, surface in your report.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs \
        src/ServiceConnect.UnitTests/Persistence/CacheProviderUpdateThrowsTests.cs
git commit -m "fix(inmemory-cache)!: Update throws KeyNotFoundException on missing key"
```

(Co-Authored-By trailer; `!` for behaviour change.)

---

## Task 6: CacheProvider mechanical fixes

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs` (4 sites).
- Create: `src/ServiceConnect.UnitTests/Persistence/CacheProviderMechanicalFixesTests.cs`.

**Four mechanical fixes:**

1. `Add` (around line 50) reads `GetUtcNow` twice — read once, reuse.
2. `Clear` (line 148) — add a comment about best-effort consistency under concurrent adds.
3. `TryPurgeItem` (lines 305-317) — wrap `Remove` in a `try/catch (ObjectDisposedException)` so the timer-callback path doesn't escape during dispose.
4. Timer callback (line 295) — check `_disposed` before re-installing a follow-up timer.

- [ ] **Step 1: Read the current sites**

```bash
sed -n '45,57p' src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs
sed -n '290,320p' src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs
```

- [ ] **Step 2: Write tests**

Create `src/ServiceConnect.UnitTests/Persistence/CacheProviderMechanicalFixesTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class CacheProviderMechanicalFixesTests
{
    [Fact]
    public void Add_AbsoluteExpiryInPast_ThrowsArgumentOutOfRange()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new CacheProvider(clock);

        var pastTime = clock.GetUtcNow() - TimeSpan.FromMinutes(1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            cache.Add("k", "v", pastTime));
    }

    [Fact]
    public void TryPurgeItem_AfterDispose_DoesNotThrow()
    {
        // Schedule a key with a short timeout, dispose the cache, ensure no
        // ObjectDisposedException escapes from the timer callback path.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new CacheProvider(clock);

        cache.Add("k", "v", TimeSpan.FromSeconds(1), CacheItemPriority.Normal);
        cache.Dispose();

        // Advance time past the timeout; this would have triggered the timer callback.
        // Post-fix: the callback path must swallow ObjectDisposedException.
        clock.Advance(TimeSpan.FromSeconds(2));

        // No assertion needed; the test passes if no exception escapes.
    }
}
```

NOTE: timer callbacks fire on a thread-pool thread; FakeTimeProvider's Advance triggers them synchronously. The actual mechanism depends on `CacheProvider`'s timer implementation — verify by reading the source. If timers don't fire deterministically with FakeTimeProvider, simplify the test to direct-invocation of internal `TryPurgeItem` after dispose.

- [ ] **Step 3: Apply the fixes**

**Fix 1 — `Add` double-clock (around line 50):**

```csharp
// before
public void Add<TKey, TValue>(TKey key, TValue value, DateTimeOffset absoluteExpiry, CacheItemPriority priority = CacheItemPriority.Normal)
{
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    if (absoluteExpiry < _timeProvider.GetUtcNow())
    {
        throw new ArgumentOutOfRangeException(nameof(absoluteExpiry), "Absolute expiry must be in the future.");
    }

    var diff = absoluteExpiry - _timeProvider.GetUtcNow();
    Add(key, value, diff, priority, false);
}

// after
public void Add<TKey, TValue>(TKey key, TValue value, DateTimeOffset absoluteExpiry, CacheItemPriority priority = CacheItemPriority.Normal)
{
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    // Single clock read — defensive against a non-monotonic test TimeProvider where
    // a second GetUtcNow() call could observe an earlier time, producing a negative diff.
    var now = _timeProvider.GetUtcNow();
    if (absoluteExpiry < now)
    {
        throw new ArgumentOutOfRangeException(nameof(absoluteExpiry), "Absolute expiry must be in the future.");
    }

    var diff = absoluteExpiry - now;
    Add(key, value, diff, priority, false);
}
```

**Fix 2 — `Clear` race comment (around line 148):**

Add a comment immediately above `var removedKeys = _cache.Keys.ToList();`:

```csharp
// Best-effort consistency: entries added between this snapshot and `_cache.Clear()`
// are dropped without firing KeyRemoved. Matches ConcurrentDictionary.Clear's own
// no-snapshot semantics — callers needing strict cross-thread consistency should
// synchronise externally.
var removedKeys = _cache.Keys.ToList();
```

**Fix 3 — `TryPurgeItem` dispose-race catch (around line 305):**

Find `TryPurgeItem` and wrap the `Remove` call:

```csharp
// before — sketch
private void TryPurgeItem(object? state)
{
    var key = (TKey)state!;
    Remove(key);
}

// after
private void TryPurgeItem(object? state)
{
    var key = state!;
    try
    {
        Remove(key);
    }
    catch (ObjectDisposedException)
    {
        // The cache was disposed while a timer callback was already in flight.
        // Swallow — the dispose path takes ownership of cleanup; the timer's
        // best-effort invocation here is redundant.
    }
}
```

**Fix 4 — timer re-install post-dispose (around line 295):**

In whichever method re-installs the timer, add a guard:

```csharp
// before — sketch
_timers[key] = _timeProvider.CreateTimer(TryPurgeItem, key, ...);

// after
if (Volatile.Read(ref _disposed) != 0)
{
    return; // dispose ran first; don't re-install a timer that holds a captured 'this'
}
_timers[key] = _timeProvider.CreateTimer(TryPurgeItem, key, ...);
```

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~CacheProvider" -m:1
```

Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs \
        src/ServiceConnect.UnitTests/Persistence/CacheProviderMechanicalFixesTests.cs
git commit -m "fix(inmemory-cache): mechanical fixes (double-clock, dispose race, timer re-install, Clear comment)"
```

---

## Task 7: InMemoryProcessManagerFinder polymorphic-T throws

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs:96-140` (and `:257-263` if applicable).
- Create: `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderPolymorphicTests.cs`.

- [ ] **Step 1: Read the current fallback path**

```bash
sed -n '95,145p' src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs
```

Identify where the fallback rewrites the stored generic type.

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderPolymorphicTests.cs`:

```csharp
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryProcessManagerFinderPolymorphicTests
{
    public sealed class SagaTypeA : IProcessManagerData { public Guid CorrelationId { get; set; } }
    public sealed class SagaTypeB : IProcessManagerData { public Guid CorrelationId { get; set; } }

    [Fact]
    public async Task FindData_StoredTypeMismatchesT_ThrowsInvalidOperation()
    {
        // Insert a SagaTypeA via a finder of one shape, then attempt to retrieve it
        // as SagaTypeB. Pre-fix: silent rewrite. Post-fix: throw.
        // ... build via test-seam ctor with a shared SagaProvider so both finders see the same data.

        // (Adapt the construction shape to the actual InMemoryProcessManagerFinder ctor signature
        // — likely (ProcessManagerPredicateCache, InMemoryPersistenceState).)
    }

    [Fact]
    public async Task FindData_StoredTypeMatchesT_ReturnsMatch()
    {
        // Regression guard: same-type retrieval works as before.
    }
}
```

The exact arrange shape depends on the finder's API. Read existing `InMemoryProcessManagerFinderTests.cs` and copy its canonical construction pattern.

- [ ] **Step 3: Run tests pre-fix**

Expected: `FindData_StoredTypeMismatchesT_ThrowsInvalidOperation` fails (no throw).

- [ ] **Step 4: Apply the fix**

In the fallback path (around lines 96-140), replace the silent rewrite with:

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

- [ ] **Step 5: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryProcessManagerFinder" -m:1
git add src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs \
        src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderPolymorphicTests.cs
git commit -m "fix(inmemory-saga)!: polymorphic-T mismatch throws InvalidOperationException"
```

---

## Task 8: `MemoryData<T>.Id` stamped at insert

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs:111, :134` (insert + post-update sites).
- Modify: `src/ServiceConnect.Persistence.InMemory/ProcessManager/MemoryData.cs` if `Id` isn't present.
- Create: `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderIdTests.cs`.

- [ ] **Step 1: Verify `MemoryData<T>` has `Id` property**

```bash
cat src/ServiceConnect.Persistence.InMemory/ProcessManager/MemoryData.cs
```

If `Id` is missing, add `public Guid Id { get; set; }`.

- [ ] **Step 2: Write the failing test**

```csharp
[Fact]
public async Task InsertData_StampsStableId()
{
    // Insert; retrieve; assert Id != Guid.Empty and stays the same across reads.
}

[Fact]
public async Task Id_RoundTripsAcrossUpdate()
{
    // Insert; capture Id; update; assert Id unchanged.
}
```

- [ ] **Step 3: Apply the fix at insert sites (lines 111 and 134)**

```csharp
// before (sketch)
var memoryData = new MemoryData<T>
{
    Data = data,
    Version = InitialVersion,
};

// after
var memoryData = new MemoryData<T>
{
    Data = data,
    Version = InitialVersion,
    Id = Guid.NewGuid(),  // M-parity with Mongo's _id
};
```

Verify `Update` paths preserve the `Id` (don't regenerate it).

- [ ] **Step 4: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryProcessManagerFinderIdTests" -m:1
git add src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs \
        src/ServiceConnect.Persistence.InMemory/ProcessManager/MemoryData.cs \
        src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderIdTests.cs
git commit -m "fix(inmemory-saga): stamp stable MemoryData<T>.Id at insert (parity with Mongo)"
```

---

## Task 9: `Expression.MakeMemberAccess` for explicit-interface impls

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs:156`.
- Create: `src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderInterfacePropertyTests.cs`.

**Background.** `Expression.Property(left, propertyName)` looks up the property by string name on the runtime type, missing properties that are explicit-interface implementations.

- [ ] **Step 1: Read the current site**

```bash
sed -n '150,165p' src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs
```

- [ ] **Step 2: Write the failing test**

```csharp
public sealed class ExplicitInterfaceSagaData : IProcessManagerData, IFooSource
{
    public Guid CorrelationId { get; set; }
    Guid IFooSource.FooId { get; } = Guid.NewGuid();  // explicit-interface impl
}

[Fact]
public async Task FindData_ExplicitInterfaceImpl_BuildsCorrectPredicate()
{
    // Map a message property to IFooSource.FooId; assert the predicate finds the row.
}
```

- [ ] **Step 3: Apply the fix**

At line 156:

```csharp
// before
left = Expression.Property(left, prop.Key);

// after — lookup the PropertyInfo with declaring-type fidelity for explicit-interface impls
var propInfo = left.Type.GetProperty(prop.Key,
    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
    ?? left.Type.GetInterfaces()
        .Select(i => i.GetProperty(prop.Key, BindingFlags.Public | BindingFlags.Instance))
        .FirstOrDefault(p => p is not null)
    ?? throw new InvalidOperationException($"Property '{prop.Key}' not found on type '{left.Type.FullName}' or its interfaces.");
left = Expression.MakeMemberAccess(left, propInfo);
```

The exact lookup logic depends on the existing reflection style — adapt to match. Key invariant: the resulting expression accesses the property via the correct declaring type.

- [ ] **Step 4: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryProcessManagerFinderInterfacePropertyTests|FullyQualifiedName~InMemoryProcessManagerFinder" -m:1
git add src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs \
        src/ServiceConnect.UnitTests/Persistence/InMemoryProcessManagerFinderInterfacePropertyTests.cs
git commit -m "fix(inmemory-saga): MakeMemberAccess uses declaring type for explicit-interface impls"
```

---

## Task 10: Remove `InMemoryProcessManagerFinder` string-ctor

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs:21-22`.
- Update any test/sample that uses it.

- [ ] **Step 1: Find all callers**

```bash
grep -rn 'new InMemoryProcessManagerFinder("' src/ examples/ 2>/dev/null | head
```

- [ ] **Step 2: Update each caller**

Each caller switches to either DI-injection or the internal `(ProcessManagerPredicateCache, InMemoryPersistenceState)` ctor. For tests:

```csharp
// before
var finder = new InMemoryProcessManagerFinder("connStr", "dbName");

// after
var state = new InMemoryPersistenceState(TimeProvider.System);
var finder = new InMemoryProcessManagerFinder(new ProcessManagerPredicateCache(), state);
```

- [ ] **Step 3: Delete the string-ctor**

In `src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs`, remove lines 21-22:

```csharp
// REMOVE:
public InMemoryProcessManagerFinder(string connectionString, string databaseName)
    : this(new ProcessManagerPredicateCache(), new InMemoryPersistenceState(TimeProvider.System)) { }
```

- [ ] **Step 4: Build + run tests**

```bash
dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemory" -m:1
```

Expected: build clean; all tests pass (after caller updates).

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs \
        src/ServiceConnect.UnitTests/<any modified test files>
git commit -m "feat(inmemory-saga)!: remove string-ctor from InMemoryProcessManagerFinder"
```

---

## Task 11: Aggregator snapshot-then-clone refactor

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs:106-129` (`GetSnapshotAsync` body).
- Create: `src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorLockHoldTests.cs`.

- [ ] **Step 1: Read the current `GetSnapshotAsync`**

```bash
sed -n '100,135p' src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs
```

- [ ] **Step 2: Write the timing test**

Create `src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorLockHoldTests.cs`:

```csharp
[Fact]
public async Task GetSnapshotAsync_AllowsConcurrentInsert()
{
    // Two tasks: one calling GetSnapshotAsync (slow), one calling InsertDataAsync.
    // Pre-fix: insert blocks until snapshot completes.
    // Post-fix: insert proceeds because the lock is released after the snapshot copy.
    // Use Task.WhenAny with a tight timeout to detect blocking; if insert returns within
    // the same window as snapshot, the test passes.
}

[Fact]
public async Task GetSnapshotAsync_ReturnsClonesIndependentOfStorage()
{
    // Insert; snapshot; mutate the snapshot; assert subsequent reads aren't affected.
}
```

The timing test is sensitive — adapt to the actual `InMemoryAggregatorPersistor` API. If timing-based assertions are flaky, replace with a simpler "snapshot returns deep-cloned objects" regression guard.

- [ ] **Step 3: Apply the refactor**

In `GetSnapshotAsync`, restructure to:

```csharp
// before — clones inside the lock
lock (_memoryCacheLock)
{
    if (!_provider.TryGet<string, object>(name, out var src) || src is not List<Entry> source)
        return /* empty snapshot */;

    foreach (var entry in source)
    {
        clones.Add(DeepClone.Clone(entry.Data));
        ids.Add(entry.Id);
    }
}

// after — copy references under lock, clone outside
List<Entry> entriesCopy;
lock (_memoryCacheLock)
{
    if (!_provider.TryGet<string, object>(name, out var src) || src is not List<Entry> source)
        return /* empty snapshot */;
    entriesCopy = source.ToList();  // shallow copy of references
}

// Clone outside the lock — DeepClone.Clone is JSON round-trip and slow.
foreach (var entry in entriesCopy)
{
    clones.Add(DeepClone.Clone(entry.Data));
    ids.Add(entry.Id);
}
```

The aggregator's existing snapshot returns a list of cloned messages and ids — keep the same contract.

- [ ] **Step 4: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryAggregatorPersistor" -m:1
git add src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs \
        src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorLockHoldTests.cs
git commit -m "perf(inmemory-aggregator): snapshot under lock; clone outside"
```

---

## Task 12: Aggregator no-negative-cache for accessors

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs:28-39` (the `CorrelationIdAccessors` cache).
- Create: `src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorAccessorTests.cs`.

- [ ] **Step 1: Write the failing test**

```csharp
public sealed class NoCorrelationIdType { public int OtherProp { get; set; } }

[Fact]
public async Task Insert_TypeWithoutCorrelationId_ThrowsInvalidOperationOnFirstCall()
{
    var persistor = new InMemoryAggregatorPersistor("", "", "", null);
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        persistor.InsertDataAsync(new NoCorrelationIdType { OtherProp = 1 }, "test-name"));
    Assert.Contains(typeof(NoCorrelationIdType).FullName!, ex.Message);
}

[Fact]
public async Task Insert_TypeWithoutCorrelationId_ThrowsAgainOnSubsequentCall()
{
    var persistor = new InMemoryAggregatorPersistor("", "", "", null);
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        persistor.InsertDataAsync(new NoCorrelationIdType { OtherProp = 1 }, "test-name"));

    // Second call: pre-fix the negative result was cached and the call returned silently
    // with a no-op accessor. Post-fix: each call resolves freshly and throws.
    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        persistor.InsertDataAsync(new NoCorrelationIdType { OtherProp = 1 }, "test-name"));
}
```

- [ ] **Step 2: Apply the fix**

In `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs`, replace the `GetCorrelationId` static helper around line 21-39:

```csharp
// before
private static Guid? GetCorrelationId(object data)
{
    if (data is Message message)
    {
        return message.CorrelationId;
    }

    var accessor = CorrelationIdAccessors.GetOrAdd(data.GetType(), static type =>
    {
        var prop = type.GetProperty("CorrelationId", BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || prop.PropertyType != typeof(Guid) || prop.GetMethod is null)
        {
            return static _ => null;  // <-- caches a negative result
        }

        return obj => (Guid?)prop.GetValue(obj);
    });
    return accessor(data);
}

// after
private static Guid GetCorrelationId(object data)
{
    if (data is Message message)
    {
        return message.CorrelationId;
    }

    // Cache only positive resolutions. A type without a Guid CorrelationId property is
    // a programmer error — throw on first miss instead of caching a no-op accessor that
    // would convert programmer errors into silent ConcurrencyExceptions later.
    var accessor = CorrelationIdAccessors.GetOrAdd(data.GetType(), static type =>
    {
        var prop = type.GetProperty("CorrelationId", BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || prop.PropertyType != typeof(Guid) || prop.GetMethod is null)
        {
            // Sentinel: throw delegate captured for fast-fail without negative caching.
            // The throw happens on every invocation — but the delegate itself is reusable.
            return obj => throw new InvalidOperationException(
                $"Aggregator data type '{obj.GetType().FullName}' does not have a public " +
                "'Guid CorrelationId' property. Add the property or use a Message subtype.");
        }

        return obj => (Guid)prop.GetValue(obj)!;
    });
    return accessor(data);
}
```

NOTE: changing the return type from `Guid?` to `Guid` may ripple. Inspect callers; if any caller checks for null, they need adjustment. Likely cleaner: keep the `Guid?` return type but make the throw-delegate explicitly throw. Choose what fits the surrounding code.

- [ ] **Step 3: Run tests + commit**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryAggregator" -m:1
git add src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs \
        src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorAccessorTests.cs
git commit -m "fix(inmemory-aggregator)!: throw on missing CorrelationId accessor; no negative cache"
```

---

## Task 13: Verify aggregator stable Id parity

**File:** `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs`.

- [ ] **Step 1: Verify `Entry.Id` is stamped at insert**

Read `InMemoryAggregatorPersistor.cs` around the `InsertDataAsync` body. The existing code likely creates `Entry(Guid.NewGuid(), data)` already (per the record definition at line 56). If yes, document as a no-op and skip Step 2.

- [ ] **Step 2: If Id is NOT stamped, fix it**

Add the stamp at insert; preserve across reads.

- [ ] **Step 3: Commit (only if a change was needed)**

If no change: skip; document the no-op in the implementation report.

```bash
# only if a change was actually made
git add src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs
git commit -m "fix(inmemory-aggregator): stamp stable Entry.Id at insert (parity with Mongo)"
```

---

## Task 14: DeepClone security boundary documentation

**File:** `src/ServiceConnect.Persistence.InMemory/DeepClone.cs`.

- [ ] **Step 1: Replace the class XML doc**

In `DeepClone.cs`, replace the class XML doc with:

```csharp
/// <summary>
/// In-process deep cloning via Newtonsoft.Json round-trip. Used by the InMemory
/// persistence components to clone saga state, aggregator entries, and timeout
/// headers so callers cannot mutate stored data after retrieval.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security boundary:</b> This implementation uses
/// <see cref="Newtonsoft.Json.TypeNameHandling.Auto"/> to round-trip polymorphic
/// CLR types (e.g. <c>Dictionary&lt;string, object&gt;</c> headers).
/// <c>TypeNameHandling.Auto</c> is a known deserialization-gadget surface —
/// feeding untrusted JSON through it could load arbitrary types via the
/// <c>$type</c> field.
/// </para>
/// <para>
/// <b>Use only for in-process trusted data.</b> Do not extend this helper to
/// deserialise external input, configuration, network payloads, or any value
/// originating outside the current process.
/// </para>
/// </remarks>
internal static class DeepClone
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj -m:1
git add src/ServiceConnect.Persistence.InMemory/DeepClone.cs
git commit -m "docs(inmemory): document DeepClone security boundary"
```

---

## Task 15: Phase 10 release notes

**File:** `website/src/content/docs/releases.mdx`.

- [ ] **Step 1: Insert the section**

Find the prior phase entry (`### Mongo aggregator + process-manager + serializer`); insert immediately after:

```mdx
### InMemory persistence

**Bug fixes**

- **Saga state isolated from public `IKeyValueStore`.** `InMemoryProcessManagerFinder` now uses a private `SagaProvider` distinct from the public `Provider` exposed via `IKeyValueStore`/`ICacheProvider`. User code reading or writing through `IKeyValueStore` cannot accidentally observe or corrupt saga state.
- **`Update` throws on missing key.** `CacheProvider.Update<TKey,TValue>` now throws `KeyNotFoundException` if the key is absent. Pre-fix it was a silent no-op while the caller advanced version state — sagas could wedge on the next real conflict.
- **Saga store rejects polymorphic-T mismatches.** `InMemoryProcessManagerFinder.FindDataAsync<T>` throws `InvalidOperationException` when the stored row's data type doesn't match `T`. Pre-fix the fallback path silently rewrote the type.
- **`MemoryData<T>.Id` stamped at insert.** Each saga row gets a stable `Guid.NewGuid()` on insert, mirroring Mongo's `_id`. Pre-fix the Id was `Guid.Empty`.
- **Aggregator missing-accessor errors are loud.** Inserting an aggregator data type without a public `Guid CorrelationId` property now throws `InvalidOperationException` on first call, naming the offending type. Pre-fix the negative result was cached and silently produced empty matches.
- **Aggregator `GetSnapshotAsync` no longer holds the lock during clone.** Snapshot copies entries under lock; `DeepClone` runs outside, allowing concurrent inserts to proceed during long snapshot operations.
- **Saga predicate handles explicit-interface impls.** `Expression.MakeMemberAccess` resolves the property by the correct declaring type, fixing a silent no-match for sagas mapping explicit-interface properties.
- **Mechanical CacheProvider hardening.** `Add` reads the clock once (was twice — non-monotonic on test `TimeProvider`s); `TryPurgeItem` swallows `ObjectDisposedException` during dispose race; the timer callback no longer re-installs after dispose; `Clear`'s race semantics are documented.

**Behaviour changes (breaking)**

- **`Get<TKey,TValue>` replaced with `TryGet<TKey,TValue>(key, out value)`.** Both `ICacheProvider` and `IKeyValueStore` switch to the `TryGet` shape so callers can distinguish "key absent" from "key present with null value." External consumers using `Get` must migrate.
- **`InMemoryProcessManagerFinder(string, string)` ctor removed.** Production callers must use DI-registered components; tests use the internal `(ProcessManagerPredicateCache, InMemoryPersistenceState)` ctor. Mirrors Phase 8's `InMemoryTimeoutStore` ctor cleanup.
- **`InMemoryPersistenceState.Provider` no longer holds saga state.** External code reaching into `Provider` looking for saga keys finds nothing; saga keys live in the private `SagaProvider`.

**New surface**

- `ICacheProvider.SagaProvider` (internal access only via `InMemoryPersistenceState`) — dedicated saga store separate from the public `Provider`.
- `ICacheProvider.TryGet<TKey,TValue>(key, out value)` and `IKeyValueStore.TryGet<TKey,TValue>(key, out value)` — replaces `Get<TKey,TValue>`.
```

- [ ] **Step 2: Build + commit**

```bash
npm --prefix website run build 2>&1 | tail -3
git add website/src/content/docs/releases.mdx
git commit -m "docs(website): phase 10 release notes"
```

---

## Task 16: API reference + learn updates

**Files:**
- `website/src/content/docs/reference/extension-points/persistence/...` (saga store contract).
- `website/src/content/docs/reference/...` (cache provider / key-value store API).

- [ ] **Step 1: Locate relevant pages**

```bash
grep -rln "ICacheProvider\|IKeyValueStore\|InMemoryProcessManagerFinder\|InMemoryAggregatorPersistor" website/src/content/docs/ 2>/dev/null | head
```

- [ ] **Step 2: Update the cache-provider reference**

Document the `Get → TryGet` migration with a before/after example:

```mdx
### `ICacheProvider.TryGet<TKey,TValue>` (v8)

Pre-v8 the cache provider exposed `Get<TKey,TValue>(key)` which returned `default!` on miss — callers couldn't distinguish "key absent" from "key present with null value." v8 replaces this with `TryGet<TKey,TValue>(key, out value)`:

```csharp
// before (v7)
var value = cache.Get<string, MyType>(key);
if (value is null) { /* could be either absent or null */ }

// after (v8)
if (cache.TryGet<string, MyType>(key, out var value))
{
    // key was present; value may still be null
}
else
{
    // key was absent
}
```
```

- [ ] **Step 3: Update the saga store extension-point page**

Add a section about the InMemory store's isolation contract:

```mdx
### InMemory saga store isolation

`InMemoryProcessManagerFinder` uses a dedicated private `SagaProvider` instance, separate from the public `ICacheProvider`/`IKeyValueStore` registered for general cache use. User code consuming `IKeyValueStore` cannot read, modify, or delete saga state through that interface. This isolation matches the Mongo persistor's behaviour, where saga state lives in dedicated collections that the user-facing API doesn't expose.
```

- [ ] **Step 4: Build + commit**

```bash
npm --prefix website run build 2>&1 | tail -3
git add website/src/content/docs/
git commit -m "docs(website): InMemory persistence contract + TryGet migration"
```

---

## Task 17: Final verification gate + code review

- [ ] **Step 1: Per-csproj build clean**

```bash
dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj -m:1
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Focused unit-test pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemory|FullyQualifiedName~CacheProvider|FullyQualifiedName~DeepClone" -m:1
```

Expected: all pass.

- [ ] **Step 3: Astro build**

```bash
npm --prefix website run build 2>&1 | tail -3
```

Expected: clean.

- [ ] **Step 4: Grep verifications**

```bash
# H11: SagaProvider is the only path to saga keys
grep -n "_state\.Provider\." src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs
# Expected: zero hits.

grep -n "_state\.SagaProvider\." src/ServiceConnect.Persistence.InMemory/ProcessManager/InMemoryProcessManagerFinder.cs
# Expected: matches the count from before (lines 99, 184, 191, 235, 243, 257, 296, 302, 315 → 9-ish).

# Q2: TryGet migration complete; no leftover Get<TKey,TValue>
grep -rn "\.Get<\(string\|.*\),\(.*\)>" src/ServiceConnect.Persistence.InMemory/ --include="*.cs" | grep -v Get_
# Expected: zero hits beyond non-cache Get methods.

# Q4: string-ctor gone
grep -n "new InMemoryProcessManagerFinder(" src/ServiceConnect.Persistence.InMemory/
# Expected: zero hits in production code.

# Q5: Update throws
grep -n "KeyNotFoundException" src/ServiceConnect.Persistence.InMemory/Cache/CacheProvider.cs
# Expected: at least 1 hit.
```

- [ ] **Step 5: Final code review (optional given budget; per-task reviews already performed)**

If running the final review: dispatch `superpowers:code-reviewer` over Phase 10 commits (from `eae11e26` through HEAD). Briefing template — same as Phases 8/9.

If skipping: ensure each per-task review caught Critical/Important issues; document in commit history.

- [ ] **Step 6: Cleanup commit (if needed)**

If review surfaced issues, fix in a follow-up commit:

```bash
git add <only-cleanup-files>
git commit -m "cleanup(phase-10): address final-review findings"
```

---

## Phase 10 done

All findings closed. The phase ships:

- H11 saga state private partition.
- Cache `TryGet` API replacing the absent-vs-null-ambiguous `Get`.
- `Update` fail-loudly on missing key.
- CacheProvider mechanical hardening (clock, dispose race, timer re-install).
- Saga finder polymorphic-T fail-loudly + Id stamping + interface-property fix + ctor cleanup.
- Aggregator snapshot-then-clone perf + no-negative-cache for accessors.
- DeepClone security boundary documented.
- Release notes + API reference updates.

Move to writing the closing summary; the user's standard pattern is "phase complete; continue to phase N+1?".
