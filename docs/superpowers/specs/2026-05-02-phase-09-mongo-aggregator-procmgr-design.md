# Phase 09 — Mongo aggregator + process-manager + serializer (design)

**Status:** approved 2026-05-02. Source phase doc: [`consolidated-issues/phases/phase-09-mongo-aggregator-procmgr.md`](../../../consolidated-issues/phases/phase-09-mongo-aggregator-procmgr.md).

**Goal:** Resolve the Mongo aggregator + process-manager correctness defects and the Guid-serializer registration bug that quietly diverges read vs. write paths. Several of these are race conditions that surface only under load; together they form the second half of the Mongo persistence audit (Phase 8 covered timeouts).

**Projects affected:**
- `ServiceConnect.Persistence.MongoDb` (aggregator, process-manager, serializer extensions, cert factory)
- `ServiceConnect.UnitTests` (Moq-based filter-shape and structural tests)
- `ServiceConnect.EndToEndTests` (Testcontainers behavioural tests using existing `PersistenceFixture`)
- `ServiceConnect` (potentially: `IProcessManagerTypeRegistry` if no existing saga-type enumeration is reachable)
- `website/src/content/docs/releases.mdx`, `website/src/content/docs/reference/...`

**Branch / starting point:** `v7-clean-architecture`, current HEAD `003fabc6`.

---

## 1. Findings in scope

### High

| ID | Summary | Pointer (verified 2026-05-02) |
|---|---|---|
| H6 | `MongoDbAggregatorPersistor.RemoveDataAsync` filter hard-codes `GuidRepresentation.Standard` while `EnsureGuidSerializerRegistered` swallows registration conflicts | [`MongoDbAggregatorPersistor.cs:177`](../../../src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs), [`MongoDbPersistenceExtensions.cs:88-96`](../../../src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs) |
| H10 | `MongoDbProcessManagerFinder.Expression.Constant` uses runtime type → never matches stored documents for `int`/`long`, `Nullable<T>`, or interface-typed properties | [`MongoDbProcessManagerFinder.cs:117`](../../../src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs) |
| H25 | Lazy `EnsureCorrelationIdIndexAsync` admits cross-process duplicates between cold-start and first I/O | [`MongoDbProcessManagerFinder.cs:296-337`](../../../src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs) |
| H26 | `_concurrencyGuardsEnabled = false` (W:0) silently advances version on missed update — saga gets wedged on the next real conflict | [`MongoDbProcessManagerFinder.cs:67-74, :235, :289`](../../../src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs) |
| H30 | `MongoDbProcessManagerFinder` semaphore never disposed; class is not `IDisposable` | [`MongoDbProcessManagerFinder.cs:20`](../../../src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs) |

### Medium

| ID | Summary | Pointer |
|---|---|---|
| M31 | Aggregator only catches `MongoException`; `BsonSerializationException` escapes the `PersistenceException` contract | [`MongoDbAggregatorPersistor.cs:99, 163, 193, 208, 233, 249`](../../../src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs) |
| M32 | Constructors accept null logger | [`MongoDbAggregatorPersistor.cs:60`](../../../src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs), [`MongoDbProcessManagerFinder.cs:51`](../../../src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs) |
| M36 | Aggregator `InsertedAtTicks` ties resolve via random `Id`, not insertion order | [`MongoDbAggregatorPersistor.cs:96, 124-126`](../../../src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs) |
| M37 | Aggregator `RemoveDataAsync` filter on `DataBson.CorrelationId` masks structural problems as transient `ConcurrencyException` | [`MongoDbAggregatorPersistor.cs:170-197`](../../../src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs) |
| M38 | Aggregator `EnsureIndexesAsync` runs on every operation — round-trip per message on the hot path | [`MongoDbAggregatorPersistor.cs:264-285`](../../../src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs) |

### Low

| ID | Summary | Pointer |
|---|---|---|
| L24 | `MongoClientFactory.ClientCertificateSelectionCallback` dereferences `certificates[0]` without bounds check | [`MongoClientFactory.cs:77`](../../../src/ServiceConnect.Persistence.MongoDb/Configuration/MongoClientFactory.cs) |

### Smaller

