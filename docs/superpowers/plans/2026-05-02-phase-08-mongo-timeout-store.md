# Phase 08 — Mongo timeout-store + lease guards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore lease-expiry semantics across `MongoDbTimeoutStore` and `InMemoryTimeoutStore`, align Mongo indexes with the actual due query, and harden cancellation between `UpdateMany` and `FindAsync`. v8 (major) — breaking changes are explicitly permitted.

**Architecture:** Three coordinated tightenings: (1) every lease-checked filter on both persistors gains `LockExpiresAt > utcNow` so an expired lease is no longer a valid identity (H7/H8/H9); (2) Mongo indexes drop the suboptimal `(Locked, Time)` and add `(Time, Locked, LockExpiresAt)` to cover both branches of the OR-shaped due filter (H31 — uses `DropOneAsync` idempotent over `IndexNotFound` code 27); (3) the UpdateMany→FindAsync window adds a best-effort lease release on cancellation (M34) plus broader fallback (M33) and sort tie-breaker (M35). InMemory gains an `InMemoryPersistenceOptions` class mirroring Mongo's options shape (smaller, breaking).

**Tech Stack:** .NET multi-target net8.0/net10.0, MongoDB.Driver, xUnit + Moq, `Microsoft.Extensions.Time.Testing.FakeTimeProvider`, Astro/Starlight for docs. No E2E tests; all unit-level (Mongo via Moq filter-shape rendering, InMemory via real in-process state).

**Spec:** [`docs/superpowers/specs/2026-05-02-phase-08-mongo-timeout-store-design.md`](../specs/2026-05-02-phase-08-mongo-timeout-store-design.md).

---

## Build/test safety

This machine has crashed when running unconstrained whole-solution `dotnet build` / `dotnet test` (CLAUDE.md has the full incident analysis). The wrapper at `~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup with `CPUQuota=800%`, `MemoryMax=8G`, `MemorySwapMax=0`, `TasksMax=200`. **Even with the wrapper, every command in this plan is per-csproj.** Add `-m:1` to `dotnet test` invocations to serialize MSBuild within `TasksMax`. Never run whole-solution `dotnet build` / `dotnet test` / `dotnet format`.

---

## File structure

### Modified — production code

- `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs` — Tasks 3 (H7+H8), 6 (H31), 7 (M33), 8 (M35), 9 (M34).
- `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs` — Tasks 4 (H9), 5 (options ctor), 11 (smaller items).
- `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs` — Task 5 (DI registration uses new options).
- `src/ServiceConnect.Interfaces/Persistence/ITimeoutStore.cs` — Task 12 (cross-persistor contract XML doc).

### Created — production code

- `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceOptions.cs` — Task 5 (new options class).

### Created — tests

- `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreLeaseFilterTests.cs` — Task 3 (H7+H8 filter-shape tests).
- `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreIndexMigrationTests.cs` — Task 6 (H31 add+drop tests).
- `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreSessionFallbackTests.cs` — Task 7 (M33 fallback test).
- `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreSortTieBreakerTests.cs` — Task 8 (M35 sort-shape test).
- `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreCancelOrphanTests.cs` — Task 9 (M34 cancellation tests).
- `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreOptionsTests.cs` — Task 5 (constructor-validation tests).

### Modified — tests

- `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreLeaseTests.cs` — Task 4 (H9 expired-lease assertions); Task 5 (ctor-shape updates); Task 11 (in-place + assert).
- `src/ServiceConnect.UnitTests/InMemoryTimeoutStoreConcurrencyTests.cs` — Task 5 (ctor-shape updates).
- `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreGuidEmptyTests.cs` (new) — Task 11 (Guid.Empty rejection test).

### Modified — website / examples

- `website/src/content/docs/releases.mdx` — Task 13.
- `website/src/content/docs/reference/configuration/...` — Task 14 (`InMemoryPersistenceOptions` documented; Mongo lease semantics).
- `website/src/content/docs/reference/process-managers/...` — Task 14 (timeout dispatch behaviour).
- `website/src/content/docs/learn/operations/...` — Task 14 (timeout / lease section).
- `examples/ProcessManager/README.md` — Task 14 (verify call sites if InMemory ctor was used).

---

## Task 1: Spec

**Already shipped at commit `cc0fa9d1`** (`docs(spec): phase 08 mongo timeout-store + lease guards`). Skip.

---

## Task 2: This plan

**This is the plan commit.** After writing this file:

```bash
git add docs/superpowers/plans/2026-05-02-phase-08-mongo-timeout-store.md
git commit -m "docs(plan): phase 08 implementation plan"
```

(Co-Authored-By trailer.)

---

## Task 3: H7 + H8 — Mongo lease-filter predicates

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs` (three filter sites: owned read-back at ~line 163, Remove at ~line 199, Release at ~line 233).
- Create: `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreLeaseFilterTests.cs`.

**Background.** All three lease-checked filters today ignore `LockExpiresAt`. An expired-but-not-yet-reaped lease still satisfies `Eq(Locked, true) & Eq(LockedBy, owner)`, so a worker holding a stale lease can still `Remove` or `Release`. Add `Gt(LockExpiresAt, utcNow)` to all three.

- [ ] **Step 1: Read the three current filter sites**

```bash
grep -n "Eq(x => x.Locked, true)\|Eq(x => x.LockedBy" src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs
```

Expected: three sites — owned read-back (one filter spans two lines combined with `&`), Remove (one filter), Release (one filter). All three currently lack `LockExpiresAt`.

- [ ] **Step 2: Write the failing test file**

Create `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreLeaseFilterTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbTimeoutStoreLeaseFilterTests
{
    static MongoDbTimeoutStoreLeaseFilterTests()
    {
        // Match the existing fixture's static-cctor pattern in MongoDbTimeoutStoreTests.cs:
        // BSON Guid serializer must be registered before any filter rendering.
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    private static (MongoDbTimeoutStore Store, Mock<IMongoCollection<TimeoutData>> Collection)
        BuildStoreCapturingFilters()
    {
        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
        indexes.Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["ok"]);
        // Idempotent index drop: matches the post-Task-6 shape and is a no-op pre-Task-6.
        indexes.Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MongoDB.Bson.BsonDocument { ["ok"] = 1 });

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);

        // DeleteOneAsync (Remove) and UpdateOneAsync (Release) capture the filter for assertion.
        collection.Setup(c => c.DeleteOneAsync(It.IsAny<FilterDefinition<TimeoutData>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteResult.Acknowledged(1));
        collection.Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateResult.Acknowledged(1, 1, null));

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null))
            .Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        var store = new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            NullLogger<MongoDbTimeoutStore>.Instance);

        return (store, collection);
    }

    private static string Render(FilterDefinition<TimeoutData> filter) =>
        filter.Render(
            BsonSerializer.LookupSerializer<TimeoutData>(),
            BsonSerializer.SerializerRegistry).ToJson();

    [Fact]
    public async Task RemoveDispatchedTimeout_FilterIncludesLockExpiresAtPredicate()
    {
        var (store, collection) = BuildStoreCapturingFilters();
        FilterDefinition<TimeoutData>? captured = null;
        collection.Setup(c => c.DeleteOneAsync(It.IsAny<FilterDefinition<TimeoutData>>(), It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, CancellationToken>((f, _) => captured = f)
            .ReturnsAsync(new DeleteResult.Acknowledged(1));

        var owner = Guid.NewGuid();
        await store.RemoveDispatchedTimeoutAsync(Guid.NewGuid(), owner);

        Assert.NotNull(captured);
        var json = Render(captured!);
        Assert.Contains("\"LockExpiresAt\"", json);
        Assert.Contains("\"$gt\"", json);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_FilterIncludesLockExpiresAtPredicate()
    {
        var (store, collection) = BuildStoreCapturingFilters();
        FilterDefinition<TimeoutData>? captured = null;
        collection.Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, UpdateDefinition<TimeoutData>, UpdateOptions, CancellationToken>(
                (f, _, _, _) => captured = f)
            .ReturnsAsync(new UpdateResult.Acknowledged(1, 1, null));

        var owner = Guid.NewGuid();
        await store.ReleaseDispatchedTimeoutAsync(Guid.NewGuid(), owner);

        Assert.NotNull(captured);
        var json = Render(captured!);
        Assert.Contains("\"LockExpiresAt\"", json);
        Assert.Contains("\"$gt\"", json);
    }

    [Fact]
    public async Task GetTimeoutsBatch_OwnedReadBackFilter_IncludesLockExpiresAtPredicate()
    {
        // The owned read-back is the FindAsync-after-UpdateMany inside GetTimeoutsBatchAsync.
        // To capture the read-back filter we need a UpdateMany that succeeds and a FindAsync
        // that we can intercept. The existing BuildStoreCapturingFilters doesn't wire FindAsync
        // — extend inline.
        var (store, collection) = BuildStoreCapturingFilters();

        // UpdateMany succeeds (lease claim).
        collection.Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateResult.Acknowledged(0, 0, null));

        // First FindAsync = candidate-id query (returns empty so we exit early before the
        // owned read-back). Capture the SECOND FindAsync's filter — the read-back — by
        // setting up an empty cursor for the candidate query and a callback for the read-back.
        FilterDefinition<TimeoutData>? readBackFilter = null;

        // For the candidate-id FindAsync (returns empty), we need any FindAsync<Guid>; for the
        // read-back FindAsync, we need FindAsync<TimeoutData>. The driver routes both through
        // the same generic API but with different projections — match by the expected projected
        // type.
        var emptyCursor = new Mock<IAsyncCursor<Guid>>();
        emptyCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        emptyCursor.SetupGet(c => c.Current).Returns(Array.Empty<Guid>());
        // Note: the production code returns early when candidateIds is empty; the read-back
        // FindAsync<TimeoutData> never fires. To exercise the read-back, return ONE candidate id.
        var oneIdCursor = new Mock<IAsyncCursor<Guid>>();
        var seq = oneIdCursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()));
        seq.ReturnsAsync(true).ReturnsAsync(false);
        oneIdCursor.SetupGet(c => c.Current).Returns(new[] { Guid.NewGuid() });

        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(oneIdCursor.Object);

        var emptyTimeoutCursor = new Mock<IAsyncCursor<TimeoutData>>();
        emptyTimeoutCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        emptyTimeoutCursor.SetupGet(c => c.Current).Returns(Array.Empty<TimeoutData>());
        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, TimeoutData>>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, FindOptions<TimeoutData, TimeoutData>, CancellationToken>(
                (f, _, _) => readBackFilter = f)
            .ReturnsAsync(emptyTimeoutCursor.Object);

        await store.GetTimeoutsBatchAsync();

        Assert.NotNull(readBackFilter);
        var json = Render(readBackFilter!);
        Assert.Contains("\"LockExpiresAt\"", json);
        Assert.Contains("\"$gt\"", json);
    }
}
```

- [ ] **Step 3: Run the tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStoreLeaseFilterTests" -m:1
```

Expected: 3 FAIL — the rendered filter JSON does NOT contain `"LockExpiresAt"` or `"$gt"`.

- [ ] **Step 4: Apply the H7 fix to `RemoveDispatchedTimeoutAsync`**

In `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs`, change the lease-checked branch:

```csharp
// before
if (lockOwner is { } owner)
{
    filter &= Builders<TimeoutData>.Filter.Eq(x => x.Locked, true) &
              Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, owner);
}

// after
if (lockOwner is { } owner)
{
    var utcNow = _timeProvider.GetUtcNow();
    filter &= Builders<TimeoutData>.Filter.Eq(x => x.Locked, true) &
              Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, owner) &
              Builders<TimeoutData>.Filter.Gt(x => x.LockExpiresAt, utcNow);
}
```

- [ ] **Step 5: Apply the H7 fix to `ReleaseDispatchedTimeoutAsync`**

Same shape:

```csharp
if (lockOwner is { } owner)
{
    var utcNow = _timeProvider.GetUtcNow();
    filter &= Builders<TimeoutData>.Filter.Eq(x => x.Locked, true) &
              Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, owner) &
              Builders<TimeoutData>.Filter.Gt(x => x.LockExpiresAt, utcNow);
}
```

- [ ] **Step 6: Apply the H8 fix to the owned read-back**

In `GetTimeoutsBatchAsync`, change the read-back filter (the `utcNow` is already captured at the top of the method):

```csharp
// before
var ownedFilter = Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, sessionId)
                & Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);

// after
var ownedFilter = Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, sessionId)
                & Builders<TimeoutData>.Filter.Eq(x => x.Locked, true)
                & Builders<TimeoutData>.Filter.Gt(x => x.LockExpiresAt, utcNow);
```

- [ ] **Step 7: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStoreLeaseFilterTests|FullyQualifiedName~MongoDbTimeoutStoreTests" -m:1
```

Expected: all pass — the new 3 plus the existing `BuildDueTimeoutFilter` and Remove/Release tests in `MongoDbTimeoutStoreTests.cs`.

- [ ] **Step 8: Commit**

```bash
git add src/ServiceConnect.UnitTests/MongoDbTimeoutStoreLeaseFilterTests.cs \
        src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs
git commit -m "fix(mongo-timeout): lease filters reject expired leases (H7+H8)"
```

(Co-Authored-By trailer.)

---

## Task 4: H9 — InMemory `Remove`/`Release` reject expired leases

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs` (Remove at ~line 154, Release at ~line 193).
- Modify: `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreLeaseTests.cs` — add new tests.

**Background.** The InMemory persistor's lease check currently rejects on `(!Locked || LockedBy != owner)` but accepts when the lease has already expired. Mirror the new Mongo contract.

- [ ] **Step 1: Read the existing InMemory ctor pattern used in tests**

```bash
grep -n "new InMemoryTimeoutStore\|InMemoryPersistenceState" src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreLeaseTests.cs | head -10
```

Tests use the internal ctor `new InMemoryTimeoutStore(state, timeProvider)`. The public ctor will be replaced in Task 5; for THIS task, keep the existing internal ctor unchanged.

- [ ] **Step 2: Write the failing tests**

Add to `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreLeaseTests.cs` (follow the existing test class's helper conventions — `FakeTimeProvider`, `InsertAndLeaseAsync` if one exists, otherwise inline):

```csharp
[Fact]
public async Task RemoveDispatchedTimeout_ExpiredLease_ThrowsConcurrencyException()
{
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
    var state = new InMemoryPersistenceState(clock);
    var store = new InMemoryTimeoutStore(state, clock);

    var id = Guid.NewGuid();
    await store.InsertTimeoutAsync(new TimeoutData
    {
        Id = id,
        Destination = "dest",
        ProcessManagerId = Guid.NewGuid(),
        Time = clock.GetUtcNow(),
        Headers = new Dictionary<string, object>(StringComparer.Ordinal),
    });

    var batch = await store.GetTimeoutsBatchAsync();
    Assert.Single(batch.DueTimeouts);
    var owner = batch.DueTimeouts[0].LockedBy;

    // Advance past the 5-minute default lease.
    clock.Advance(TimeSpan.FromMinutes(6));

    await Assert.ThrowsAsync<ConcurrencyException>(() =>
        store.RemoveDispatchedTimeoutAsync(id, owner));
}

[Fact]
public async Task ReleaseDispatchedTimeout_ExpiredLease_ThrowsConcurrencyException()
{
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
    var state = new InMemoryPersistenceState(clock);
    var store = new InMemoryTimeoutStore(state, clock);

    var id = Guid.NewGuid();
    await store.InsertTimeoutAsync(new TimeoutData
    {
        Id = id,
        Destination = "dest",
        ProcessManagerId = Guid.NewGuid(),
        Time = clock.GetUtcNow(),
        Headers = new Dictionary<string, object>(StringComparer.Ordinal),
    });

    var batch = await store.GetTimeoutsBatchAsync();
    var owner = batch.DueTimeouts[0].LockedBy;

    clock.Advance(TimeSpan.FromMinutes(6));

    await Assert.ThrowsAsync<ConcurrencyException>(() =>
        store.ReleaseDispatchedTimeoutAsync(id, owner));
}
```

If existing test helpers like `InsertAndLeaseAsync` already exist in this file, use them instead of inlining.

- [ ] **Step 3: Run the tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryTimeoutStoreLeaseTests" -m:1
```