| Summary | Pointer |
|---|---|
| Aggregator `count > int.MaxValue` silently truncates (intentional clamp; needs comment) | [`MongoDbAggregatorPersistor.cs:247`](../../../src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs) |
| `RegisterSerializer` swallows `BsonSerializationException` | Subsumed by H6 |
| `typeof(T).FullName` for generics has illegal-for-tooling chars (`+`, backtick, brackets) | [`MongoDbProcessManagerFinder.cs:343`](../../../src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs) |
| Index-creation semaphore shared across collections | **Skip** — current behaviour is fine; per-collection semaphore is an unnecessary refinement |
| `MongoClientFactory.GetOrLoadCertificate` cached cert keeps loading after passphrase rotation | [`MongoClientFactory.cs:89`](../../../src/ServiceConnect.Persistence.MongoDb/Configuration/MongoClientFactory.cs) — **Verification only**: cache key already includes passphrase. Likely already addressed. Confirm during implementation. |

## 2. Out of scope (routed elsewhere)

- **H5** (Mongo dedup persistor builds own `MongoClient`) — Phase 1.
- **Phase 8** owned Mongo timeout-store concerns (shipped).
- **H11** (InMemory `IKeyValueStore` collision with saga store) — Phase 10.

## 3. Design decisions

Six design questions resolved during brainstorming on 2026-05-02:

| # | Question | Decision |
|---|---|---|
| Q1 | Test approach | **B.** Hybrid using existing E2E project. Moq unit tests for filter-shape and structural invariants; E2E behavioural tests using `PersistenceFixture` (Testcontainers MongoDB) for round-trip-sensitive findings. |
| Q2 | H6 fix shape | **A.** `EnsureGuidSerializerRegistered` throws `InvalidOperationException` on `BsonSerializationException` (no swallow). Loud failure at startup; the operator sees what to fix. |
| Q3 | H25 fix shape | **B.** Startup-time index creation via `IHostedService`. Add a saga-type registry if no existing enumeration is reachable. Lazy fallback retained on the I/O path. |
| Q4 | H26 fix shape | **B.** Reject unacknowledged WriteConcern at startup with `InvalidOperationException`. Remove the `_concurrencyGuardsEnabled` field + branches; saga state is correctness-sensitive and w:0 is silently incorrect. |
| Q5 | H30 semaphore lifetime | **B.** Rely on GC; mirror Connection / Producer / Bus pattern from Phases 4 + 6 + 7. |
| Q6 | M36 tie-break shape | **A.** Add monotonic per-process counter via `Interlocked.Increment`; new `InsertSequence` field on `AggregatorDocument`; sort becomes `(InsertedAtTicks, InsertSequence, Id)`. |

## 4. Per-fix behaviour spec

### 4.1 H6 — `EnsureGuidSerializerRegistered` throws on conflict

**File:** `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs`.

Replace the swallow at lines 92-96:

```csharp
// before
try
{
    BsonSerializer.RegisterSerializer(typeof(Guid), new GuidSerializer(GuidRepresentation.Standard));
}
catch (BsonSerializationException)
{
    // Another component registered a different Guid serializer first — respect that,
    // but keep the Standard-representation query literals in our own filters.
}

// after
try
{
    BsonSerializer.RegisterSerializer(typeof(Guid), new GuidSerializer(GuidRepresentation.Standard));
}
catch (BsonSerializationException ex)
{
    throw new InvalidOperationException(
        "ServiceConnect MongoDB persistence requires GuidRepresentation.Standard but another " +
        "component has already registered a different Guid serializer. Configure your driver " +
        "initialisation to either skip Guid serializer registration or register " +
        "GuidRepresentation.Standard before any other component does so.",
        ex);
}
```

The volatile flag's commit point at line 101 stays AFTER this — a thrown `InvalidOperationException` leaves `_guidSerializerRegistered = 0`, so the next caller retries. Combined with the existing V3-mode verification at line 75, the persistor now fails loudly at startup if any incompatible state exists.

The hard-coded `GuidRepresentation.Standard` at `MongoDbAggregatorPersistor.cs:177` stays unchanged — once Q2's contract is enforced, "Standard" is the only valid representation.

### 4.2 H10 — `Expression.Convert` for property-hierarchy queries

**File:** `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs:117`.