Expected: 2 new FAIL — Remove/Release succeed silently with expired lease.

- [ ] **Step 4: Apply the H9 fix to `RemoveDispatchedTimeoutAsync`**

In `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs`:

```csharp
// before
if (lockOwner is { } owner)
{
    if (!found || !entry!.Data.Locked || entry.Data.LockedBy != owner)
    {
        throw new ConcurrencyException(
            $"Lease for timeout '{id}' was invalidated; lock owner '{owner}' no longer holds the lease.");
    }
}

// after
if (lockOwner is { } owner)
{
    var utcNow = _timeProvider.GetUtcNow();
    if (!found
        || !entry!.Data.Locked
        || entry.Data.LockedBy != owner
        || entry.Data.LockExpiresAt is null
        || entry.Data.LockExpiresAt <= utcNow)
    {
        throw new ConcurrencyException(
            $"Lease for timeout '{id}' was invalidated; lock owner '{owner}' no longer holds the lease.");
    }
}
```

- [ ] **Step 5: Apply the H9 fix to `ReleaseDispatchedTimeoutAsync`**

Same shape — duplicate the block above into the `Release` method's lease branch.

- [ ] **Step 6: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryTimeoutStore" -m:1
```

Expected: all pass — both new + existing tests.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreLeaseTests.cs \
        src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs
git commit -m "fix(inmemory-timeout): reject expired leases (H9 — parity with Mongo)"
```

(Co-Authored-By trailer.)

---

## Task 5: Smaller — `InMemoryPersistenceOptions` (breaking)

**Files:**
- Create: `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceOptions.cs`.
- Modify: `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs` (replace ctors).
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs` (DI registration).
- Create: `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreOptionsTests.cs`.
- Modify: `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreLeaseTests.cs` (ctor calls update).
- Modify: `src/ServiceConnect.UnitTests/InMemoryTimeoutStoreConcurrencyTests.cs` (ctor calls update).

**Background.** Mirror Mongo's options shape so InMemory's lease duration is configurable. Replace the legacy `(string="", string="", TimeProvider?=null)` public ctor (also breaks the legacy internal `(state, TimeProvider?)` signature). Major-version breaking change.

- [ ] **Step 1: Create the new options class**

```csharp
// src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceOptions.cs
namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Configuration for the in-memory persistence stores.
/// </summary>
public sealed class InMemoryPersistenceOptions
{
    /// <summary>
    /// Lease duration applied when claiming a timeout for dispatch via
    /// <see cref="ServiceConnect.Interfaces.ITimeoutStore.GetTimeoutsBatchAsync"/>.
    /// Mirrors <see cref="MongoDbPersistenceOptions.TimeoutLockLeaseDuration"/>. Must be positive.
    /// </summary>
    public TimeSpan LockLeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
}
```

- [ ] **Step 2: Replace `InMemoryTimeoutStore` ctors and remove the hard-coded constant**

In `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs`:

```csharp
// before
public sealed class InMemoryTimeoutStore : ITimeoutStore
{
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryPersistenceState _state;

    private static readonly TimeSpan LockLeaseDuration = TimeSpan.FromMinutes(5);

    public InMemoryTimeoutStore(string connectionString = "", string databaseName = "", TimeProvider? timeProvider = null)
        : this(new InMemoryPersistenceState(timeProvider), timeProvider) { }

    internal InMemoryTimeoutStore(InMemoryPersistenceState state, TimeProvider? timeProvider = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

// after
public sealed class InMemoryTimeoutStore : ITimeoutStore
{
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryPersistenceState _state;
    private readonly TimeSpan _lockLeaseDuration;

    public InMemoryTimeoutStore(InMemoryPersistenceOptions options, TimeProvider? timeProvider = null)
        : this(options, new InMemoryPersistenceState(timeProvider), timeProvider) { }

    internal InMemoryTimeoutStore(
        InMemoryPersistenceOptions options,
        InMemoryPersistenceState state,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.LockLeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LockLeaseDuration,
                $"{nameof(InMemoryPersistenceOptions.LockLeaseDuration)} must be positive.");
        }
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lockLeaseDuration = options.LockLeaseDuration;
    }
```

Then update the body to use `_lockLeaseDuration` instead of `LockLeaseDuration`:

```csharp
// In GetTimeoutsBatchAsync, around the in-place mutation
entry.Data.LockExpiresAt = utcNow + _lockLeaseDuration;
```

- [ ] **Step 3: Update DI registration**

In `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs`, change the `InMemoryTimeoutStore` registration:

```csharp
// before
services.TryAddSingleton<InMemoryTimeoutStore>(sp =>
    new InMemoryTimeoutStore(
        sp.GetRequiredService<InMemoryPersistenceState>(),
        sp.GetRequiredService<TimeProvider>()));

// after
services.TryAddSingleton<InMemoryPersistenceOptions>();
services.TryAddSingleton<InMemoryTimeoutStore>(sp =>
    new InMemoryTimeoutStore(
        sp.GetRequiredService<InMemoryPersistenceOptions>(),
        sp.GetRequiredService<InMemoryPersistenceState>(),
        sp.GetRequiredService<TimeProvider>()));
```

`TryAddSingleton<InMemoryPersistenceOptions>()` registers a default-constructed instance (`LockLeaseDuration = TimeSpan.FromMinutes(5)`). Callers can override by registering their own `InMemoryPersistenceOptions` before calling `UseInMemoryPersistence`.

- [ ] **Step 4: Update existing test ctor calls**

In `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreLeaseTests.cs` and `src/ServiceConnect.UnitTests/InMemoryTimeoutStoreConcurrencyTests.cs`, replace every call:

```csharp
// before
new InMemoryTimeoutStore(state, clock)

// after
new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), state, clock)
```

Search:

```bash
grep -rn "new InMemoryTimeoutStore" src/ServiceConnect.UnitTests/
```

Update every match. Add a `using ServiceConnect.Persistence.InMemory;` if missing.

- [ ] **Step 5: Write the new options-validation tests**

Create `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryTimeoutStoreOptionsTests
{
    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new InMemoryTimeoutStore(options: null!));
    }

    [Theory]
    [InlineData(0)]      // TimeSpan.Zero
    [InlineData(-1000)]  // negative
    public void Constructor_NonPositiveLeaseDuration_ThrowsArgumentOutOfRange(long ticks)
    {
        var options = new InMemoryPersistenceOptions
        {
            LockLeaseDuration = TimeSpan.FromTicks(ticks),
        };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InMemoryTimeoutStore(options));
    }

    [Fact]
    public async Task LockLeaseDuration_HonoursOptions()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var options = new InMemoryPersistenceOptions
        {
            LockLeaseDuration = TimeSpan.FromSeconds(30),
        };
        var state = new InMemoryPersistenceState(clock);
        var store = new InMemoryTimeoutStore(options, state, clock);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = id,
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = clock.GetUtcNow(),
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });

        // First poll: row leased.
        var first = await store.GetTimeoutsBatchAsync();
        Assert.Single(first.DueTimeouts);

        // Advance just-under the configured 30s lease — second poll returns nothing
        // because the row is still held.
        clock.Advance(TimeSpan.FromSeconds(29));
        var second = await store.GetTimeoutsBatchAsync();
        Assert.Empty(second.DueTimeouts);

        // Advance past the lease — third poll returns the row again because the
        // due-filter's expired-lease branch picks it up.
        clock.Advance(TimeSpan.FromSeconds(2));
        var third = await store.GetTimeoutsBatchAsync();
        Assert.Single(third.DueTimeouts);
    }
}
```

- [ ] **Step 6: Build and run the focused tests**

```bash
dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj -m:1
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryTimeoutStore" -m:1
```

Expected: build clean (0 errors, 0 warnings); all tests pass — new options validation, existing lease tests (re-shaped), existing concurrency tests (re-shaped).

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceOptions.cs \
        src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs \
        src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs \
        src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreOptionsTests.cs \
        src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreLeaseTests.cs \
        src/ServiceConnect.UnitTests/InMemoryTimeoutStoreConcurrencyTests.cs
git commit -m "feat(inmemory)!: replace ctor with InMemoryPersistenceOptions"
```

(Note the `!` marker in the commit subject indicating a breaking change. Co-Authored-By trailer.)

---

## Task 6: H31 — Mongo index migration

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs` (`EnsureTimeoutIndexAsync` at ~line 315).
- Create: `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreIndexMigrationTests.cs`.

**Background.** Add `(Time, Locked, LockExpiresAt)` composite covering both branches of the OR-shaped due filter; drop the legacy `(Locked, Time)` index (auto-name `Locked_1_Time_1`). Idempotent over `MongoCommandException` code 27 (`IndexNotFound`).

- [ ] **Step 1: Read the current `EnsureTimeoutIndexAsync` shape**

```bash
sed -n '315,360p' src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs
```

Three index models exist today: `(Locked, Time)`, `(LockedBy, Locked)`, `(LockExpiresAt)`. The first will be replaced with `(Time, Locked, LockExpiresAt)` plus a drop of the old.

- [ ] **Step 2: Write the failing tests**

Create `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreIndexMigrationTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbTimeoutStoreIndexMigrationTests
{
    static MongoDbTimeoutStoreIndexMigrationTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    private static string RenderKeys(IndexKeysDefinition<TimeoutData> keys) =>
        keys.Render(
            BsonSerializer.LookupSerializer<TimeoutData>(),
            BsonSerializer.SerializerRegistry).ToJson();

    private static (MongoDbTimeoutStore Store, Mock<IMongoIndexManager<TimeoutData>> Indexes)
        BuildStore(
            Action<Mock<IMongoIndexManager<TimeoutData>>>? indexSetup = null)
    {
        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
        indexSetup?.Invoke(indexes);

        // Default: CreateMany succeeds.
        indexes.Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["ok"]);
        // Default: DropOne succeeds.
        indexes.Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BsonDocument { ["ok"] = 1 });

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);
        collection.Setup(c => c.InsertOneAsync(
                It.IsAny<TimeoutData>(),
                It.IsAny<InsertOneOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null))
            .Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        var store = new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            NullLogger<MongoDbTimeoutStore>.Instance);

        return (store, indexes);
    }

    [Fact]
    public async Task EnsureTimeoutIndex_CreatesTimeLockedLockExpiresAtComposite()
    {
        IEnumerable<CreateIndexModel<TimeoutData>>? captured = null;
        var (store, _) = BuildStore(indexes =>
        {
            indexes.Setup(m => m.CreateManyAsync(
                    It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<CreateIndexModel<TimeoutData>>, CancellationToken>((m, _) => captured = m.ToList())
                .ReturnsAsync(["ok"]);
        });

        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = DateTimeOffset.UtcNow,
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });

        Assert.NotNull(captured);
        var keysJsonList = captured!.Select(m => RenderKeys(m.Keys)).ToList();
        Assert.Contains(keysJsonList, k =>
            k.Contains("\"Time\" : 1") && k.Contains("\"Locked\" : 1") && k.Contains("\"LockExpiresAt\" : 1"));
    }

    [Fact]
    public async Task EnsureTimeoutIndex_DropsLegacyLockedTimeIndex()
    {
        var (store, indexes) = BuildStore();

        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = DateTimeOffset.UtcNow,
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });

        indexes.Verify(
            m => m.DropOneAsync("Locked_1_Time_1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EnsureTimeoutIndex_SwallowsIndexNotFoundOnDrop()
    {
        var (store, _) = BuildStore(indexes =>
        {
            // Code 27 = IndexNotFound.
            var connectionId = new MongoDB.Driver.Core.Connections.ConnectionId(
                new MongoDB.Driver.Core.Servers.ServerId(
                    new MongoDB.Driver.Core.Clusters.ClusterId(),
                    new System.Net.DnsEndPoint("localhost", 27017)));
            var result = new BsonDocument { ["ok"] = 0, ["code"] = 27, ["errmsg"] = "index not found" };
            var command = new BsonDocument { ["dropIndexes"] = "Timeouts" };
            indexes.Setup(m => m.DropOneAsync("Locked_1_Time_1", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new MongoCommandException(connectionId, "index not found", command, result));
        });

        // Should NOT throw.
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = DateTimeOffset.UtcNow,
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });
    }

    [Fact]
    public async Task EnsureTimeoutIndex_PropagatesOtherDropErrors()
    {
        var (store, _) = BuildStore(indexes =>
        {
            // Code 13 = Unauthorized.
            var connectionId = new MongoDB.Driver.Core.Connections.ConnectionId(
                new MongoDB.Driver.Core.Servers.ServerId(
                    new MongoDB.Driver.Core.Clusters.ClusterId(),
                    new System.Net.DnsEndPoint("localhost", 27017)));
            var result = new BsonDocument { ["ok"] = 0, ["code"] = 13, ["errmsg"] = "unauthorized" };
            var command = new BsonDocument { ["dropIndexes"] = "Timeouts" };
            indexes.Setup(m => m.DropOneAsync("Locked_1_Time_1", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new MongoCommandException(connectionId, "unauthorized", command, result));
        });

        await Assert.ThrowsAsync<PersistenceException>(() =>
            store.InsertTimeoutAsync(new TimeoutData
            {
                Id = Guid.NewGuid(),
                Destination = "dest",
                ProcessManagerId = Guid.NewGuid(),
                Time = DateTimeOffset.UtcNow,
                Headers = new Dictionary<string, object>(StringComparer.Ordinal),
            }));
    }
}
```

- [ ] **Step 3: Run the tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStoreIndexMigrationTests" -m:1
```

Expected: 4 FAIL — `(Time, Locked, LockExpiresAt)` not created, `DropOneAsync` not called.

- [ ] **Step 4: Apply the H31 fix to `EnsureTimeoutIndexAsync`**

In `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs`:

```csharp
// before
private async Task EnsureTimeoutIndexAsync(IMongoCollection<TimeoutData> collection, CancellationToken cancellationToken)
{
    try
    {
        var lockedTimeIndexModel = new CreateIndexModel<TimeoutData>(
            Builders<TimeoutData>.IndexKeys
                .Ascending(x => x.Locked)
                .Ascending(x => x.Time));

        var lockedByIndexModel = new CreateIndexModel<TimeoutData>(
            Builders<TimeoutData>.IndexKeys
                .Ascending(x => x.LockedBy)
                .Ascending(x => x.Locked));

        var lockExpiresAtIndexModel = new CreateIndexModel<TimeoutData>(
            Builders<TimeoutData>.IndexKeys.Ascending(x => x.LockExpiresAt));

        await collection.Indexes.CreateManyAsync(
            [lockedTimeIndexModel, lockedByIndexModel, lockExpiresAtIndexModel],
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
    }
    catch (MongoCommandException ex) when (ex.Code is 85 or 86)
    {
        // 85 IndexOptionsConflict / 86 IndexKeySpecsConflict — concurrent creation race.
    }
}

// after
private async Task EnsureTimeoutIndexAsync(IMongoCollection<TimeoutData> collection, CancellationToken cancellationToken)
{
    // Drop the legacy (Locked, Time) index from prior versions. v8 uses
    // (Time, Locked, LockExpiresAt) which covers both branches of the OR-shaped
    // due filter and the Time-prefix sort. Idempotent over IndexNotFound (code 27)
    // so fresh databases and re-runs are no-ops.
    try
    {
        await collection.Indexes.DropOneAsync("Locked_1_Time_1", cancellationToken).ConfigureAwait(false);
    }
    catch (MongoCommandException ex) when (ex.Code == 27)
    {
        // IndexNotFound — already dropped, or never existed.
    }

    try
    {
        var dueQueryIndexModel = new CreateIndexModel<TimeoutData>(
            Builders<TimeoutData>.IndexKeys
                .Ascending(x => x.Time)
                .Ascending(x => x.Locked)
                .Ascending(x => x.LockExpiresAt));

        var lockedByIndexModel = new CreateIndexModel<TimeoutData>(
            Builders<TimeoutData>.IndexKeys
                .Ascending(x => x.LockedBy)
                .Ascending(x => x.Locked));

        var lockExpiresAtIndexModel = new CreateIndexModel<TimeoutData>(
            Builders<TimeoutData>.IndexKeys.Ascending(x => x.LockExpiresAt));

        await collection.Indexes.CreateManyAsync(
            [dueQueryIndexModel, lockedByIndexModel, lockExpiresAtIndexModel],
            cancellationToken: cancellationToken
        ).ConfigureAwait(false);
    }
    catch (MongoCommandException ex) when (ex.Code is 85 or 86)
    {
        // 85 IndexOptionsConflict / 86 IndexKeySpecsConflict — concurrent creation race.
    }
}
```

- [ ] **Step 5: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStore" -m:1
```

Expected: all pass — new migration tests + existing tests (the existing `BuildStore`/`BuildStoreWithIndexException` helpers in `MongoDbTimeoutStoreTests.cs` already mock `Indexes` so `DropOneAsync` returning a benign `BsonDocument` from the default Moq returns won't cause failures; if a pre-existing test fails because it didn't set up `DropOneAsync`, add the default setup to its mock).

If a pre-existing test breaks because its mock didn't anticipate `DropOneAsync`, the fix is a one-line addition to its `Indexes` mock setup:

```csharp
indexes.Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
    .ReturnsAsync(new BsonDocument { ["ok"] = 1 });
```

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/MongoDbTimeoutStoreIndexMigrationTests.cs \
        src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs \
        <any pre-existing-test files needing the DropOneAsync default>
git commit -m "perf(mongo-timeout)!: index migration to (Time,Locked,LockExpiresAt) (H31)"
```

(The `!` marker reflects the index-shape change — existing v7 deployments will issue `DropOneAsync` on first startup. Co-Authored-By trailer.)

---

## Task 7: M33 — Broaden `StartSessionAsync` fallback

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs` (`GetTimeoutsBatchAsync` at ~lines 118-128).
- Create: `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreSessionFallbackTests.cs`.

**Background.** Today the fallback only catches `NotSupportedException`. Other exception types from the driver (e.g., `MongoConfigurationException`, transient `MongoConnectionException`) bubble out and fail the whole poll. Broaden to `MongoException` and log a Warning. `OperationCanceledException` is NOT caught (Phase 3 discipline).

- [ ] **Step 1: Read the current fallback shape**

```bash
sed -n '118,130p' src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs
```

- [ ] **Step 2: Write the failing test**

Create `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreSessionFallbackTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbTimeoutStoreSessionFallbackTests
{
    static MongoDbTimeoutStoreSessionFallbackTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    [Fact]
    public async Task GetTimeoutsBatch_StartSessionThrowsMongoConfiguration_FallsBackAndLogs()
    {
        // Logger-capture pattern matching project canon: Mock<ILogger<T>> + IsEnabled(true) +
        // InvocationAction + DynamicInvoke.
        var logEntries = new List<(LogLevel Level, string Message, Exception? Exception)>();
        var logger = new Mock<ILogger<MongoDbTimeoutStore>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var state = invocation.Arguments[2];
                var exception = (Exception?)invocation.Arguments[3];
                var formatter = invocation.Arguments[4];
                var message = (string)formatter.GetType().GetMethod("Invoke")!.Invoke(formatter, new[] { state, exception })!;
                logEntries.Add((level, message, exception));
            }));

        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
        indexes.Setup(m => m.CreateManyAsync(It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["ok"]);
        indexes.Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BsonDocument { ["ok"] = 1 });

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);

        // UpdateMany without session (the fallback path) is exercised — set it up to
        // succeed with zero matches so the FindAsync candidate query also fires.
        collection.Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateResult.Acknowledged(0, 0, null));

        var emptyGuidCursor = new Mock<IAsyncCursor<Guid>>();
        emptyGuidCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        emptyGuidCursor.SetupGet(c => c.Current).Returns(Array.Empty<Guid>());
        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyGuidCursor.Object);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null)).Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);
        // Throw a non-NotSupportedException MongoException to verify the broadened fallback.
        client.Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MongoConfigurationException("test cluster is misconfigured"));

        var store = new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            logger.Object);

        // Should not throw — falls through to the unsessioned UpdateMany/FindAsync path.
        var batch = await store.GetTimeoutsBatchAsync();

        Assert.Empty(batch.DueTimeouts);
        Assert.Contains(logEntries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("session") &&
            e.Exception is MongoConfigurationException);
    }
}
```

- [ ] **Step 3: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStoreSessionFallbackTests" -m:1
```