```csharp
// before
Expression right = Expression.Constant(msgPropValue, msgPropValue.GetType());

// after
// Coerce the runtime value's type to the declared property type. msgPropValue's
// runtime type can differ from the saga property's declared type (e.g., message
// has int but saga has long, message has T but saga has Nullable<T>, message has
// concrete type but saga has interface). Without the convert, MongoDB filter
// rendering uses the runtime type and the BSON path projection silently misses.
Expression right = Expression.Convert(
    Expression.Constant(msgPropValue, msgPropValue.GetType()),
    left.Type);
```

Mirrors the InMemory equivalent's `Expression.Convert(valueParam, key.PropertyType)` pattern.

### 4.3 H25 — Startup-time index creation via `IHostedService`

**Files:**
- New: `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerIndexInitializer.cs`.
- Possibly new: `src/ServiceConnect.Persistence.MongoDb/ProcessManager/IProcessManagerTypeRegistry.cs` (if no existing enumeration reachable).
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs`.
- Modify: `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs` (expose `EnsureCorrelationIdIndexForTypeAsync(Type, CancellationToken)` that dispatches to the existing generic via cached delegate).

Hosted-service shape:

```csharp
internal sealed class MongoDbProcessManagerIndexInitializer : IHostedService
{
    private readonly MongoDbProcessManagerFinder _finder;
    private readonly IProcessManagerTypeRegistry _registry;
    private readonly ILogger<MongoDbProcessManagerIndexInitializer> _logger;