Expected: FAIL — `MongoConfigurationException` propagates as `PersistenceException` (the outer `MongoException` catch wraps it).

- [ ] **Step 4: Apply the M33 fix**

In `GetTimeoutsBatchAsync`, change:

```csharp
// before
catch (NotSupportedException)
{
    // Standalone mongods / older servers don't support sessions; fall back.
}

// after
catch (NotSupportedException)
{
    // Standalone mongods / older servers don't support sessions; fall back.
}
catch (MongoException ex)
{
    // Configuration/transient driver errors during session establishment shouldn't
    // fail the whole poll — fall back to the unsessioned path. The fallback is
    // less safe under primary failover (read-after-write lag) but better than zero.
    logger.LogWarning(ex, "MongoDB session establishment failed; falling back to unsessioned poll.");
}
```

The `logger` parameter is the constructor-injected `ILogger<MongoDbTimeoutStore>`. The class field name in current code is the constructor parameter directly (no field promotion via primary constructor). Confirm the actual identifier (likely `_logger` if rewritten with a field, or a closure on the ctor param). Match what's there.

- [ ] **Step 5: Run the test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStore" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.UnitTests/MongoDbTimeoutStoreSessionFallbackTests.cs \
        src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs
git commit -m "fix(mongo-timeout): broaden session fallback to MongoException (M33)"
```

(Co-Authored-By trailer.)

---

## Task 8: M35 — Candidate sort tie-breaker

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs` (the candidate-id sort at ~line 142).
- Create: `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreSortTieBreakerTests.cs`.

**Background.** Sorting candidate ids by `Time` ascending alone leaves ties undefined. Add `Id` as the tie-breaker so timeouts with identical `Time` are eventually all picked up.

- [ ] **Step 1: Write the failing test**

Create `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreSortTieBreakerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbTimeoutStoreSortTieBreakerTests
{
    static MongoDbTimeoutStoreSortTieBreakerTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    [Fact]
    public async Task GetTimeoutsBatch_CandidateSort_IncludesIdTieBreaker()
    {
        FindOptions<TimeoutData, Guid>? capturedOptions = null;

        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
        indexes.Setup(m => m.CreateManyAsync(It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["ok"]);
        indexes.Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BsonDocument { ["ok"] = 1 });

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);

        var emptyCursor = new Mock<IAsyncCursor<Guid>>();
        emptyCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        emptyCursor.SetupGet(c => c.Current).Returns(Array.Empty<Guid>());
        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, Guid>>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, FindOptions<TimeoutData, Guid>, CancellationToken>(
                (_, opts, _) => capturedOptions = opts)
            .ReturnsAsync(emptyCursor.Object);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null)).Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);
        // No session — keeps the test simple; the sort shape is the same with or without.
        client.Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("test"));

        var store = new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            NullLogger<MongoDbTimeoutStore>.Instance);

        await store.GetTimeoutsBatchAsync();

        Assert.NotNull(capturedOptions);
        Assert.NotNull(capturedOptions!.Sort);
        var sortJson = capturedOptions.Sort.Render(
            BsonSerializer.LookupSerializer<TimeoutData>(),
            BsonSerializer.SerializerRegistry).ToJson();
        Assert.Contains("\"Time\" : 1", sortJson);
        Assert.Contains("\"Id\" : 1", sortJson);
    }
}
```

- [ ] **Step 2: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStoreSortTieBreakerTests" -m:1
```

Expected: FAIL — sort doesn't contain `"Id" : 1`.

- [ ] **Step 3: Apply the M35 fix**

In `GetTimeoutsBatchAsync`, change the `FindAsync` candidate sort:

```csharp
// before
var candidateIds = await FindAsync(collection, dueUnlockedFilter,
        Builders<TimeoutData>.Sort.Ascending(x => x.Time),
        batchSize ?? _batchSize, session, cancellationToken)
    .ConfigureAwait(false);

// after
var candidateIds = await FindAsync(collection, dueUnlockedFilter,
        Builders<TimeoutData>.Sort.Ascending(x => x.Time).Ascending(x => x.Id),
        batchSize ?? _batchSize, session, cancellationToken)
    .ConfigureAwait(false);
```

- [ ] **Step 4: Run the test to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStore" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/MongoDbTimeoutStoreSortTieBreakerTests.cs \
        src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs
git commit -m "fix(mongo-timeout): tie-break candidate sort by Id (M35)"
```

(Co-Authored-By trailer.)

---

## Task 9: M34 — Best-effort lease release on cancellation

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs` (`GetTimeoutsBatchAsync` UpdateMany→FindAsync window).
- Create: `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreCancelOrphanTests.cs`.

**Background.** When OCE fires AFTER `UpdateMany` succeeds but BEFORE `FindAsync` runs, the lease is held by a sessionId no caller will use. Best-effort release: in a `catch (OperationCanceledException)` block inside the try, run a release UpdateMany scoped to the sessionId using `CancellationToken.None`. Swallow exceptions from the release itself; reaper / lease-expiry is the ultimate recovery.

- [ ] **Step 1: Write the failing tests**

Create `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreCancelOrphanTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbTimeoutStoreCancelOrphanTests
{
    static MongoDbTimeoutStoreCancelOrphanTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    private static (MongoDbTimeoutStore Store, Mock<IMongoCollection<TimeoutData>> Collection, Mock<ILogger<MongoDbTimeoutStore>> Logger)
        BuildStore()
    {
        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
        indexes.Setup(m => m.CreateManyAsync(It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["ok"]);
        indexes.Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BsonDocument { ["ok"] = 1 });

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);

        var oneIdCursor = new Mock<IAsyncCursor<Guid>>();
        var seq = oneIdCursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()));
        seq.ReturnsAsync(true).ReturnsAsync(false);
        oneIdCursor.SetupGet(c => c.Current).Returns(new[] { Guid.NewGuid() });
        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(oneIdCursor.Object);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null)).Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);
        client.Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("standalone"));

        var logger = new Mock<ILogger<MongoDbTimeoutStore>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var store = new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            logger.Object);

        return (store, collection, logger);
    }

    [Fact]
    public async Task GetTimeoutsBatch_CancelAfterUpdateMany_ReleasesLeaseBestEffort()
    {
        var (store, collection, _) = BuildStore();

        var updateManyCalls = 0;
        var releaseFilterCaptured = false;
        collection.Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, UpdateDefinition<TimeoutData>, UpdateOptions, CancellationToken>(
                (filter, _, _, ct) =>
                {
                    updateManyCalls++;
                    if (updateManyCalls == 2)
                    {
                        // Release call should use CancellationToken.None.
                        Assert.Equal(CancellationToken.None, ct);
                        releaseFilterCaptured = true;
                    }
                })
            .ReturnsAsync(new UpdateResult.Acknowledged(0, 0, null));

        // Wire FindAsync<TimeoutData> (the read-back) to throw OCE.
        var emptyTimeoutCursor = new Mock<IAsyncCursor<TimeoutData>>();
        emptyTimeoutCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        emptyTimeoutCursor.SetupGet(c => c.Current).Returns(Array.Empty<TimeoutData>());
        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, TimeoutData>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("simulated"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.GetTimeoutsBatchAsync());

        Assert.Equal(2, updateManyCalls); // claim + release
        Assert.True(releaseFilterCaptured);
    }

    [Fact]
    public async Task GetTimeoutsBatch_CancelBeforeUpdateMany_DoesNotAttemptRelease()
    {
        var (store, collection, _) = BuildStore();

        var updateManyCalls = 0;
        collection.Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => updateManyCalls++)
            .ThrowsAsync(new OperationCanceledException("simulated"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.GetTimeoutsBatchAsync());

        Assert.Equal(1, updateManyCalls); // only the failed claim attempt
    }

    [Fact]
    public async Task GetTimeoutsBatch_CancelDuringBestEffortRelease_SwallowsAndPropagatesOriginalOce()
    {
        var (store, collection, logger) = BuildStore();

        var updateManyCalls = 0;
        collection.Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => updateManyCalls++)
            .Returns<FilterDefinition<TimeoutData>, UpdateDefinition<TimeoutData>, UpdateOptions, CancellationToken>(
                (_, _, _, _) =>
                {
                    if (updateManyCalls == 1)
                    {
                        return Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(0, 0, null));
                    }
                    // Release fails too — should be swallowed and logged.
                    throw new MongoException("simulated release failure");
                });

        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, TimeoutData>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("simulated"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.GetTimeoutsBatchAsync());

        Assert.Equal(2, updateManyCalls);
        // Verify a Warning was logged (the release failure).
        logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<MongoException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
```

- [ ] **Step 2: Run the tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStoreCancelOrphanTests" -m:1
```

Expected: at least one FAIL — `updateManyCalls == 1` instead of 2 in the after-claim test (no release attempted).

- [ ] **Step 3: Apply the M34 fix**

In `GetTimeoutsBatchAsync`, restructure the UpdateMany→FindAsync window:

```csharp
// Inside the existing try { ... } block, wrap the UpdateMany + FindAsync in a nested
// try/catch that handles OperationCanceledException specifically:

bool leaseClaimed = false;
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

    // Read back exactly the rows we just claimed (LockedBy == sessionId).
    var ownedFilter = Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, sessionId)
                    & Builders<TimeoutData>.Filter.Eq(x => x.Locked, true)
                    & Builders<TimeoutData>.Filter.Gt(x => x.LockExpiresAt, utcNow);  // H8 predicate
    using var cursor = session is not null
        ? await collection.FindAsync(session, ownedFilter, cancellationToken: cancellationToken).ConfigureAwait(false)
        : await collection.FindAsync(ownedFilter, cancellationToken: cancellationToken).ConfigureAwait(false);
    await cursor.ForEachAsync(retval.DueTimeouts.Add, cancellationToken).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    if (leaseClaimed)
    {
        try
        {
            // Best-effort release of rows we claimed for a session no caller will use.
            // CancellationToken.None: don't let the cancelling token preempt our cleanup.
            // Reaper / lease-expiry is the ultimate recovery if this also fails.
            var releaseFilter = Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, sessionId)
                              & Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);
            var releaseUpdate = Builders<TimeoutData>.Update
                .Set(x => x.Locked, false)
                .Set(x => x.LockedBy, Guid.Empty)
                .Set(x => x.LockExpiresAt, null);
            await collection.UpdateManyAsync(releaseFilter, releaseUpdate, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort lease release after cancellation failed for session {SessionId}; reaper will reclaim.", sessionId);
        }
    }
    throw;
}

return retval;
```

NOTE: the H8 predicate (`Gt(LockExpiresAt, utcNow)`) is folded into this step because the read-back filter sits inside the new try/catch shape. If H8 already landed in Task 3, leave the existing predicate in place — the structural change in this task is the OCE catch around UpdateMany + FindAsync.

The outer `catch (MongoException ex) { throw new PersistenceException(...); }` and `finally { session?.Dispose(); }` stay unchanged. The OCE catch is INSIDE the outer try so cancellation isn't accidentally wrapped in `PersistenceException`.

- [ ] **Step 4: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStore" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/MongoDbTimeoutStoreCancelOrphanTests.cs \
        src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs
git commit -m "fix(mongo-timeout): best-effort lease release on cancellation (M34)"
```

(Co-Authored-By trailer.)

---

## Task 10: Smaller — InMemory `Guid.Empty` rejection + `SortedSet.Remove` assert + in-place mutation comment

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs`.
- Create: `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreGuidEmptyTests.cs`.

**Background.** Three small parity / hygiene items batched into one commit.

- [ ] **Step 1: Write the failing test for `Guid.Empty` rejection**