    public MongoDbProcessManagerIndexInitializer(
        MongoDbProcessManagerFinder finder,
        IProcessManagerTypeRegistry registry,
        ILogger<MongoDbProcessManagerIndexInitializer> logger)
    {
        _finder = finder ?? throw new ArgumentNullException(nameof(finder));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var dataType in _registry.SagaDataTypes)
        {
            try
            {
                await _finder.EnsureCorrelationIdIndexForTypeAsync(dataType, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to pre-create CorrelationId index for {SagaDataType}; lazy fallback will retry on first I/O.",
                    dataType.FullName);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

`MongoDbProcessManagerFinder.EnsureCorrelationIdIndexForTypeAsync` dispatches by reflection (cached delegate, mirroring the existing `InsertDelegateCache` pattern at line 30) to the generic `EnsureCorrelationIdIndexAsync<T>`.

DI registration:

```csharp
services.AddHostedService<MongoDbProcessManagerIndexInitializer>();
```

The lazy fallback at `EnsureCorrelationIdIndexAsync<T>` stays in place — if startup index creation fails (e.g., Mongo not reachable yet), a Warning is logged and lazy creation handles the eventual catch-up. The marker-flip-after-success logic from the existing fix is preserved.

**Saga-type registry investigation (during implementation):** If `ServiceConnectBuilder` already has a way to enumerate registered process-manager data types (DI scan over `IProcessManager<TData>` registrations, or an existing registry), use that. If not, add a small `ProcessManagerTypeRegistry` that records data types as `AddProcessManager<TPm, TData>()` is called.

### 4.4 H26 — Reject `WriteConcern.Unacknowledged` at startup

**File:** `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs`.

Replace the detection-and-warn block at lines 62-74:

```csharp
// before
var effectiveWriteConcern = mongoClient.Settings.WriteConcern;
_concurrencyGuardsEnabled = effectiveWriteConcern.IsAcknowledged;
if (!_concurrencyGuardsEnabled)
{
    _logger.LogWarning(
        "MongoDbProcessManagerFinder: WriteConcern.Unacknowledged (w:0) detected. " +
        "Optimistic-concurrency guards (MatchedCount/DeletedCount checks) are DISABLED. " +
        "Concurrent saga updates will not be detected.");
}

// after
if (!mongoClient.Settings.WriteConcern.IsAcknowledged)
{
    throw new InvalidOperationException(
        "MongoDbProcessManagerFinder requires an acknowledged WriteConcern (w:1 or higher). " +
        "WriteConcern.Unacknowledged (w:0) silently loses concurrent saga updates and " +
        "wedges sagas on the next real conflict because the version field advances. " +
        "Configure mongoClient.Settings.WriteConcern to a value where IsAcknowledged is true.");
}
```

Remove the `_concurrencyGuardsEnabled` field. Remove the `if (_concurrencyGuardsEnabled && ...)` guards in `UpdateDataAsync` (line 235) and `DeleteDataAsync` (line 289) — they now run unconditionally.

### 4.5 H30 — Semaphore under GC

**File:** `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs:20`.

Add a comment block above the field declaration (verbatim adapted from `Connection.cs` / `Bus.cs`):

```csharp
// _indexCreationSemaphore is intentionally NOT Disposed:
// SemaphoreSlim.Dispose only releases the lazily-allocated WaitHandle, and we never call
// AvailableWaitHandle, so disposal is a functional no-op. A concurrent caller's Release()
// on a disposed semaphore would throw ObjectDisposedException out of the unwind path,
// which we cannot prevent without holding GC references to every caller. Mirrors the
// Connection / ProducerConnection / Producer / Bus pattern (Phases 4 + 6 + 7).
private readonly SemaphoreSlim _indexCreationSemaphore = new(1, 1);
```

The class does NOT implement `IDisposable`/`IAsyncDisposable`.

### 4.6 M31 — Catch `BsonException` in aggregator

**File:** `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`.

Each method that catches `MongoException` adds a sibling `catch (BsonException ex)` BEFORE the `MongoException` catch. `BsonSerializationException` derives from `BsonException`, so this catches both. Pattern:

```csharp
// before
catch (MongoException ex)
{
    throw new PersistenceException("Failed to ...", ex);
}

// after
catch (BsonException ex)
{
    throw new PersistenceException("Failed to ...", ex);
}
catch (MongoException ex)
{
    throw new PersistenceException("Failed to ...", ex);
}
```

Apply to `InsertDataAsync`, `GetSnapshotAsync`, `RemoveDataAsync`, `RemoveAllAsync`, `RemoveSnapshotAsync`, `CountAsync`. The single message form is fine (one PersistenceException per method); the `ex` parameter carries the type distinction.

### 4.7 M32 — Null-logger guards

**Files:**
- `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:60`
- `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs:51`

Both ctors gain `ArgumentNullException.ThrowIfNull(logger)` immediately after the existing `ArgumentNullException.ThrowIfNull(mongoClient)` call.

### 4.8 M36 — `InsertSequence` field for tie-break

**File:** `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`.

- Add `private long _insertSequence;` instance field.
- Add `public long InsertSequence { get; set; }` to `AggregatorDocument` (existing documents missing the field deserialize to `0` — same pattern as `InsertedAtTicks` was added in a prior phase).
- In `InsertDataAsync`:

```csharp
await _collection.InsertOneAsync(new AggregatorDocument
{
    Id = Guid.NewGuid(),
    Name = name,
    DataBson = dataBson,
    DataTypeName = dataType.FullName!,
    Version = 1,
    InsertedAtTicks = _timeProvider.GetUtcNow().UtcTicks,
    InsertSequence = Interlocked.Increment(ref _insertSequence),
}, cancellationToken: cancellationToken).ConfigureAwait(false);
```

- Sort in `GetSnapshotAsync`:

```csharp
var sort = Builders<AggregatorDocument>.Sort
    .Ascending(x => x.InsertedAtTicks)
    .Ascending(x => x.InsertSequence)
    .Ascending(x => x.Id);  // final tie-break for cross-process ties
```

- Add a compound index `(Name, InsertedAtTicks, InsertSequence)` in `EnsureIndexesAsync` to support the new sort path. The existing simple `Name` index becomes vestigial (covered by the prefix of the new compound) but is kept to avoid an unnecessary `DropOneAsync` step. The existing `(Name, DataBson.CorrelationId)` for `RemoveDataAsync` stays.

### 4.9 M37 — Distinguish structural mismatch from concurrency in `RemoveDataAsync`

**File:** `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:170-197`.

Pre-check by Name when `DeletedCount == 0`:

```csharp
if (result.IsAcknowledged && result.DeletedCount == 0)
{
    var nameOnly = Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name);
    var nameCount = await _collection.CountDocumentsAsync(nameOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
    if (nameCount == 0)
    {
        throw new KeyNotFoundException(
            $"Aggregator has no rows for Name='{name}'. Caller may have used the wrong " +
            $"aggregator name or the rows were already removed (RemoveAllAsync) by another path.");
    }
    throw new ConcurrencyException(
        $"Aggregator row not found: Name='{name}', CorrelationId='{correlationId}'. " +
        $"{nameCount} row(s) exist for this Name but none with this CorrelationId — " +
        $"row was concurrently removed or caller passed a mismatched key.");
}
```

The diagnostic distinction is operator-facing: `KeyNotFoundException` flags a structural / configuration error; `ConcurrencyException` flags a runtime race. Pre-fix both shapes were `ConcurrencyException`.

### 4.10 M38 — Per-instance index cache

**File:** `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`.

Add `private int _indexed;` instance field. `EnsureIndexesAsync` becomes:

```csharp
private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
{
    if (Volatile.Read(ref _indexed) != 0) return;

    try
    {
        var nameIndex = new CreateIndexModel<AggregatorDocument>(
            Builders<AggregatorDocument>.IndexKeys.Ascending(x => x.Name));

        var nameInsertOrderIndex = new CreateIndexModel<AggregatorDocument>(
            Builders<AggregatorDocument>.IndexKeys
                .Ascending(x => x.Name)
                .Ascending(x => x.InsertedAtTicks)
                .Ascending(x => x.InsertSequence));

        var nameCorrelationIndex = new CreateIndexModel<AggregatorDocument>(
            Builders<AggregatorDocument>.IndexKeys
                .Ascending(x => x.Name)
                .Ascending("DataBson.CorrelationId"));

        await _collection.Indexes.CreateManyAsync(
            [nameIndex, nameInsertOrderIndex, nameCorrelationIndex], cancellationToken).ConfigureAwait(false);
    }
    catch (MongoCommandException ex) when (BenignIndexCodes.Contains(ex.Code))
    {
        // Concurrent creation race; the indexes are present.
    }

    Volatile.Write(ref _indexed, 1);
}
```

Both the success path AND the benign-conflict path set `_indexed = 1`. Only an unexpected `MongoException` leaves the flag at 0 (and propagates to the caller's `catch (MongoException)`).

### 4.11 L24 — Cert callback bounds check

**File:** `src/ServiceConnect.Persistence.MongoDb/Configuration/MongoClientFactory.cs:77`.

```csharp
// before
ssl.ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0];

// after
ssl.ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) =>
    certificates is { Count: > 0 } ? certificates[0] : certificate;
```

Falls back to the server-supplied `certificate` parameter when the client-cert collection is empty/null.

### 4.12 Smaller — `count > int.MaxValue` clamp comment

**File:** `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:247`.

Add an explanatory comment; no behaviour change:

```csharp
// Clamp at int.MaxValue to match IAggregatorPersistor's int return contract. Aggregators
// are keyed by (Name, CorrelationId) and rarely exceed a few hundred rows in normal usage;
// the clamp guards against pathological cases without changing the contract.
return count > int.MaxValue ? int.MaxValue : (int)count;
```

### 4.13 Smaller — Sanitize `typeof(T).FullName` for collection names

**File:** `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs:343-348`.

```csharp
private static readonly Regex CollectionNameSanitizer = new(@"[+`\[\],]", RegexOptions.Compiled);

private static string GetCollectionName<T>() where T : class, IProcessManagerData
{
    var raw = typeof(T).FullName ?? typeof(T).Name;
    return CollectionNameSanitizer.Replace(raw, "_");
}

private static string GetCollectionName(IProcessManagerData data)
{
    var t = data.GetType();
    var raw = t.FullName ?? t.Name;
    return CollectionNameSanitizer.Replace(raw, "_");
}
```

**Migration:** v7 deployments with non-generic saga types are unaffected (no illegal chars to sanitize). v7 deployments with generic saga types had data in unsanitized collection names; v8 writes go to sanitized names → orphaned data. **Document in release notes** with a manual rename step.

### 4.14 Smaller — Verify cert cache passphrase rotation

`MongoClientFactory.cs:89` already uses:
```csharp
var cacheKey = path + "\0" + (passphrase ?? string.Empty);
```

A rotated passphrase produces a different cache key → fresh cert load. **Likely already addressed.** Confirm during implementation; document as no-op finding in spec self-review if confirmed.

## 5. Tests

Per Q1's hybrid decision — Moq unit tests for filter-shape and structural invariants; E2E behavioural tests using the existing `PersistenceFixture` for round-trip-sensitive findings.

### 5.1 Unit tests (Moq, in `src/ServiceConnect.UnitTests/`)

**New file `MongoDbProcessManagerFinderExpressionTests.cs` — H10:**
- `FindDataAsync_MessagePropIntSagaPropLong_ConvertsType`
- `FindDataAsync_NullableProperty_ConvertsType`
- `FindDataAsync_InterfaceTypedSagaProp_ConvertsType`

These structural tests pin down the `Expression.Convert` is in place; round-trip verification is in §5.2.

**New file `MongoDbProcessManagerFinderConstructorTests.cs` — H26 + M32:**
- `Constructor_NullLogger_ThrowsArgumentNullException`
- `Constructor_UnacknowledgedWriteConcern_ThrowsInvalidOperationException`
- `Constructor_AcknowledgedWriteConcern_Succeeds` (default w:1)
- `Constructor_W1WriteConcern_Succeeds`
- `Constructor_MajorityWriteConcern_Succeeds`

**New file `MongoDbAggregatorPersistorConstructorTests.cs` — M32:**
- `Constructor_NullLogger_ThrowsArgumentNullException`

**Extend existing `MongoDbAggregatorPersistorTests.cs` — M38:**
- `EnsureIndexes_CalledTwice_OnlyHitsCreateManyAsyncOnce`
- `EnsureIndexes_BenignConflict85_FlipsCacheFlag`
- `EnsureIndexes_NonBenignError_LeavesFlagUnflipped`

**Extend existing tests — M36:**
- `GetSnapshot_SortIncludesInsertSequenceTieBreaker`
- `InsertData_AssignsMonotonicInsertSequence`

**Extend existing tests — M37:**
- `RemoveData_NoRowsForName_ThrowsKeyNotFoundException`
- `RemoveData_RowsForNameButNoCorrelation_ThrowsConcurrencyException`

**H6 unit test (best-effort):** `EnsureGuidSerializerRegistered_PreExistingDifferentSerializer_ThrowsInvalidOperation`. `BsonSerializer.RegisterSerializer` mutates global state; isolation requires `[Collection("Mongo.GlobalState")]` and reflection-based reset between tests. **If global-state reset proves intractable in unit-land, defer to E2E.**

**Extend `MongoClientFactoryTests.cs` (or new file) — L24:**
- `CertificateSelectionCallback_NullCertificates_FallsBackToCertificateParam`
- `CertificateSelectionCallback_EmptyCertificates_FallsBackToCertificateParam`
- `CertificateSelectionCallback_NonEmptyCertificates_ReturnsFirst`

### 5.2 E2E tests (Testcontainers, in `src/ServiceConnect.EndToEndTests/Persistence/`)

All tests are `[Collection(nameof(PersistenceCollection))]` + `[Trait("Category", "Docker")]`.

**New file `MongoGuidSerializerConflictTests.cs` — H6:**
- `EnsureGuidSerializerRegistered_NonStandardRegisteredFirst_ThrowsInvalidOperationException`. Test must run before any other test in its collection touches `BsonSerializer`. Use isolation via `[CollectionDefinition(DisableParallelization = true)]` and possibly a separate test class with a single test. If isolation proves impractical, move to a small dedicated test assembly.

**New file `MongoDbProcessManagerFinderRoundTripTests.cs` — H10:**
- `FindData_SagaPropertyLong_MessagePropertyInt_FindsRow`
- `FindData_SagaPropertyNullableInt_MessagePropertyInt_FindsRow`
- `FindData_SagaPropertyInterface_MessagePropertyConcrete_FindsRow`

**New file `MongoDbProcessManagerFinderConcurrentInsertTests.cs` — H25:**
- `ConcurrentInsertSameCorrelationId_AfterStartupIndex_OnlyOneSurvives`
- `StartupIndexInitializer_PreCreatesIndex_BeforeFirstIO`

**New file `MongoDbAggregatorInsertOrderTests.cs` — M36:**
- `GetSnapshot_TwoInsertsAtSameTick_PreservesInsertionOrder` (uses `FakeTimeProvider`)
- `GetSnapshot_HighFrequencyInserts_PreservesInsertionOrder` (system clock)

**New file `MongoDbAggregatorRemoveDataDistinctionTests.cs` — M37:**
- `RemoveData_NoRowsForName_ThrowsKeyNotFoundException`
- `RemoveData_NameExistsButNoCorrelation_ThrowsConcurrencyException`

### 5.3 Test discipline

- Mongo unit tests use the canonical `BuildStore` Moq pattern from existing fixtures.
- Logger-capture pattern (`Mock<ILogger>` + `IsEnabled(true)` + `InvocationAction` + `DynamicInvoke`) for the index-initializer Warning test.
- E2E tests use `_fixture.GetUniqueDatabaseName(prefix)` so each test has an isolated database.
- Per-csproj `dotnet test` only with `-m:1`.
- E2E filter: `--filter "FullyQualifiedName~Mongo&FullyQualifiedName~Persistence"`.
- TDD ordering: write failing test → run pre-fix → apply fix → run post-fix → commit.

## 6. Rollout

**Single PR, multiple commits.** Same pattern as Phases 5/6/7/8.

Commit order:

1. **Spec** — this document.
2. **Plan** — `docs/superpowers/plans/2026-05-02-phase-09-mongo-aggregator-procmgr.md`.
3. **H6** — `EnsureGuidSerializerRegistered` throws on conflict.
4. **M32** — Null-logger guards (low-risk hygiene; lands early).
5. **H26** — Reject unacknowledged WriteConcern; remove `_concurrencyGuardsEnabled`.
6. **H10** — `Expression.Convert` for property hierarchy.
7. **H25** — Startup-time index creation via `IHostedService` (saga-type registry investigation may add scope).
8. **H30** — Semaphore under GC (comment-only).
9. **M31** — `BsonException` catch in aggregator.
10. **M36** — `InsertSequence` field for tie-break.
11. **M37** — Distinguish structural mismatch from concurrency in `RemoveData`.
12. **M38** — `_indexed` cache flag.
13. **L24** — Cert callback bounds check.
14. **Smaller** — count clamp comment + collection-name sanitization + cert cache passphrase verification.
15. **Phase 9 release notes.**
16. **API reference + learn updates.**
17. **Final verification gate + code review.**

**Estimated commit count:** 16-20 (including review-loop fix-ups).

**Build/test discipline:**
- Per-csproj only.
- Unit-test filter: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Mongo" -m:1`.
- E2E filter: `dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~Mongo&FullyQualifiedName~Persistence" -m:1`.

## 7. Risk assessment

| Change | Risk | Mitigation |
|---|---|---|
| H6 throws on conflict | Existing deployments with custom Guid serializer registration break loudly | v8 major; release notes + actionable error message |
| H26 rejects w:0 | Existing deployments using w:0 for sagas break loudly | v8 major; release notes + actionable error message |
| H25 hosted-service index creation | Startup ordering: must run before consumer hosts begin polling | `IHostedService.StartAsync` runs before consumer hosts; lazy fallback retained |
| M36 new `InsertSequence` field | Existing documents missing the field deserialize to `0` | Acceptable — `InsertedAtTicks` provides primary ordering; the change only affects same-tick ties |
| Generic-saga collection-name sanitization | v7 deployments with generic saga types had data in unsanitized collection names; v8 writes go to sanitized names → orphaned data | Document migration step in release notes (manual rename) |

## 8. Spec self-review

Performed inline 2026-05-02.

- **Placeholder scan:** No "TBD"/"TODO". Section 4.14 is an explicit verification-only entry; documented as no-op contingency. Section 4.3 has an "investigate during implementation" note for the saga-type registry. Section 5.1 H6 unit test has a documented fallback to E2E if global-state isolation fails. These are scoped contingencies, not unresolved design items.
- **Internal consistency:** Section 4 (per-fix), Section 5 (tests), and Section 6 (rollout) cover the same finding set in the same order. Section 3 (decisions) maps to specific section-4 items via the Q1-Q6 references. No contradictions.
- **Scope check:** Single phase, single PR. Findings bounded to one project (`ServiceConnect.Persistence.MongoDb`) plus testing artefacts and docs. Appropriate for a single implementation plan.
- **Ambiguity check:** Q3's saga-type registry shape is the only soft spot; explicitly flagged as "investigate during implementation" with two viable fallback options (existing DI metadata vs new registry). Q4's "remove `_concurrencyGuardsEnabled`" is unambiguous (Section 4.4 spells out the exact removals). Q6's `InsertSequence` field shape is concrete (Section 4.8). No remaining ambiguity.

No issues found.