Create `src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreGuidEmptyTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryTimeoutStoreGuidEmptyTests
{
    [Fact]
    public async Task InsertTimeout_GuidEmpty_ThrowsArgumentException()
    {
        var clock = new FakeTimeProvider();
        var state = new InMemoryPersistenceState(clock);
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), state, clock);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.InsertTimeoutAsync(new TimeoutData
            {
                Id = Guid.Empty,
                Destination = "dest",
                ProcessManagerId = Guid.NewGuid(),
                Time = clock.GetUtcNow(),
                Headers = new Dictionary<string, object>(StringComparer.Ordinal),
            }));
    }

    [Fact]
    public async Task InsertTimeout_ValidGuid_Succeeds()
    {
        var clock = new FakeTimeProvider();
        var state = new InMemoryPersistenceState(clock);
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), state, clock);

        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = clock.GetUtcNow(),
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });
        // No exception → success.
    }
}
```

- [ ] **Step 2: Run the tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryTimeoutStoreGuidEmptyTests" -m:1
```

Expected: `InsertTimeout_GuidEmpty_ThrowsArgumentException` FAILS — InMemory currently accepts `Guid.Empty`.

- [ ] **Step 3: Apply the smaller fixes**

In `src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs`:

**Fix 1 — `Guid.Empty` rejection in `InsertTimeoutAsync`** (parity with Mongo at line ~78):

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

**Fix 2 — `SortedSet.Remove` assert** in `RemoveDispatchedTimeoutAsync`:

```csharp
// before
_state.TimeoutsById.Remove(id);
_state.TimeoutIndex.Remove(entry!);

// after
_state.TimeoutsById.Remove(id);
var removed = _state.TimeoutIndex.Remove(entry!);
Debug.Assert(removed, "TimeoutIndex.Remove returned false; comparer drift between insert and remove.");
```

Add `using System.Diagnostics;` at the top of the file if not already present.

**Fix 3 — In-place mutation comment** in `GetTimeoutsBatchAsync`. Find the block:

```csharp
if (!entry.Data.Locked || entry.Data.LockExpiresAt <= utcNow)
{
    entry.Data.Locked = true;
    entry.Data.LockedBy = sessionId;
    entry.Data.LockExpiresAt = utcNow + _lockLeaseDuration;
    retval.DueTimeouts.Add(Clone(entry.Data));
    // ...
```

Add a comment immediately above the three `entry.Data.*` mutations:

```csharp
// In-place mutation under the write lock: _state.TimeoutsById and _state.TimeoutIndex
// hold the same TimeoutEntry reference, so the index is the single source of truth.
// The clone in retval.DueTimeouts.Add isolates the caller from subsequent mutations.
entry.Data.Locked = true;
entry.Data.LockedBy = sessionId;
entry.Data.LockExpiresAt = utcNow + _lockLeaseDuration;
```

- [ ] **Step 4: Run the focused tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryTimeoutStore" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/Persistence/InMemoryTimeoutStoreGuidEmptyTests.cs \
        src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs
git commit -m "fix(inmemory-timeout): reject Guid.Empty; assert removal; doc invariant"
```

(Co-Authored-By trailer.)

---

## Task 11: Cross-persistor contract XML doc on `ITimeoutStore`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Persistence/ITimeoutStore.cs`.

**Background.** The interface currently has a brief remark about the lock-owner mechanic. Strengthen it to capture the post-Phase-8 lease-expiry contract that both persistors honour.

- [ ] **Step 1: Read the current XML doc**

```bash
sed -n '1,72p' src/ServiceConnect.Interfaces/Persistence/ITimeoutStore.cs
```

- [ ] **Step 2: Replace the interface-level `<remarks>` block**

In `src/ServiceConnect.Interfaces/Persistence/ITimeoutStore.cs`:

```csharp
// before
/// <summary>
/// Persists scheduled timeout messages for later dispatch.
/// </summary>
/// <remarks>
/// The remove and release operations accept an optional lock owner. When supplied,
/// the operation is lease-checked: implementations throw
/// <see cref="Exceptions.ConcurrencyException"/> when the lease has been reassigned
/// to another worker. When the lock owner is null, the operation is unconditional.
/// </remarks>

// after
/// <summary>
/// Persists scheduled timeout messages for later dispatch.
/// </summary>
/// <remarks>
/// Lease semantics — consistent across all <see cref="ITimeoutStore"/> implementations:
/// <list type="bullet">
/// <item>Remove and release operations accept an optional lock owner. When supplied,
/// the operation is lease-checked.</item>
/// <item>A worker passing a non-null <c>lockOwner</c> must hold an unexpired lease for
/// the row. An expired-but-not-yet-reaped lease is treated as already invalidated.</item>
/// <item>A reaper (or the natural lease-expiry path) wins any race with a worker; the
/// worker observes <see cref="Exceptions.ConcurrencyException"/>.</item>
/// <item>When the lock owner is null, the operation is unconditional and never throws
/// <see cref="Exceptions.ConcurrencyException"/>.</item>
/// </list>
/// </remarks>
```

- [ ] **Step 3: Build to verify clean**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Interfaces/Persistence/ITimeoutStore.cs
git commit -m "docs(itimeoutstore): document cross-persistor lease-expiry contract"
```

(Co-Authored-By trailer.)

---

## Task 12: Phase 8 release notes

**Files:**
- Modify: `website/src/content/docs/releases.mdx`.

- [ ] **Step 1: Locate the prior phase entry**

```bash
grep -n "### Bus, dispatcher\|### Connection-lifecycle\|### Phase\|^## " website/src/content/docs/releases.mdx | head -20
```

Phase 7 was added as `### Bus, dispatcher, and request-reply correctness`. Place Phase 8 immediately after, before any later entry.

- [ ] **Step 2: Insert the Phase 8 section**

Add the following section (match heading-style of prior phases, no emojis):

```mdx
### Mongo timeout-store + lease guards

**Bug fixes**

- **Lease-expiry semantics restored across both persistors.** `MongoDbTimeoutStore.RemoveDispatchedTimeoutAsync` and `ReleaseDispatchedTimeoutAsync` now reject calls whose lease has expired, throwing `ConcurrencyException`. `InMemoryTimeoutStore` matches the contract exactly (cross-persistor parity). The owned-row read-back inside `GetTimeoutsBatchAsync` also re-checks the lease before returning rows.
- **Mongo timeout-store indexes match the actual due query.** Added composite index `(Time, Locked, LockExpiresAt)` covering both branches of the OR-shaped due filter. Legacy `(Locked, Time)` index is dropped on first startup of v8 (idempotent over `IndexNotFound`).
- **Mongo session-establishment fallback broadened.** Standalone-mongod / configuration / transient driver errors during `StartSessionAsync` no longer fail the whole poll — the persistor falls back to the unsessioned path with a Warning log.
- **Cancellation between UpdateMany and FindAsync no longer orphans leases.** A best-effort lease release runs in a `catch (OperationCanceledException)` block before propagating the cancellation. Recovery is immediate; reaper / lease-expiry remains the ultimate fallback.
- **Candidate sort tie-breaker.** Timeouts with identical `Time` are now ordered by `Id` ascending, eliminating starvation under heavy bursts.
- **InMemory parity tightening.** `InsertTimeoutAsync` rejects `Guid.Empty` Id (parity with Mongo); a `Debug.Assert` documents the SortedSet-removal invariant; an in-place-mutation comment documents the under-lock single-source-of-truth design.

**Behaviour changes**

- **Mongo index migration on first startup.** Existing v7 deployments upgrading to v8 will issue `DropOneAsync("Locked_1_Time_1")` once on first startup. Idempotent over `IndexNotFound`.
- **`InMemoryPersistenceOptions` (breaking).** `InMemoryTimeoutStore`'s ctor changes to take `InMemoryPersistenceOptions` (and an internal `(options, state, timeProvider)` overload). Callers using `new InMemoryTimeoutStore("", "", clock)` must pass `new InMemoryPersistenceOptions()` instead. The DI registration honours the existing default lease duration (`TimeSpan.FromMinutes(5)`) so most users see no change.
- **Expired-lease tolerance removed.** Workers holding a stale lease that previously got away with `Remove`/`Release` now observe `ConcurrencyException`. This matches the documented contract; callers were silently incorrect under v7.
```

- [ ] **Step 3: Build the website**

```bash
npm --prefix website run build
```

Expected: clean. (Pre-existing warnings about `/404.html` are unrelated.)

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/releases.mdx
git commit -m "docs(website): phase 08 release notes"
```

(Co-Authored-By trailer.)

---

## Task 13: API reference + learn updates

**Files (locate first; then update only what's relevant):**
- `website/src/content/docs/reference/configuration/...` (or wherever `MongoDbPersistenceOptions` is documented today; the new `InMemoryPersistenceOptions` lands alongside).
- `website/src/content/docs/reference/process-managers/...` (timeout dispatch behaviour).
- `website/src/content/docs/learn/operations/...` (timeout / lease section, if one exists).
- `examples/ProcessManager/README.md` (verify InMemory ctor call sites).

- [ ] **Step 1: Locate relevant pages**

```bash
ls website/src/content/docs/reference/configuration/
ls website/src/content/docs/reference/process-managers/ 2>/dev/null || echo "no process-managers folder"
ls website/src/content/docs/learn/operations/
grep -rln "TimeoutLockLeaseDuration\|InMemoryTimeoutStore\|MongoDbTimeoutStore\|TimeoutBatchSize\|new InMemoryTimeoutStore" website/src/content/docs/ examples/ README.md 2>/dev/null | head -20
```

- [ ] **Step 2: Update the configuration reference**

If `website/src/content/docs/reference/configuration/...` documents `MongoDbPersistenceOptions`, add the new `InMemoryPersistenceOptions` alongside it with the same shape:

```mdx
### `InMemoryPersistenceOptions`

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `LockLeaseDuration` | `TimeSpan` | `TimeSpan.FromMinutes(5)` | Lease duration applied when claiming a timeout for dispatch. Mirrors `MongoDbPersistenceOptions.TimeoutLockLeaseDuration`. Must be positive. |

Configure by registering an instance before `UseInMemoryPersistence`:

```csharp
services.AddSingleton(new InMemoryPersistenceOptions { LockLeaseDuration = TimeSpan.FromSeconds(30) });
services.AddServiceConnect(...).UseInMemoryPersistence();
```
```

(Match the documentation page's existing prose / table style.)

- [ ] **Step 3: Update the timeout / lease conceptual page**

If a `learn/operations` page covers timeouts or saga timeouts, add a brief section:

```mdx
### Timeout lease semantics

`ITimeoutStore.RemoveDispatchedTimeoutAsync` and `ReleaseDispatchedTimeoutAsync` accept an optional lock owner. When supplied, the operation is lease-checked: workers holding an expired lease observe `ConcurrencyException` so the row is not double-dispatched. The Mongo and InMemory persistors honour identical semantics — the `LockLeaseDuration` setting (`MongoDbPersistenceOptions.TimeoutLockLeaseDuration` or `InMemoryPersistenceOptions.LockLeaseDuration`) governs how long a worker holds the row before the reaper reclaims it.

Operators running the Mongo persistor should configure `ReapStaleLeasesAsync` on a timer at roughly `LockLeaseDuration / 4` cadence to reclaim crashed-handler rows promptly.
```

- [ ] **Step 4: Verify examples / READMEs**

```bash
grep -rn "new InMemoryTimeoutStore" examples/ README.md 2>/dev/null
```

If any sample uses `new InMemoryTimeoutStore(...)` directly, update to pass `new InMemoryPersistenceOptions()` per Task 5's signature change. If samples only consume the DI-registered instance, no changes needed.

- [ ] **Step 5: Build the website and commit**

```bash
npm --prefix website run build
```

Expected: clean.

```bash
git add website/src/content/docs/ examples/
git commit -m "docs(website): InMemoryPersistenceOptions + timeout lease semantics"
```

(Co-Authored-By trailer.)

---

## Task 14: Final verification gate + code review

This task is mechanical. Runs all verification commands and dispatches the final code reviewer over Phase 8 commits.

- [ ] **Step 1: Per-csproj builds clean**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1
dotnet build src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj -m:1
dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj -m:1
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
```

Expected: all succeed, 0 errors, 0 warnings.

- [ ] **Step 2: Focused unit-test pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj \
    --filter "FullyQualifiedName~Timeout|FullyQualifiedName~Lease|FullyQualifiedName~InMemoryPersistence|FullyQualifiedName~MongoDbPersistence" \
    -m:1
```

Expected: all pass.

- [ ] **Step 3: Astro build**

```bash
npm --prefix website run build
```

Expected: clean.

- [ ] **Step 4: Grep verifications**

```bash
# H7/H8: every Mongo lease-checked filter includes Gt(LockExpiresAt, utcNow)
grep -nE "Eq\(x => x\.LockedBy" src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs
# Each occurrence should be in a block that also calls Gt(x => x.LockExpiresAt, utcNow).

# H9: InMemory lease branches reference LockExpiresAt
grep -nE "LockExpiresAt" src/ServiceConnect.Persistence.InMemory/Timeout/InMemoryTimeoutStore.cs
# Expected: at least 4 references (mutation site, two branch checks, possibly comment).

# H31: index migration in place
grep -n "DropOneAsync\|Locked_1_Time_1\|Time.*Locked.*LockExpiresAt" src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs
# Expected: DropOneAsync call + the new composite index spec.

# Smaller — InMemoryPersistenceOptions present
ls src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceOptions.cs
# Expected: file exists.

# M34 best-effort release shape
grep -n "leaseClaimed\|CancellationToken.None" src/ServiceConnect.Persistence.MongoDb/Timeout/MongoDbTimeoutStore.cs
# Expected: leaseClaimed flag and CancellationToken.None on the release.
```

- [ ] **Step 5: Final code review**

Dispatch `superpowers:code-reviewer` (model: opus) over all Phase 8 commits (from `cc0fa9d1` — the spec commit — through HEAD). Briefing:

```
Review Phase 8 commits (cc0fa9d1..HEAD) against
docs/superpowers/specs/2026-05-02-phase-08-mongo-timeout-store-design.md.

Focus on:
- H7/H8/H9: every lease-checked filter (Mongo Remove/Release/owned-readback,
  InMemory Remove/Release) rejects expired leases. Verify the Gt(LockExpiresAt, utcNow)
  predicate is present and the InMemory branch checks LockExpiresAt is null OR <= utcNow.
- H31: (Locked, Time) dropped via DropOneAsync; (Time, Locked, LockExpiresAt) added.
  Drop is idempotent over IndexNotFound (code 27); other errors propagate.
- M33: session fallback catches MongoException broadly (not just NotSupportedException);
  OCE is NOT caught (Phase 3 discipline preserved); a Warning is logged.
- M34: lease-claimed gate; OCE catch INSIDE the outer try (so MongoException wrapping
  doesn't swallow OCE); release uses CancellationToken.None; release exceptions are
  swallowed and logged.
- M35: candidate sort includes Id ASC after Time ASC.
- InMemoryPersistenceOptions: legacy ctors are GONE; DI registration uses the new shape;
  all test sites updated.
- ITimeoutStore XML doc captures the cross-persistor lease contract.

Flag any test that locks in pre-fix behaviour, any race condition the spec did not
anticipate (especially in the new M34 release path's interaction with the reaper),
and any public-API surface change beyond InMemoryPersistenceOptions.
```

- [ ] **Step 6: Cleanup commit (only if review surfaced issues)**

If Steps 4 or 5 found anything, fix in a follow-up commit:

```bash
git add <only-cleanup-files>
git commit -m "cleanup(phase-08): address final-review findings"
```

If nothing needed, skip — Phase 8 is done.

---

## Phase 8 done

All findings closed. The phase ships:

- H7 + H8 + H9 lease-expiry parity across both persistors.
- H31 index migration to `(Time, Locked, LockExpiresAt)`.
- M33 broadened session fallback.
- M34 best-effort lease release on cancellation.
- M35 candidate sort tie-breaker.
- `InMemoryPersistenceOptions` (breaking) + Guid.Empty rejection + assertion + comment.
- Cross-persistor contract documented on `ITimeoutStore`.
- Phase 8 release notes + reference / learn updates.

Cross-link in plan only: H7's fix narrows but does not close the **M16** double-dispatch window (`ProcessManagerTimeoutService.PollOnceAsync`). M16 itself remains Phase 11.

Move to writing the closing summary; the user's standard pattern is "phase complete; continue to phase N+1?".
