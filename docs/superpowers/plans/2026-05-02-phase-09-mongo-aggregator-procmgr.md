# Phase 09 — Mongo aggregator + process-manager + serializer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve the Mongo aggregator + process-manager correctness defects and the Guid-serializer registration bug. v8 (major) — breaking changes are explicitly permitted.

**Architecture:** Three coordinated tightenings: (1) `EnsureGuidSerializerRegistered` throws on conflict instead of swallowing (H6); (2) `MongoDbProcessManagerFinder` rejects `WriteConcern.Unacknowledged` at startup (H26) and uses `Expression.Convert` for property-hierarchy queries (H10); (3) a new `IHostedService` pre-creates per-saga unique CorrelationId indexes at startup (H25), feeding off a new public `IProcessManagerTypeRegistry` exposing the existing `ProcessManagerHandlerRegistry` descriptors. Aggregator gains a monotonic `InsertSequence` field for tie-break (M36), distinguishes `KeyNotFoundException` vs `ConcurrencyException` in `RemoveDataAsync` (M37), caches index-creation post-success (M38), and catches `BsonException` (M31). Plus null-logger guards (M32), cert-callback bounds check (L24), `_indexCreationSemaphore` rely-on-GC pattern (H30), and several smaller items.

**Tech Stack:** .NET multi-target net8.0/net10.0, MongoDB.Driver, xUnit + Moq, `Microsoft.Extensions.Time.Testing.FakeTimeProvider`, Testcontainers MongoDB (existing E2E `PersistenceFixture`), Astro/Starlight for docs.

**Spec:** [`docs/superpowers/specs/2026-05-02-phase-09-mongo-aggregator-procmgr-design.md`](../specs/2026-05-02-phase-09-mongo-aggregator-procmgr-design.md).

---

## Build/test safety

This machine has crashed when running unconstrained whole-solution `dotnet build` / `dotnet test` (CLAUDE.md has the full incident analysis). The wrapper at `~/.local/bin/dotnet` re-execs every `dotnet` invocation under a systemd cgroup with `CPUQuota=800%`, `MemoryMax=8G`, `MemorySwapMax=0`, `TasksMax=200`. **Even with the wrapper, every command in this plan is per-csproj.** Add `-m:1` to `dotnet test` invocations to serialize MSBuild within `TasksMax`. Never run whole-solution `dotnet build` / `dotnet test` / `dotnet format`.

E2E tests are Docker-tagged (`[Trait("Category", "Docker")]`) and use the existing `PersistenceFixture` (Testcontainers MongoDB + RabbitMQ). User is in the `docker` group; call `docker` directly without `sg docker -c`.

---

## File structure

### Modified — production code

- `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs` — Tasks 3 (H6), 7 (H25 DI registration).
- `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs` — Tasks 4 (M32), 5 (H26), 6 (H10), 7 (H25 reflection helper), 8 (H30 comment), 14 (smaller — collection-name sanitizer).
- `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs` — Tasks 4 (M32), 9 (M31), 10 (M36), 11 (M37), 12 (M38), 14 (smaller — count clamp comment).
- `src/ServiceConnect.Persistence.MongoDb/Configuration/MongoClientFactory.cs` — Task 13 (L24).
- `src/ServiceConnect/Services/Processors/ProcessManagerHandlerRegistry.cs` — Task 7 (implements new `IProcessManagerTypeRegistry`).
- `src/ServiceConnect/ServiceCollectionExtensions.Handlers.cs` — Task 7 (DI: register `ProcessManagerHandlerRegistry` under both `IHandlerRegistry` and `IProcessManagerTypeRegistry`).

### Created — production code

- `src/ServiceConnect.Interfaces/ProcessManagers/IProcessManagerTypeRegistry.cs` — Task 7 (new public interface).
- `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerIndexInitializer.cs` — Task 7 (`IHostedService`).

### Created — tests (unit, in `src/ServiceConnect.UnitTests/`)

- `MongoDbProcessManagerFinderExpressionTests.cs` — Task 6 (H10 expression-tree shape).
- `MongoDbProcessManagerFinderConstructorTests.cs` — Tasks 4 + 5 (M32 + H26 ctor validation).
- `MongoDbAggregatorPersistorConstructorTests.cs` — Task 4 (M32).
- `MongoClientFactoryCertCallbackTests.cs` — Task 13 (L24).

### Modified — tests (unit)

- `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs` — Tasks 9 (M31 BsonException catch), 10 (M36 sort + InsertSequence), 11 (M37 KeyNotFound vs Concurrency), 12 (M38 cache flag).
- `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreTests.cs` — Possibly (Task 7 may shift DI registrations affecting test setups).

### Created — tests (E2E, in `src/ServiceConnect.EndToEndTests/Persistence/`)

- `MongoGuidSerializerConflictTests.cs` — Task 3 (H6 round-trip).
- `MongoDbProcessManagerFinderRoundTripTests.cs` — Task 6 (H10 round-trip).
- `MongoDbProcessManagerFinderConcurrentInsertTests.cs` — Task 7 (H25 startup-index race).
- `MongoDbAggregatorInsertOrderTests.cs` — Task 10 (M36 round-trip).
- `MongoDbAggregatorRemoveDataDistinctionTests.cs` — Task 11 (M37 round-trip).

### Modified — website / examples

- `website/src/content/docs/releases.mdx` — Task 15.
- `website/src/content/docs/reference/configuration/...` — Task 16 (WriteConcern requirement, Guid representation contract).
- `website/src/content/docs/reference/process-managers/...` — Task 16 (saga store contract: acknowledged-writes-only, startup-time index pre-creation, generic-saga collection-name sanitization).
- `website/src/content/docs/reference/extension-points/persistence/...` — Task 16 (link the contract from `IProcessManagerFinder`).
- `examples/ProcessManager/README.md` — Task 16 (verify nothing depends on the removed `_concurrencyGuardsEnabled = false` semantics or unsanitized generic saga names).

---

## Task 1: Spec

**Already shipped at commit `e196f1e9`** (`docs(spec): phase 09 mongo aggregator + process-manager + serializer`). Skip.

---

## Task 2: This plan

**This is the plan commit.** After writing this file:

```bash
git add docs/superpowers/plans/2026-05-02-phase-09-mongo-aggregator-procmgr.md
git commit -m "docs(plan): phase 09 implementation plan"
```

(Add Co-Authored-By trailer: `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>`.)

---

## Task 3: H6 — `EnsureGuidSerializerRegistered` throws on conflict

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs:88-96`.
- Create: `src/ServiceConnect.EndToEndTests/Persistence/MongoGuidSerializerConflictTests.cs`.

**Background.** Currently `EnsureGuidSerializerRegistered` swallows `BsonSerializationException` if a different Guid serializer was already registered, while `MongoDbAggregatorPersistor.RemoveDataAsync` hard-codes `GuidRepresentation.Standard` for filter literals. If another component registered a non-Standard serializer first, writes use that subtype but filter literals use subtype 4 → silent miss. Q2's decision: throw loudly so the operator sees the conflict and fixes it.

- [ ] **Step 1: Read the current `EnsureGuidSerializerRegistered` body**

```bash
sed -n '36,103p' src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs
```

Confirm the `try { RegisterSerializer } catch (BsonSerializationException) { /* swallow */ }` shape at lines 88-96.

- [ ] **Step 2: Write the failing E2E test**

Create `src/ServiceConnect.EndToEndTests/Persistence/MongoGuidSerializerConflictTests.cs`:

```csharp
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoGuidSerializerConflictTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public void EnsureGuidSerializerRegistered_NonStandardRegisteredFirst_ThrowsInvalidOperationException()
    {
        // BsonSerializer.RegisterSerializer mutates global state. Once ServiceConnect's
        // own registration has run anywhere in the test process, we can no longer set up
        // the conflict scenario. The test asserts ONLY the error path: if a downstream
        // call to RegisterSerializer throws BsonSerializationException, the persistor's
        // entry-point must wrap it in InvalidOperationException with an actionable message.
        // The actual conflict is tested via reflection on a private helper if needed.
        //
        // Safer test: simulate the conflict by attempting to register the same Guid
        // serializer with a DIFFERENT representation after ServiceConnect's first call.
        // If the test runs first in the test process, ServiceConnect's call hasn't yet
        // happened — register CSharpLegacy first, then trigger ServiceConnect's path.
        try
        {
            BsonSerializer.RegisterSerializer(typeof(Guid),
                new GuidSerializer(GuidRepresentation.CSharpLegacy));
        }
        catch (BsonSerializationException)
        {
            // ServiceConnect (or another component) has already registered Guid; we cannot
            // prepare the conflict. Skip — see the unit-test fallback in MongoDbPersistenceExtensionsTests.
            return;
        }

        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = _fixture.GetUniqueDatabaseName("guidconflict"),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => MongoClientFactory.Create(options));

        Assert.Contains("GuidRepresentation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<BsonSerializationException>(ex.InnerException);
    }
}
```

The "skip if already registered" branch is a real concern given xUnit's per-class instantiation and BSON's process-wide state. Document the limitation in a code comment.

- [ ] **Step 3: Run the E2E test pre-fix**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~MongoGuidSerializerConflictTests" -m:1
```

Expected: FAIL — pre-fix the swallow returns success silently and `MongoClientFactory.Create` succeeds without throwing.

- [ ] **Step 4: Apply the H6 fix**

In `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs`, replace lines 88-96:

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

- [ ] **Step 5: Run the E2E test to verify pass**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~MongoGuidSerializerConflictTests" -m:1
```

Expected: PASS.

- [ ] **Step 6: Run unit + E2E filters to confirm no regressions**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Mongo" -m:1
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~Mongo&FullyQualifiedName~Persistence" -m:1
```

Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs \
        src/ServiceConnect.EndToEndTests/Persistence/MongoGuidSerializerConflictTests.cs
git commit -m "fix(mongo)!: EnsureGuidSerializerRegistered throws on conflict (H6)"
```

The `!` marker indicates the breaking change. Add Co-Authored-By trailer.

---

## Task 4: M32 — Null-logger guards

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:60`.
- Modify: `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs:51`.
- Create: `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorConstructorTests.cs`.
- Create: `src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderConstructorTests.cs` (will be extended in Task 5 with the WriteConcern tests).

**Background.** Both ctors currently accept null logger silently. Add `ArgumentNullException.ThrowIfNull(logger)` immediately after the existing `mongoClient` null-check. Low-risk hygiene; lands early before larger commits touch these files.

- [ ] **Step 1: Write the failing aggregator constructor test**

Create `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorConstructorTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbAggregatorPersistorConstructorTests
{
    static MongoDbAggregatorPersistorConstructorTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var client = new Mock<IMongoClient>();
        var typeRegistry = new Mock<IMessageTypeRegistry>();
        Assert.Throws<ArgumentNullException>(() =>
            new MongoDbAggregatorPersistor(
                client.Object,
                new MongoDbPersistenceOptions { DatabaseName = "test" },
                logger: null!,
                typeRegistry.Object));
    }

    [Fact]
    public void Constructor_NullTypeRegistry_ThrowsArgumentNullException()
    {
        // Regression guard for the existing null-check that already exists.
        var client = new Mock<IMongoClient>();
        Assert.Throws<ArgumentNullException>(() =>
            new MongoDbAggregatorPersistor(
                client.Object,
                new MongoDbPersistenceOptions { DatabaseName = "test" },
                NullLogger<MongoDbAggregatorPersistor>.Instance,
                typeRegistry: null!));
    }
}
```

- [ ] **Step 2: Write the failing process-manager constructor test (M32 part)**

Create `src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderConstructorTests.cs`:

```csharp
using Moq;
using MongoDB.Driver;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbProcessManagerFinderConstructorTests
{
    static MongoDbProcessManagerFinderConstructorTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    private static Mock<IMongoClient> BuildAcknowledgedClient()
    {
        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        return client;
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var client = BuildAcknowledgedClient();
        Assert.Throws<ArgumentNullException>(() =>
            new MongoDbProcessManagerFinder(
                client.Object,
                new MongoDbPersistenceOptions { DatabaseName = "test" },
                logger: null!));
    }
}
```

NOTE: this test will be extended in Task 5 with WriteConcern coverage. The `BuildAcknowledgedClient` helper is set up here for reuse.

- [ ] **Step 3: Run the tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbAggregatorPersistorConstructorTests|FullyQualifiedName~MongoDbProcessManagerFinderConstructorTests" -m:1
```

Expected: 2 of 3 FAIL (the null-logger tests; the null-typeRegistry test passes pre-fix because that guard already exists).

- [ ] **Step 4: Apply the M32 fix to `MongoDbAggregatorPersistor`**

In `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`, around line 59:

```csharp
// before
public MongoDbAggregatorPersistor(IMongoClient mongoClient, MongoDbPersistenceOptions options, string collectionName, ILogger<MongoDbAggregatorPersistor> logger, IMessageTypeRegistry typeRegistry, TimeProvider? timeProvider = null)
{
    ArgumentNullException.ThrowIfNull(mongoClient);
    _logger = logger;
    _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));

// after
public MongoDbAggregatorPersistor(IMongoClient mongoClient, MongoDbPersistenceOptions options, string collectionName, ILogger<MongoDbAggregatorPersistor> logger, IMessageTypeRegistry typeRegistry, TimeProvider? timeProvider = null)
{
    ArgumentNullException.ThrowIfNull(mongoClient);
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
    _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
```

- [ ] **Step 5: Apply the M32 fix to `MongoDbProcessManagerFinder`**

In `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs`, around line 50:

```csharp
// before
public MongoDbProcessManagerFinder(IMongoClient mongoClient, MongoDbPersistenceOptions options, ILogger<MongoDbProcessManagerFinder> logger, TimeProvider? timeProvider = null)
{
    ArgumentNullException.ThrowIfNull(mongoClient);
    _logger = logger;

// after
public MongoDbProcessManagerFinder(IMongoClient mongoClient, MongoDbPersistenceOptions options, ILogger<MongoDbProcessManagerFinder> logger, TimeProvider? timeProvider = null)
{
    ArgumentNullException.ThrowIfNull(mongoClient);
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
```

- [ ] **Step 6: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbAggregatorPersistorConstructorTests|FullyQualifiedName~MongoDbProcessManagerFinderConstructorTests" -m:1
```

Expected: 3/3 PASS.

- [ ] **Step 7: Commit**

```bash
git add src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorConstructorTests.cs \
        src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderConstructorTests.cs \
        src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs \
        src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs
git commit -m "fix(mongo): null-logger guards on aggregator + process-manager ctors (M32)"
```

(Co-Authored-By trailer.)

---

## Task 5: H26 — Reject `WriteConcern.Unacknowledged` at startup

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs:62-74` (detection block); also lines 26 (`_concurrencyGuardsEnabled` field), 235 (UpdateDataAsync guard), 289 (DeleteDataAsync guard).
- Modify: `src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderConstructorTests.cs` (extend with WriteConcern tests).

**Background.** Q4's decision: reject w:0 at construction. Saga state is correctness-sensitive; w:0 silently loses concurrent updates and wedges sagas on the next real conflict. Remove the `_concurrencyGuardsEnabled` field entirely.

- [ ] **Step 1: Extend `MongoDbProcessManagerFinderConstructorTests` with WriteConcern tests**

In `src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderConstructorTests.cs`, add:

```csharp
[Fact]
public void Constructor_UnacknowledgedWriteConcern_ThrowsInvalidOperationException()
{
    var client = new Mock<IMongoClient>();
    client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.Unacknowledged });

    var ex = Assert.Throws<InvalidOperationException>(() =>
        new MongoDbProcessManagerFinder(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MongoDbProcessManagerFinder>.Instance));

    Assert.Contains("WriteConcern", ex.Message);
    Assert.Contains("acknowledged", ex.Message, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void Constructor_W1WriteConcern_Succeeds()
{
    var client = new Mock<IMongoClient>();
    client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
    var database = new Mock<IMongoDatabase>();
    client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

    var finder = new MongoDbProcessManagerFinder(
        client.Object,
        new MongoDbPersistenceOptions { DatabaseName = "test" },
        Microsoft.Extensions.Logging.Abstractions.NullLogger<MongoDbProcessManagerFinder>.Instance);

    Assert.NotNull(finder);
}

[Fact]
public void Constructor_MajorityWriteConcern_Succeeds()
{
    var client = new Mock<IMongoClient>();
    client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.WMajority });
    var database = new Mock<IMongoDatabase>();
    client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

    var finder = new MongoDbProcessManagerFinder(
        client.Object,
        new MongoDbPersistenceOptions { DatabaseName = "test" },
        Microsoft.Extensions.Logging.Abstractions.NullLogger<MongoDbProcessManagerFinder>.Instance);

    Assert.NotNull(finder);
}
```

- [ ] **Step 2: Run the new tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbProcessManagerFinderConstructorTests" -m:1
```

Expected: `Constructor_UnacknowledgedWriteConcern_ThrowsInvalidOperationException` FAILS — pre-fix the ctor logs a Warning and continues.

- [ ] **Step 3: Apply the H26 fix — remove `_concurrencyGuardsEnabled` field**

In `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs`, remove the field declaration (around line 26):

```csharp
// REMOVE:
// Under WriteConcern.Unacknowledged the driver does not report MatchedCount/DeletedCount.
// ... (preceding comment block)
private readonly bool _concurrencyGuardsEnabled;
```

- [ ] **Step 4: Replace the detection block with a throw**

Around line 62-74, replace:

```csharp
// before
// Detect WriteConcern.Unacknowledged (w:0) at construction time.
// Under w:0 the driver does not populate MatchedCount/DeletedCount and accessing
// them throws NotSupportedException. Disable the concurrency assertions and log a
// one-time Warning so operators are aware the guarantees are relaxed.
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
// Saga state is correctness-sensitive: w:0 silently loses concurrent updates and
// wedges the saga on the next real conflict because the version field advances
// without the matching ReplaceOne hitting a row. Reject loudly at startup.
if (!mongoClient.Settings.WriteConcern.IsAcknowledged)
{
    throw new InvalidOperationException(
        "MongoDbProcessManagerFinder requires an acknowledged WriteConcern (w:1 or higher). " +
        "WriteConcern.Unacknowledged (w:0) silently loses concurrent saga updates and " +
        "wedges sagas on the next real conflict because the version field advances. " +
        "Configure mongoClient.Settings.WriteConcern to a value where IsAcknowledged is true.");
}
```

- [ ] **Step 5: Remove the `_concurrencyGuardsEnabled` guards in `UpdateDataAsync`**

Around line 235, replace:

```csharp
// before
if (_concurrencyGuardsEnabled && result.IsAcknowledged && result.MatchedCount == 0)
{
    throw new ConcurrencyException(
        $"Concurrency conflict: ProcessManagerData with CorrelationId {versionData.Data.CorrelationId} and Version {currentVersion} could not be updated.");
}

// after
if (result.IsAcknowledged && result.MatchedCount == 0)
{
    throw new ConcurrencyException(
        $"Concurrency conflict: ProcessManagerData with CorrelationId {versionData.Data.CorrelationId} and Version {currentVersion} could not be updated.");
}
```

- [ ] **Step 6: Remove the `_concurrencyGuardsEnabled` guard in `DeleteDataAsync`**

Around line 289, replace:

```csharp
// before
if (_concurrencyGuardsEnabled && result.IsAcknowledged && result.DeletedCount == 0)
{
    throw new ConcurrencyException(
        $"Concurrency conflict: ProcessManagerData with CorrelationId {correlationId} and Version {expectedVersion} could not be deleted.");
}

// after
if (result.IsAcknowledged && result.DeletedCount == 0)
{
    throw new ConcurrencyException(
        $"Concurrency conflict: ProcessManagerData with CorrelationId {correlationId} and Version {expectedVersion} could not be deleted.");
}
```

- [ ] **Step 7: Run the tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbProcessManagerFinderConstructorTests" -m:1
```

Expected: 4/4 PASS (1 from Task 4 null-logger + 3 new WriteConcern tests).

- [ ] **Step 8: Run any pre-existing tests that exercise UpdateDataAsync/DeleteDataAsync**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDb&FullyQualifiedName~ProcessManager" -m:1
```

Expected: all pass. If a pre-existing test set up `WriteConcern.Unacknowledged` and asserted "no throw," that test was locking in the pre-fix bug — surface in your report and either remove it or invert it (the v8 contract is reject-at-startup).

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderConstructorTests.cs \
        src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs
git commit -m "fix(mongo)!: reject unacknowledged WriteConcern at startup (H26)"
```

(Co-Authored-By trailer; `!` marker for breaking change.)

---

## Task 6: H10 — `Expression.Convert` for property-hierarchy queries

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs:117`.
- Create: `src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderExpressionTests.cs`.
- Create: `src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderRoundTripTests.cs`.

**Background.** Currently `Expression.Constant(msgPropValue, msgPropValue.GetType())` uses the runtime type — when message-side `int` is passed against a saga-side `long` (or `Nullable<T>`, or interface-typed property), the BSON path projection mismatches and the query silently misses.

- [ ] **Step 1: Read the current expression-building code**

```bash
sed -n '107,130p' src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs
```

- [ ] **Step 2: Write the failing unit tests (structural)**

Create `src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderExpressionTests.cs`:

```csharp
using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbProcessManagerFinderExpressionTests
{
    static MongoDbProcessManagerFinderExpressionTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    public sealed class TestSagaData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public long LongProp { get; set; }
        public int? NullableIntProp { get; set; }
    }

    public sealed class TestMessage : Message
    {
        public int LongProp { get; set; } // int on the message side, long on saga
        public int NullableIntProp { get; set; }
    }

    private static (MongoDbProcessManagerFinder Finder, Mock<IMongoCollection<MongoDbData<TestSagaData>>> Collection)
        BuildFinder()
    {
        var indexes = new Mock<IMongoIndexManager<MongoDbData<TestSagaData>>>();
        indexes.Setup(m => m.CreateOneAsync(
                It.IsAny<CreateIndexModel<MongoDbData<TestSagaData>>>(),
                It.IsAny<CreateOneIndexOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("ok");

        var collection = new Mock<IMongoCollection<MongoDbData<TestSagaData>>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<MongoDbData<TestSagaData>>(It.IsAny<string>(), null))
            .Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        var finder = new MongoDbProcessManagerFinder(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            NullLogger<MongoDbProcessManagerFinder>.Instance);

        return (finder, collection);
    }

    [Fact]
    public async Task FindDataAsync_MessagePropIntSagaPropLong_ExpressionContainsConvert()
    {
        var (finder, collection) = BuildFinder();

        Expression<Func<MongoDbData<TestSagaData>, bool>>? captured = null;
        var emptyCursor = new Mock<IAsyncCursor<MongoDbData<TestSagaData>>>();
        emptyCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        collection.Setup(c => c.FindAsync(
                It.IsAny<Expression<Func<MongoDbData<TestSagaData>, bool>>>(),
                It.IsAny<FindOptions<MongoDbData<TestSagaData>, MongoDbData<TestSagaData>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Expression<Func<MongoDbData<TestSagaData>, bool>>, FindOptions<MongoDbData<TestSagaData>, MongoDbData<TestSagaData>>, CancellationToken>(
                (expr, _, _) => captured = expr)
            .ReturnsAsync(emptyCursor.Object);

        var mapper = new Mock<IProcessManagerPropertyMapper>();
        var mapping = BuildMapping(typeof(TestMessage), msg => ((TestMessage)msg).LongProp, "LongProp");
        mapper.SetupGet(m => m.Mappings).Returns([mapping]);

        await finder.FindDataAsync<TestSagaData>(mapper.Object, new TestMessage { LongProp = 42 });

        Assert.NotNull(captured);
        // Walk the body looking for an Expression.Convert. Pre-fix the expression contains
        // an Equal of (Property("LongProp") == Constant(42, int)) — no Convert. Post-fix the
        // RHS is wrapped in Convert(Constant, long).
        var body = captured!.Body;
        var equal = Assert.IsAssignableFrom<BinaryExpression>(body);
        Assert.IsAssignableFrom<UnaryExpression>(equal.Right);
        var convert = (UnaryExpression)equal.Right;
        Assert.Equal(ExpressionType.Convert, convert.NodeType);
        Assert.Equal(typeof(long), convert.Type);
    }

    [Fact]
    public async Task FindDataAsync_NullableProperty_ExpressionContainsConvert()
    {
        var (finder, collection) = BuildFinder();

        Expression<Func<MongoDbData<TestSagaData>, bool>>? captured = null;
        var emptyCursor = new Mock<IAsyncCursor<MongoDbData<TestSagaData>>>();
        emptyCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        collection.Setup(c => c.FindAsync(
                It.IsAny<Expression<Func<MongoDbData<TestSagaData>, bool>>>(),
                It.IsAny<FindOptions<MongoDbData<TestSagaData>, MongoDbData<TestSagaData>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<Expression<Func<MongoDbData<TestSagaData>, bool>>, FindOptions<MongoDbData<TestSagaData>, MongoDbData<TestSagaData>>, CancellationToken>(
                (expr, _, _) => captured = expr)
            .ReturnsAsync(emptyCursor.Object);

        var mapper = new Mock<IProcessManagerPropertyMapper>();
        var mapping = BuildMapping(typeof(TestMessage), msg => ((TestMessage)msg).NullableIntProp, "NullableIntProp");
        mapper.SetupGet(m => m.Mappings).Returns([mapping]);

        await finder.FindDataAsync<TestSagaData>(mapper.Object, new TestMessage { NullableIntProp = 7 });

        Assert.NotNull(captured);
        var body = captured!.Body;
        var equal = Assert.IsAssignableFrom<BinaryExpression>(body);
        Assert.IsAssignableFrom<UnaryExpression>(equal.Right);
        var convert = (UnaryExpression)equal.Right;
        Assert.Equal(ExpressionType.Convert, convert.NodeType);
        Assert.Equal(typeof(int?), convert.Type);
    }

    private static MessageMapping BuildMapping(Type messageType, Func<Message, object?> messageProp, string propertyName)
    {
        var prop = typeof(TestSagaData).GetProperty(propertyName)!;
        return new MessageMapping
        {
            MessageType = messageType,
            MessageProp = messageProp,
            PropertiesHierarchy = new Dictionary<string, Type> { { propertyName, prop.PropertyType } },
        };
    }
}
```

NOTE: the exact shape of `MessageMapping` and `IProcessManagerPropertyMapper` depends on the codebase — read the actual types and adapt the helper. The test's intent is: capture the `Expression`, walk the body, assert the RHS is a `Convert` to the saga property's declared type.

- [ ] **Step 3: Run the unit tests pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbProcessManagerFinderExpressionTests" -m:1
```

Expected: 2 FAIL — pre-fix the expression has no `Convert` (RHS is a `Constant` directly).

- [ ] **Step 4: Apply the H10 fix**

In `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs:117`, change:

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

- [ ] **Step 5: Run the unit tests to verify pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbProcessManagerFinderExpressionTests" -m:1
```

Expected: PASS.

- [ ] **Step 6: Write the E2E round-trip tests**

Create `src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderRoundTripTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbProcessManagerFinderRoundTripTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    public sealed class LongPropSagaData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public long MyProp { get; set; }
    }

    public sealed class IntPropMessage : Message
    {
        public int MyProp { get; set; }
    }

    public sealed class NullableIntPropSagaData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public int? MyProp { get; set; }
    }

    private MongoDbProcessManagerFinder BuildFinder(string dbName)
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        return new MongoDbProcessManagerFinder(client, options, NullLogger<MongoDbProcessManagerFinder>.Instance);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task FindData_SagaPropertyLong_MessagePropertyInt_FindsRow()
    {
        var dbName = _fixture.GetUniqueDatabaseName("longvsint");
        var finder = BuildFinder(dbName);

        var sagaData = new LongPropSagaData { CorrelationId = Guid.NewGuid(), MyProp = 42L };
        await finder.InsertDataAsync(sagaData);

        // Build a mapper that maps message.MyProp (int) → saga.MyProp (long).
        var mapper = new TestProcessManagerPropertyMapper();
        mapper.AddMapping<IntPropMessage, LongPropSagaData>(m => m.MyProp, d => d.MyProp);

        var result = await finder.FindDataAsync<LongPropSagaData>(mapper, new IntPropMessage { MyProp = 42 });

        Assert.NotNull(result);
        Assert.Equal(sagaData.CorrelationId, result.Data.CorrelationId);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task FindData_SagaPropertyNullableInt_MessagePropertyInt_FindsRow()
    {
        var dbName = _fixture.GetUniqueDatabaseName("nullablevsint");
        var finder = BuildFinder(dbName);

        var sagaData = new NullableIntPropSagaData { CorrelationId = Guid.NewGuid(), MyProp = 7 };
        await finder.InsertDataAsync(sagaData);

        var mapper = new TestProcessManagerPropertyMapper();
        mapper.AddMapping<IntPropMessage, NullableIntPropSagaData>(m => m.MyProp, d => d.MyProp);

        var result = await finder.FindDataAsync<NullableIntPropSagaData>(mapper, new IntPropMessage { MyProp = 7 });

        Assert.NotNull(result);
        Assert.Equal(sagaData.CorrelationId, result.Data.CorrelationId);
    }
}
```

NOTE: the helper `TestProcessManagerPropertyMapper` already exists at `src/ServiceConnect.EndToEndTests/Helpers/TestProcessManagerPropertyMapper.cs`. Use it directly. Adapt the `AddMapping` call to its actual signature.

- [ ] **Step 7: Run the E2E tests pre-fix... wait, the fix is already applied.**

Actually we've already applied the fix in Step 4. The E2E tests are post-fix only — they verify round-trip semantics. Run them:

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~MongoDbProcessManagerFinderRoundTripTests" -m:1
```

Expected: PASS.

If you want to demonstrate the pre-fix failure, `git stash` the production change in `MongoDbProcessManagerFinder.cs:117`, run the E2E tests (expect FAIL — `result` is null), then `git stash pop` and re-run (expect PASS). Document in commit message.

- [ ] **Step 8: Run the broader filter to confirm no regressions**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDb" -m:1
```

Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderExpressionTests.cs \
        src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderRoundTripTests.cs \
        src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs
git commit -m "fix(mongo): Expression.Convert for saga property-hierarchy queries (H10)"
```

(Co-Authored-By trailer.)

---

## Task 7: H25 — Startup-time index creation via `IHostedService`

**Files:**
- Create: `src/ServiceConnect.Interfaces/ProcessManagers/IProcessManagerTypeRegistry.cs`.
- Create: `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerIndexInitializer.cs`.
- Modify: `src/ServiceConnect/Services/Processors/ProcessManagerHandlerRegistry.cs` (implement `IProcessManagerTypeRegistry`).
- Modify: `src/ServiceConnect/ServiceCollectionExtensions.Handlers.cs` (register the same instance under both interfaces).
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs` (add `IHostedService` registration).
- Modify: `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs` (add `EnsureCorrelationIdIndexForTypeAsync(Type, CancellationToken)` reflection helper).
- Create: `src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderConcurrentInsertTests.cs`.

**Background.** The lazy `EnsureCorrelationIdIndexAsync` admits cross-process duplicates between cold-start and first I/O. Pre-create unique indexes at startup via an `IHostedService` driven by a saga-type registry. The lazy fallback is retained on the I/O path.

- [ ] **Step 1: Create the new public interface**

Create `src/ServiceConnect.Interfaces/ProcessManagers/IProcessManagerTypeRegistry.cs`:

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// Enumerates the saga data types registered with the bus. Used by persistence
/// providers that need to pre-create per-saga structures (e.g. Mongo unique
/// CorrelationId indexes) at startup.
/// </summary>
public interface IProcessManagerTypeRegistry
{
    /// <summary>
    /// All saga data types currently registered. Implementations should return a
    /// snapshot — callers may iterate freely without locking.
    /// </summary>
    IEnumerable<Type> SagaDataTypes { get; }
}
```

- [ ] **Step 2: Make `ProcessManagerHandlerRegistry` implement `IProcessManagerTypeRegistry`**

In `src/ServiceConnect/Services/Processors/ProcessManagerHandlerRegistry.cs`:

```csharp
// before
internal sealed class ProcessManagerHandlerRegistry : IHandlerRegistry
{
    private readonly FrozenDictionary<Type, ProcessManagerDescriptor> _descriptors;
    // ...

// after
internal sealed class ProcessManagerHandlerRegistry : IHandlerRegistry, IProcessManagerTypeRegistry
{
    private readonly FrozenDictionary<Type, ProcessManagerDescriptor> _descriptors;
    private readonly IReadOnlyList<Type> _sagaDataTypes;
    // ...
```

In the constructor, after `_descriptors = builder.ToFrozenDictionary()`, capture the saga data types:

```csharp
_descriptors = builder.ToFrozenDictionary();
// Snapshot of distinct saga data types for IProcessManagerTypeRegistry consumers.
_sagaDataTypes = _descriptors.Values
    .Select(d => d.DataType)
    .Distinct()
    .ToArray();
```

Add the interface implementation at the bottom of the class:

```csharp
public IEnumerable<Type> SagaDataTypes => _sagaDataTypes;
```

- [ ] **Step 3: Wire DI to register the same instance under both interfaces**

In `src/ServiceConnect/ServiceCollectionExtensions.Handlers.cs`, find the `ProcessManagerHandlerRegistry` registration and add the second registration:

```bash
grep -n "ProcessManagerHandlerRegistry" src/ServiceConnect/ServiceCollectionExtensions.Handlers.cs
```

The existing pattern likely registers it as `IHandlerRegistry` via factory. Add:

```csharp
// Register the same instance under IProcessManagerTypeRegistry so persistence
// providers that need to pre-create per-saga structures (e.g. Mongo unique
// CorrelationId indexes) at startup can enumerate the saga data types.
services.AddSingleton<IProcessManagerTypeRegistry>(sp =>
    sp.GetServices<IHandlerRegistry>().OfType<ProcessManagerHandlerRegistry>().Single());
```

If the existing registration uses a different shape (e.g., explicit instance), adapt to ensure both interfaces resolve to the same instance.

- [ ] **Step 4: Add `EnsureCorrelationIdIndexForTypeAsync` to the finder**

In `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs`, add a new internal method that dispatches by reflection (cached delegate, mirroring the existing `InsertDelegateCache` at line 30):

```csharp
// Cache for reflection-dispatched generic invocations of EnsureCorrelationIdIndexAsync<T>.
private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<MongoDbProcessManagerFinder, CancellationToken, Task>>
    EnsureIndexDelegateCache = new();

internal Task EnsureCorrelationIdIndexForTypeAsync(Type dataType, CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(dataType);
    var del = EnsureIndexDelegateCache.GetOrAdd(dataType, static t =>
    {
        var collectionMethod = typeof(IMongoDatabase)
            .GetMethod(nameof(IMongoDatabase.GetCollection), [typeof(string), typeof(MongoCollectionSettings)])!
            .MakeGenericMethod(typeof(MongoDbData<>).MakeGenericType(t));
        var ensureMethod = typeof(MongoDbProcessManagerFinder)
            .GetMethod(nameof(EnsureCorrelationIdIndexAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(t);

        // Build (finder, ct) => finder.EnsureCorrelationIdIndexAsync<T>(
        //     finder._mongoDatabase.GetCollection<MongoDbData<T>>(collectionName, null),
        //     collectionName,
        //     ct)
        // using cached compiled expression. The collection-name calculation is the same
        // sanitization as GetCollectionName<T>().
        var finderParam = Expression.Parameter(typeof(MongoDbProcessManagerFinder), "finder");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        var collectionName = SanitizeCollectionName(t.FullName ?? t.Name);
        var dbField = Expression.Field(finderParam, nameof(_mongoDatabase));
        var collectionExpr = Expression.Call(
            dbField,
            collectionMethod,
            Expression.Constant(collectionName),
            Expression.Constant(null, typeof(MongoCollectionSettings)));

        var call = Expression.Call(finderParam, ensureMethod, collectionExpr,
            Expression.Constant(collectionName), ctParam);

        return Expression.Lambda<Func<MongoDbProcessManagerFinder, CancellationToken, Task>>(
            call, finderParam, ctParam).Compile();
    });

    return del(this, cancellationToken);
}
```

NOTE: `SanitizeCollectionName` is added in Task 14. For Task 7 the body can use `t.FullName ?? t.Name` directly; the sanitizer integration happens when Task 14 lands.

- [ ] **Step 5: Create the hosted service**

Create `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerIndexInitializer.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.MongoDb;

/// <summary>
/// Pre-creates per-saga unique CorrelationId indexes at startup. Closes the
/// cross-process race window where two cold-started processes could insert
/// duplicate saga rows before either one called the lazy index-creation path.
/// </summary>
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
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

- [ ] **Step 6: Register the hosted service in `MongoDbPersistenceExtensions`**

In `src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs`, in the `UseMongoDbPersistence`'s `AddRegistration` callback, after the `services.TryAddSingleton<MongoDbProcessManagerFinder>` line:

```csharp
services.AddHostedService<MongoDbProcessManagerIndexInitializer>();
```

- [ ] **Step 7: Write the E2E concurrent-insert test**

Create `src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderConcurrentInsertTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbProcessManagerFinderConcurrentInsertTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    public sealed class IndexRaceSagaData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ConcurrentInsertSameCorrelationId_AfterStartupIndex_OnlyOneSurvives()
    {
        var dbName = _fixture.GetUniqueDatabaseName("indexrace");
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var finder = new MongoDbProcessManagerFinder(client, options, NullLogger<MongoDbProcessManagerFinder>.Instance);

        // Simulate the hosted service: pre-create the unique index for the saga type.
        await finder.EnsureCorrelationIdIndexForTypeAsync(typeof(IndexRaceSagaData), CancellationToken.None);

        var correlationId = Guid.NewGuid();
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await finder.InsertDataAsync(new IndexRaceSagaData { CorrelationId = correlationId });
                    return true;
                }
                catch (Exception)
                {
                    // Mongo throws E11000 (duplicate key) as MongoBulkWriteException or wrapped
                    // PersistenceException — either way, this insert lost the race.
                    return false;
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);

        Assert.Equal(1, successCount);
    }
}
```

- [ ] **Step 8: Run unit + E2E tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDb" -m:1
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~MongoDbProcessManagerFinderConcurrentInsertTests" -m:1
```

Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Interfaces/ProcessManagers/IProcessManagerTypeRegistry.cs \
        src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerIndexInitializer.cs \
        src/ServiceConnect/Services/Processors/ProcessManagerHandlerRegistry.cs \
        src/ServiceConnect/ServiceCollectionExtensions.Handlers.cs \
        src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs \
        src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs \
        src/ServiceConnect.EndToEndTests/Persistence/MongoDbProcessManagerFinderConcurrentInsertTests.cs
git commit -m "feat(mongo): startup-time CorrelationId index creation via IHostedService (H25)"
```

(Co-Authored-By trailer.)

---

## Task 8: H30 — Semaphore under GC (comment-only)

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs:20`.

- [ ] **Step 1: Add the explanatory comment block above the field declaration**

In `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs`, around line 20:

```csharp
// before
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _indexedCollections = new(StringComparer.Ordinal);
private readonly SemaphoreSlim _indexCreationSemaphore = new(1, 1);

// after
private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _indexedCollections = new(StringComparer.Ordinal);
// _indexCreationSemaphore is intentionally NOT Disposed:
// SemaphoreSlim.Dispose only releases the lazily-allocated WaitHandle, and we never call
// AvailableWaitHandle, so disposal is a functional no-op. A concurrent caller's Release()
// on a disposed semaphore would throw ObjectDisposedException out of the unwind path,
// which we cannot prevent without holding GC references to every caller. Mirrors the
// Connection / ProducerConnection / Producer / Bus pattern (Phases 4 + 6 + 7).
private readonly SemaphoreSlim _indexCreationSemaphore = new(1, 1);
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj -m:1
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs
git commit -m "docs(mongo): document semaphore rely-on-GC pattern (H30)"
```

(Co-Authored-By trailer.)

---

## Task 9: M31 — Catch `BsonException` in aggregator

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs` (every method that catches `MongoException`).
- Modify: `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs` — add the BsonException-catch tests.

**Background.** `BsonSerializationException` derives from `BsonException`, not `MongoException`. Currently the aggregator only catches `MongoException`, so BSON-level errors escape the `PersistenceException` contract. Add a sibling `catch (BsonException ex)` BEFORE each existing `MongoException` catch.

- [ ] **Step 1: Write the failing test for `InsertDataAsync` BsonException catch**

In `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs` (or new file if extension is too large), add:

```csharp
using MongoDB.Bson;

[Fact]
public async Task InsertDataAsync_BsonSerializationException_WrappedInPersistenceException()
{
    var indexes = new Mock<IMongoIndexManager<MongoDbAggregatorPersistor.AggregatorDocument>>();
    indexes.Setup(m => m.CreateManyAsync(
            It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(["ok"]);

    var collection = new Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>>();
    collection.SetupGet(c => c.Indexes).Returns(indexes.Object);
    collection.Setup(c => c.InsertOneAsync(
            It.IsAny<MongoDbAggregatorPersistor.AggregatorDocument>(),
            It.IsAny<InsertOneOptions?>(),
            It.IsAny<CancellationToken>()))
        .ThrowsAsync(new BsonSerializationException("simulated BSON failure"));

    var database = new Mock<IMongoDatabase>();
    database.Setup(d => d.GetCollection<MongoDbAggregatorPersistor.AggregatorDocument>("Aggregator", null))
        .Returns(collection.Object);

    var client = new Mock<IMongoClient>();
    client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

    var typeRegistry = new Mock<IMessageTypeRegistry>();
    var persistor = new MongoDbAggregatorPersistor(
        client.Object,
        new MongoDbPersistenceOptions { DatabaseName = "test" },
        Microsoft.Extensions.Logging.Abstractions.NullLogger<MongoDbAggregatorPersistor>.Instance,
        typeRegistry.Object);

    var ex = await Assert.ThrowsAsync<PersistenceException>(() =>
        persistor.InsertDataAsync(new { Foo = "bar" }, "test-name"));

    Assert.IsType<BsonSerializationException>(ex.InnerException);
}
```

NOTE: `MongoDbAggregatorPersistor.AggregatorDocument` is `internal` — uses `InternalsVisibleTo("ServiceConnect.UnitTests")` to access. If not present in the Mongo csproj's InternalsVisibleTo list, add it.

- [ ] **Step 2: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbAggregatorPersistorTests.InsertDataAsync_BsonSerializationException" -m:1
```

Expected: FAIL — pre-fix the `BsonSerializationException` propagates uncaught (escapes `MongoException` catch) and `Assert.ThrowsAsync<PersistenceException>` sees the wrong type.

- [ ] **Step 3: Apply the M31 fix to all aggregator methods**

In `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`, add `catch (BsonException ex)` BEFORE each existing `catch (MongoException ex)`. Methods to update: `InsertDataAsync`, `GetSnapshotAsync`, `RemoveDataAsync`, `RemoveAllAsync`, `RemoveSnapshotAsync`, `CountAsync`. Pattern:

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

The same message string is fine — the `ex` parameter carries the type distinction.

- [ ] **Step 4: Run the focused tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbAggregatorPersistorTests" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs \
        src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs
git commit -m "fix(mongo-aggregator): catch BsonException in PersistenceException contract (M31)"
```

(Co-Authored-By trailer.)

---

## Task 10: M36 — `InsertSequence` field for tie-break

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`.
- Modify: `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs` (sort + sequence assertions).
- Create: `src/ServiceConnect.EndToEndTests/Persistence/MongoDbAggregatorInsertOrderTests.cs`.

**Background.** Q6's decision: monotonic per-process counter via `Interlocked.Increment`. Sort becomes `(InsertedAtTicks, InsertSequence, Id)`. New compound index covers the sort path.

- [ ] **Step 1: Write the failing unit test for the sort shape**

In `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs`, add:

```csharp
[Fact]
public async Task GetSnapshot_SortIncludesInsertSequenceTieBreaker()
{
    SortDefinition<MongoDbAggregatorPersistor.AggregatorDocument>? capturedSort = null;
    var indexes = new Mock<IMongoIndexManager<MongoDbAggregatorPersistor.AggregatorDocument>>();
    indexes.Setup(m => m.CreateManyAsync(
            It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(["ok"]);

    var collection = new Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>>();
    collection.SetupGet(c => c.Indexes).Returns(indexes.Object);

    var fluent = new Mock<IFindFluent<MongoDbAggregatorPersistor.AggregatorDocument, MongoDbAggregatorPersistor.AggregatorDocument>>();
    fluent.Setup(f => f.Sort(It.IsAny<SortDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>()))
        .Callback<SortDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(s => capturedSort = s)
        .Returns(fluent.Object);
    fluent.Setup(f => f.ToListAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(new List<MongoDbAggregatorPersistor.AggregatorDocument>());
    collection.Setup(c => c.Find(It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(), It.IsAny<FindOptions>()))
        .Returns(fluent.Object);

    var database = new Mock<IMongoDatabase>();
    database.Setup(d => d.GetCollection<MongoDbAggregatorPersistor.AggregatorDocument>("Aggregator", null))
        .Returns(collection.Object);
    var client = new Mock<IMongoClient>();
    client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);
    var typeRegistry = new Mock<IMessageTypeRegistry>();

    var persistor = new MongoDbAggregatorPersistor(
        client.Object,
        new MongoDbPersistenceOptions { DatabaseName = "test" },
        Microsoft.Extensions.Logging.Abstractions.NullLogger<MongoDbAggregatorPersistor>.Instance,
        typeRegistry.Object);

    await persistor.GetSnapshotAsync("test-name");

    Assert.NotNull(capturedSort);
    var sortJson = capturedSort!.Render(
        BsonSerializer.LookupSerializer<MongoDbAggregatorPersistor.AggregatorDocument>(),
        BsonSerializer.SerializerRegistry).ToJson();
    Assert.Contains("\"InsertedAtTicks\" : 1", sortJson);
    Assert.Contains("\"InsertSequence\" : 1", sortJson);
}
```

- [ ] **Step 2: Run the test pre-fix**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbAggregatorPersistorTests.GetSnapshot_SortIncludesInsertSequenceTieBreaker" -m:1
```

Expected: FAIL — pre-fix sort has no `InsertSequence`.

- [ ] **Step 3: Add `InsertSequence` field and counter**

In `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`:

Add the per-instance counter near the other private fields:

```csharp
// Monotonic per-process counter: assigned to each insert via Interlocked.Increment so
// rows that share an InsertedAtTicks value are still totally ordered within this process.
// Cross-process ties remain unsolved (the existing Id sort is the final tie-break) but
// per-(Name, CorrelationId) aggregator state is processed by a single consumer at a time,
// so per-process order matches the actual usage pattern.
private long _insertSequence;
```

Add the field to `AggregatorDocument`:

```csharp
internal sealed class AggregatorDocument
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public BsonDocument DataBson { get; set; } = default!;
    public string DataTypeName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long InsertedAtTicks { get; set; }
    // Existing documents missing this field deserialize to 0 — same default as
    // InsertedAtTicks's introduction in a prior phase. New inserts populate via
    // Interlocked.Increment(ref _insertSequence).
    public long InsertSequence { get; set; }
}
```

- [ ] **Step 4: Populate `InsertSequence` in `InsertDataAsync`**

In `InsertDataAsync`:

```csharp
// before
await _collection.InsertOneAsync(new AggregatorDocument
{
    Id = Guid.NewGuid(),
    Name = name,
    DataBson = dataBson,
    DataTypeName = dataType.FullName!,
    Version = 1,
    InsertedAtTicks = _timeProvider.GetUtcNow().UtcTicks,
}, cancellationToken: cancellationToken).ConfigureAwait(false);

// after
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

- [ ] **Step 5: Update the sort in `GetSnapshotAsync`**

```csharp
// before
var sort = Builders<AggregatorDocument>.Sort
    .Ascending(x => x.InsertedAtTicks)
    .Ascending(x => x.Id);

// after
var sort = Builders<AggregatorDocument>.Sort
    .Ascending(x => x.InsertedAtTicks)
    .Ascending(x => x.InsertSequence)
    .Ascending(x => x.Id);  // final tie-break for cross-process ties
```

- [ ] **Step 6: Add the compound index in `EnsureIndexesAsync`**

```csharp
// inside EnsureIndexesAsync, alongside the existing nameIndex and nameCorrelationIndex:
var nameInsertOrderIndex = new CreateIndexModel<AggregatorDocument>(
    Builders<AggregatorDocument>.IndexKeys
        .Ascending(x => x.Name)
        .Ascending(x => x.InsertedAtTicks)
        .Ascending(x => x.InsertSequence));

await _collection.Indexes.CreateManyAsync(
    [nameIndex, nameInsertOrderIndex, nameCorrelationIndex], cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 7: Write the E2E round-trip test**

Create `src/ServiceConnect.EndToEndTests/Persistence/MongoDbAggregatorInsertOrderTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbAggregatorInsertOrderTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    public sealed class OrderTestMessage
    {
        public int Sequence { get; set; }
    }

    private MongoDbAggregatorPersistor BuildPersistor(string dbName, TimeProvider timeProvider)
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var typeRegistry = new Mock<IMessageTypeRegistry>();
        Type? outType = typeof(OrderTestMessage);
        typeRegistry.Setup(r => r.TryResolve("ServiceConnect.EndToEndTests.MongoDbAggregatorInsertOrderTests+OrderTestMessage", out outType))
            .Returns(true);

        return new MongoDbAggregatorPersistor(
            client,
            options,
            NullLogger<MongoDbAggregatorPersistor>.Instance,
            typeRegistry.Object,
            timeProvider);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task GetSnapshot_TwoInsertsAtSameTick_PreservesInsertionOrder()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var dbName = _fixture.GetUniqueDatabaseName("aggorder");
        var persistor = BuildPersistor(dbName, clock);

        // Both inserts share the same tick (clock not advanced).
        await persistor.InsertDataAsync(new OrderTestMessage { Sequence = 1 }, "test-name");
        await persistor.InsertDataAsync(new OrderTestMessage { Sequence = 2 }, "test-name");

        var snapshot = await persistor.GetSnapshotAsync("test-name");
        var sequences = snapshot.ResolvedMessages.Cast<OrderTestMessage>().Select(m => m.Sequence).ToArray();

        Assert.Equal(new[] { 1, 2 }, sequences);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task GetSnapshot_HighFrequencyInserts_PreservesInsertionOrder()
    {
        var dbName = _fixture.GetUniqueDatabaseName("aggorder2");
        var persistor = BuildPersistor(dbName, TimeProvider.System);

        for (int i = 1; i <= 100; i++)
        {
            await persistor.InsertDataAsync(new OrderTestMessage { Sequence = i }, "test-name");
        }

        var snapshot = await persistor.GetSnapshotAsync("test-name");
        var sequences = snapshot.ResolvedMessages.Cast<OrderTestMessage>().Select(m => m.Sequence).ToArray();

        Assert.Equal(Enumerable.Range(1, 100).ToArray(), sequences);
    }
}
```

- [ ] **Step 8: Run unit + E2E tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbAggregatorPersistorTests" -m:1
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~MongoDbAggregatorInsertOrderTests" -m:1
```

Expected: all pass.

- [ ] **Step 9: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs \
        src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs \
        src/ServiceConnect.EndToEndTests/Persistence/MongoDbAggregatorInsertOrderTests.cs
git commit -m "fix(mongo-aggregator): InsertSequence tie-break for InsertedAtTicks ties (M36)"
```

(Co-Authored-By trailer.)

---

## Task 11: M37 — Distinguish structural mismatch from concurrency in `RemoveDataAsync`

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:170-197`.
- Modify: `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs`.
- Create: `src/ServiceConnect.EndToEndTests/Persistence/MongoDbAggregatorRemoveDataDistinctionTests.cs`.

- [ ] **Step 1: Write the failing unit tests**

In `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs`:

```csharp
[Fact]
public async Task RemoveData_NoRowsForName_ThrowsKeyNotFoundException()
{
    var (persistor, collection) = BuildPersistorWithCollection();
    collection.Setup(c => c.DeleteOneAsync(It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new DeleteResult.Acknowledged(0));
    collection.Setup(c => c.CountDocumentsAsync(
            It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
            It.IsAny<CountOptions?>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(0);

    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        persistor.RemoveDataAsync("missing-name", Guid.NewGuid()));
}

[Fact]
public async Task RemoveData_RowsForNameButNoCorrelation_ThrowsConcurrencyException()
{
    var (persistor, collection) = BuildPersistorWithCollection();
    collection.Setup(c => c.DeleteOneAsync(It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(new DeleteResult.Acknowledged(0));
    collection.Setup(c => c.CountDocumentsAsync(
            It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
            It.IsAny<CountOptions?>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(5);

    var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
        persistor.RemoveDataAsync("test-name", Guid.NewGuid()));

    Assert.Contains("5 row", ex.Message);
}
```

The `BuildPersistorWithCollection` helper is the canonical Mongo aggregator test arrange — adapt from existing test patterns.

- [ ] **Step 2: Run the tests pre-fix**

Expected: 1 FAIL (the KeyNotFoundException test — pre-fix throws ConcurrencyException), 1 passes-but-message-mismatch (the ConcurrencyException test — pre-fix has no row count in message).

- [ ] **Step 3: Apply the M37 fix**

In `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`, replace the post-DeleteOne block in `RemoveDataAsync`:

```csharp
// before
if (result.IsAcknowledged && result.DeletedCount == 0)
{
    throw new ConcurrencyException(
        $"Aggregator row not found: Name='{name}', CorrelationId='{correlationId}'. Row was concurrently removed or caller passed a mismatched key.");
}

// after
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

- [ ] **Step 4: Write the E2E round-trip tests**

Create `src/ServiceConnect.EndToEndTests/Persistence/MongoDbAggregatorRemoveDataDistinctionTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbAggregatorRemoveDataDistinctionTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    public sealed class TestPayload
    {
        public Guid CorrelationId { get; set; }
    }

    private MongoDbAggregatorPersistor BuildPersistor(string dbName)
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var typeRegistry = new Mock<IMessageTypeRegistry>();
        Type? outType = typeof(TestPayload);
        typeRegistry.Setup(r => r.TryResolve(It.IsAny<string>(), out outType)).Returns(true);
        return new MongoDbAggregatorPersistor(
            client, options, NullLogger<MongoDbAggregatorPersistor>.Instance, typeRegistry.Object);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RemoveData_NoRowsForName_ThrowsKeyNotFoundException()
    {
        var dbName = _fixture.GetUniqueDatabaseName("removedist1");
        var persistor = BuildPersistor(dbName);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            persistor.RemoveDataAsync("never-existed", Guid.NewGuid()));
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RemoveData_NameExistsButNoCorrelation_ThrowsConcurrencyException()
    {
        var dbName = _fixture.GetUniqueDatabaseName("removedist2");
        var persistor = BuildPersistor(dbName);

        await persistor.InsertDataAsync(new TestPayload { CorrelationId = Guid.NewGuid() }, "shared-name");

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            persistor.RemoveDataAsync("shared-name", Guid.NewGuid()));

        Assert.Contains("row(s) exist for this Name", ex.Message);
    }
}
```

- [ ] **Step 5: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbAggregatorPersistorTests" -m:1
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~MongoDbAggregatorRemoveDataDistinctionTests" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs \
        src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs \
        src/ServiceConnect.EndToEndTests/Persistence/MongoDbAggregatorRemoveDataDistinctionTests.cs
git commit -m "fix(mongo-aggregator): distinguish KeyNotFoundException from ConcurrencyException in RemoveData (M37)"
```

(Co-Authored-By trailer.)

---

## Task 12: M38 — Per-instance index cache flag

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`.
- Modify: `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs`.

- [ ] **Step 1: Write the failing tests**

In `src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs`:

```csharp
[Fact]
public async Task EnsureIndexes_CalledTwice_OnlyHitsCreateManyAsyncOnce()
{
    var (persistor, collection, indexes) = BuildPersistorWithIndexCapture();

    var data = new { Value = 1 };
    await persistor.InsertDataAsync(data, "test-name");
    await persistor.InsertDataAsync(data, "test-name");

    indexes.Verify(m => m.CreateManyAsync(
        It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
        It.IsAny<CancellationToken>()), Times.Once);
}

[Fact]
public async Task EnsureIndexes_BenignConflict85_FlipsCacheFlag()
{
    var (persistor, _, indexes) = BuildPersistorWithIndexCapture();
    var connectionId = new MongoDB.Driver.Core.Connections.ConnectionId(
        new MongoDB.Driver.Core.Servers.ServerId(
            new MongoDB.Driver.Core.Clusters.ClusterId(),
            new System.Net.DnsEndPoint("localhost", 27017)));
    var result = new BsonDocument { ["ok"] = 0, ["code"] = 85, ["errmsg"] = "options conflict" };
    var command = new BsonDocument { ["createIndexes"] = "Aggregator" };
    indexes.SetupSequence(m => m.CreateManyAsync(
            It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
            It.IsAny<CancellationToken>()))
        .ThrowsAsync(new MongoCommandException(connectionId, "options conflict", command, result))
        .ReturnsAsync(["ok"]); // second call, if it happens

    var data = new { Value = 1 };
    await persistor.InsertDataAsync(data, "test-name");
    await persistor.InsertDataAsync(data, "test-name");

    indexes.Verify(m => m.CreateManyAsync(
        It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
        It.IsAny<CancellationToken>()), Times.Once);
}

[Fact]
public async Task EnsureIndexes_NonBenignError_LeavesFlagUnflipped()
{
    var (persistor, _, indexes) = BuildPersistorWithIndexCapture();
    var connectionId = new MongoDB.Driver.Core.Connections.ConnectionId(
        new MongoDB.Driver.Core.Servers.ServerId(
            new MongoDB.Driver.Core.Clusters.ClusterId(),
            new System.Net.DnsEndPoint("localhost", 27017)));
    var result = new BsonDocument { ["ok"] = 0, ["code"] = 13, ["errmsg"] = "unauthorized" };
    var command = new BsonDocument { ["createIndexes"] = "Aggregator" };
    indexes.Setup(m => m.CreateManyAsync(
            It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
            It.IsAny<CancellationToken>()))
        .ThrowsAsync(new MongoCommandException(connectionId, "unauthorized", command, result));

    var data = new { Value = 1 };
    await Assert.ThrowsAsync<PersistenceException>(() => persistor.InsertDataAsync(data, "test-name"));
    await Assert.ThrowsAsync<PersistenceException>(() => persistor.InsertDataAsync(data, "test-name"));

    // Flag should NOT have flipped — both calls retry the index creation.
    indexes.Verify(m => m.CreateManyAsync(
        It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
        It.IsAny<CancellationToken>()), Times.Exactly(2));
}
```

`BuildPersistorWithIndexCapture` returns the tuple `(persistor, collection, indexes)` for assertion.

- [ ] **Step 2: Run pre-fix**

Expected: `EnsureIndexes_CalledTwice_OnlyHitsCreateManyAsyncOnce` FAILS (current code calls CreateManyAsync twice). The other two tests' shapes match pre-fix behaviour (no caching means each call retries) so they pass; post-fix they should still pass with the new caching semantics.

- [ ] **Step 3: Apply the M38 fix**

In `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`:

Add field:

```csharp
private int _indexed; // 0 = not yet ensured, 1 = ensured (success or benign conflict)
```

Replace `EnsureIndexesAsync`:

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

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbAggregatorPersistorTests" -m:1
```

Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs \
        src/ServiceConnect.UnitTests/MongoDbAggregatorPersistorTests.cs
git commit -m "perf(mongo-aggregator): cache _indexed flag after first success (M38)"
```

(Co-Authored-By trailer.)

---

## Task 13: L24 — Cert callback bounds check

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Configuration/MongoClientFactory.cs:77`.
- Create: `src/ServiceConnect.UnitTests/MongoClientFactoryCertCallbackTests.cs`.

**Background.** The callback dereferences `certificates[0]` without bounds check. If the collection is null or empty (driver edge case), NRE/IndexOutOfRangeException. Fall back to the server-supplied `certificate` parameter.

- [ ] **Step 1: Write the failing tests**

Create `src/ServiceConnect.UnitTests/MongoClientFactoryCertCallbackTests.cs`:

```csharp
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MongoDB.Driver;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoClientFactoryCertCallbackTests
{
    private static LocalCertificateSelectionCallback BuildCallback()
    {
        // Capture the callback by constructing an SSL-enabled client. We only need the
        // SslSettings.ClientCertificateSelectionCallback, not a working connection.
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            DatabaseName = "test",
            Ssl = new MongoDbSslOptions
            {
                CertPath = CreateTempPemPath(),
                CertPassphrase = null,
                AllowInsecureTls = true,
            },
        };

        // The factory loads the cert eagerly via GetOrLoadCertificate; substitute the loader
        // with one that returns a dummy in-memory cert so the test doesn't depend on real PEM
        // contents.
        var dummyCert = CreateDummyCertificate();
        MongoClientFactory.CertLoader = (_, _) => dummyCert;

        try
        {
            var client = MongoClientFactory.Create(options);
            return client.Settings.SslSettings!.ClientCertificateSelectionCallback;
        }
        finally
        {
            // Restore default loader to avoid bleeding into other tests.
            typeof(MongoClientFactory)
                .GetProperty("CertLoader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .SetValue(null,
                    (Func<string, string?, X509Certificate2>)((path, pass) =>
                        throw new InvalidOperationException("Test cleanup: real loader should not run.")));
        }
    }

    [Fact]
    public void CertificateSelectionCallback_NullCertificates_FallsBackToCertificateParam()
    {
        var callback = BuildCallback();
        var fallbackCert = CreateDummyCertificate();

        var result = callback.Invoke(this, "host", null!, fallbackCert, []);

        Assert.Same(fallbackCert, result);
    }

    [Fact]
    public void CertificateSelectionCallback_EmptyCertificates_FallsBackToCertificateParam()
    {
        var callback = BuildCallback();
        var fallbackCert = CreateDummyCertificate();
        var emptyCollection = new X509CertificateCollection();

        var result = callback.Invoke(this, "host", emptyCollection, fallbackCert, []);

        Assert.Same(fallbackCert, result);
    }

    [Fact]
    public void CertificateSelectionCallback_NonEmptyCertificates_ReturnsFirst()
    {
        var callback = BuildCallback();
        var firstCert = CreateDummyCertificate();
        var collection = new X509CertificateCollection { firstCert };

        var result = callback.Invoke(this, "host", collection, CreateDummyCertificate(), []);

        Assert.Same(firstCert, result);
    }

    private static X509Certificate2 CreateDummyCertificate()
    {
        // Generate a minimal self-signed cert in-memory for the test.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest("CN=test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
    }

    private static string CreateTempPemPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sc-test-{Guid.NewGuid()}.pem");
        File.WriteAllText(path, "dummy");
        return path;
    }
}
```

The test approach: substitute `MongoClientFactory.CertLoader` (an `internal` test seam already present at line 21 of `MongoClientFactory.cs`) with a dummy loader, build the SSL client, extract the callback, invoke with various inputs.

- [ ] **Step 2: Run tests pre-fix**

Expected: `CertificateSelectionCallback_NullCertificates_FallsBackToCertificateParam` and `CertificateSelectionCallback_EmptyCertificates_FallsBackToCertificateParam` FAIL (NRE / IndexOutOfRange). The non-empty test passes pre-fix.

- [ ] **Step 3: Apply the L24 fix**

In `src/ServiceConnect.Persistence.MongoDb/Configuration/MongoClientFactory.cs:77`:

```csharp
// before
ssl.ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0];

// after
ssl.ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) =>
    certificates is { Count: > 0 } ? certificates[0] : certificate;
```

- [ ] **Step 4: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoClientFactoryCertCallbackTests" -m:1
```

Expected: 3/3 pass.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/Configuration/MongoClientFactory.cs \
        src/ServiceConnect.UnitTests/MongoClientFactoryCertCallbackTests.cs
git commit -m "fix(mongo-cert): bounds-check cert callback; fall back to server cert (L24)"
```

(Co-Authored-By trailer.)

---

## Task 14: Smaller — count clamp comment + collection-name sanitization + cert cache passphrase verification

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs:247`.
- Modify: `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs:343-348`.
- Modify (potentially): `src/ServiceConnect.Persistence.MongoDb/Configuration/MongoClientFactory.cs` if the passphrase verification surfaces an actual gap.
- Modify: `src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderTests.cs` (or new file) — sanitization tests.

- [ ] **Step 1: Add count clamp comment**

In `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs`, around line 247:

```csharp
// before
return count > int.MaxValue ? int.MaxValue : (int)count;

// after
// Clamp at int.MaxValue to match IAggregatorPersistor's int return contract. Aggregators
// are keyed by (Name, CorrelationId) and rarely exceed a few hundred rows in normal usage;
// the clamp guards against pathological cases without changing the contract.
return count > int.MaxValue ? int.MaxValue : (int)count;
```

- [ ] **Step 2: Add collection-name sanitization**

In `src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs`:

```csharp
// before
private static string GetCollectionName<T>() where T : class, IProcessManagerData
    => typeof(T).FullName ?? typeof(T).Name;

private static string GetCollectionName(IProcessManagerData data)
{
    var t = data.GetType();
    return t.FullName ?? t.Name;
}

// after
// Mongo collection names containing +`[], from generic type names break tooling
// (mongosh autocomplete, mongo-express, etc.). Replace those characters with '_'
// so the collection name is portable. Existing v7 deployments with non-generic
// saga types are unaffected; v8 deployments with generic saga types must rename
// their existing collection (see release notes).
[GeneratedRegex(@"[+`\[\],]", RegexOptions.None)]
private static partial Regex CollectionNameSanitizerRegex();

internal static string SanitizeCollectionName(string raw)
    => CollectionNameSanitizerRegex().Replace(raw, "_");

private static string GetCollectionName<T>() where T : class, IProcessManagerData
    => SanitizeCollectionName(typeof(T).FullName ?? typeof(T).Name);

private static string GetCollectionName(IProcessManagerData data)
{
    var t = data.GetType();
    return SanitizeCollectionName(t.FullName ?? t.Name);
}
```

The class declaration must be `partial`:

```csharp
public sealed partial class MongoDbProcessManagerFinder : IProcessManagerFinder
```

Add `using System.Text.RegularExpressions;` to the top of the file. The `[GeneratedRegex]` attribute is supported on net8+/net10.

- [ ] **Step 3: Add a unit test for the sanitizer**

In `src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderTests.cs` (or new file):

```csharp
[Theory]
[InlineData("Foo.Bar.Baz", "Foo.Bar.Baz")]                                       // non-generic, no change
[InlineData("Foo.Generic`1[[Bar.Baz, MyAssembly]]", "Foo.Generic_1__Bar.Baz_ MyAssembly__")]  // adjust to actual replace shape
[InlineData("Foo+Nested", "Foo_Nested")]                                          // nested type
public void SanitizeCollectionName_ReplacesIllegalChars(string raw, string expected)
{
    var result = MongoDbProcessManagerFinder.SanitizeCollectionName(raw);
    Assert.Equal(expected, result);
}
```

NOTE: the exact `expected` strings depend on the specific regex pattern. Run the test to find the actual replacement and update; don't pre-guess.

- [ ] **Step 4: Verify cert cache passphrase rotation**

Read `src/ServiceConnect.Persistence.MongoDb/Configuration/MongoClientFactory.cs:85-95`:

```bash
sed -n '85,95p' src/ServiceConnect.Persistence.MongoDb/Configuration/MongoClientFactory.cs
```

Confirm the cache key is `path + "\0" + (passphrase ?? string.Empty)`. If yes, the rotation case is already handled (different passphrase produces different key → different `Lazy` slot → fresh load). **No change needed.** Document in commit body.

If the cache key does NOT include the passphrase, fix it:

```csharp
var cacheKey = path + "\0" + (passphrase ?? string.Empty);
```

- [ ] **Step 5: Run tests**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Mongo" -m:1
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs \
        src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs \
        src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderTests.cs
git commit -m "$(cat <<'EOF'
fix(mongo)!: sanitize generic collection names; clamp comment; verify cert cache

- Aggregator count clamp gets a clarifying comment (no behaviour change).
- ProcessManagerFinder collection names sanitize +`[], chars (illegal in mongosh /
  mongo-express tooling). Existing v7 generic-saga deployments must rename their
  existing collections — see release notes.
- MongoClientFactory cert cache passphrase rotation: verified existing code keys
  by passphrase; no change needed.
EOF
)"
```

(Co-Authored-By trailer; `!` for the generic-saga rename break.)

---

## Task 15: Phase 9 release notes

**Files:**
- Modify: `website/src/content/docs/releases.mdx`.

- [ ] **Step 1: Locate prior phase entry**

```bash
grep -n "### Mongo timeout\|### Bus, dispatcher\|### Phase\|^## " website/src/content/docs/releases.mdx | head -10
```

Phase 8 was added as `### Mongo timeout-store + lease guards`. Place Phase 9 immediately after.

- [ ] **Step 2: Insert the Phase 9 section**

Add this section, matching the heading-style of prior phases (no emojis):

```mdx
### Mongo aggregator + process-manager + serializer

**Bug fixes**

- **Saga property-hierarchy queries handle type coercion.** `MongoDbProcessManagerFinder.FindDataAsync` now wraps the message-side property value in `Expression.Convert` against the saga property's declared type. Previously, a message-side `int` against a saga-side `long` (or `Nullable<T>`, or interface-typed) silently missed.
- **Guid serializer registration fails loudly on conflict.** `EnsureGuidSerializerRegistered` now throws `InvalidOperationException` if another component has registered a different Guid serializer first. Pre-fix, the registration was silently skipped while the aggregator's filter literals still used `GuidRepresentation.Standard` — causing reads to silently miss writes.
- **Saga store rejects unacknowledged WriteConcern at startup.** `MongoDbProcessManagerFinder` now throws `InvalidOperationException` if the supplied `IMongoClient` has `WriteConcern.Unacknowledged` (w:0). Pre-fix, the persistor logged a Warning and disabled concurrency guards — silently advancing the version on missed updates and wedging sagas on the next real conflict.
- **CorrelationId unique index pre-created at startup.** A new `IHostedService` enumerates registered saga data types via the new `IProcessManagerTypeRegistry` and pre-creates the unique CorrelationId index for each before any consumer host begins polling. Closes the cross-process startup race that admitted duplicate saga rows.
- **Aggregator `RemoveDataAsync` distinguishes structural from concurrency errors.** `KeyNotFoundException` is now thrown when no rows exist for the given Name (caller error / cleanup race); `ConcurrencyException` retains its meaning of "rows for Name exist but not for this CorrelationId" (genuine race). The `ConcurrencyException` message includes the row count.
- **Aggregator `InsertSequence` tie-break.** Inserts sharing an `InsertedAtTicks` value (e.g., two messages within a single `DateTime.Tick`) now sort in actual insertion order via a per-process monotonic counter. Cross-process ties remain unsolved; per-process correctness matches the actual aggregator usage pattern.
- **Aggregator catches `BsonException` in the persistence contract.** `BsonSerializationException` (and other `BsonException` subtypes) now wrap as `PersistenceException` rather than escaping to the caller raw.
- **Aggregator `EnsureIndexesAsync` cached after first success.** Index creation runs once per persistor instance instead of on every operation. Round-trip per message on the hot path is eliminated.
- **Constructors null-check the logger.** Both `MongoDbAggregatorPersistor` and `MongoDbProcessManagerFinder` now throw `ArgumentNullException` on null `logger`.
- **Cert selection callback bounds-checked.** `MongoClientFactory.ClientCertificateSelectionCallback` now falls back to the server-supplied certificate parameter when the client-cert collection is null or empty. Pre-fix it dereferenced `[0]` and threw `NullReferenceException` / `IndexOutOfRangeException`.
- **Generic saga collection names sanitized.** Mongo collection names produced from `typeof(T).FullName` for generic saga types contained `+`, backticks, `[`, `]`, `,` — illegal in mongosh autocomplete and various Mongo tooling. v8 sanitizes these to `_`. **Migration required**: deployments with generic saga types must rename existing collections to the sanitized form before upgrading.
- **Internal hygiene.** `MongoDbProcessManagerFinder._indexCreationSemaphore` no longer disposed (mirrors Connection / Producer / Bus pattern).

**Behaviour changes**

- **`WriteConcern.Unacknowledged` rejected at startup.** Existing deployments using w:0 with the saga store break loudly. Configure `mongoClient.Settings.WriteConcern` to `WriteConcern.W1` or higher.
- **Conflicting Guid serializer registration rejected at startup.** Existing deployments with a custom Guid serializer registered before ServiceConnect break loudly. Either skip your existing registration or register `GuidRepresentation.Standard` to match.
- **Generic saga collection names changed.** Deployments with generic saga data types had data in collection names containing `+`/backtick/`[`/`]`/`,`. v8 reads from sanitized names. **Manual migration required**: rename existing collections via `db.runCommand({ renameCollection: "old", to: "new" })`.

**New surface**

- `IProcessManagerTypeRegistry` (in `ServiceConnect.Interfaces`) — enumerates registered saga data types. Implemented by `ProcessManagerHandlerRegistry`. Used by Mongo's index initializer; downstream persistence providers can consume the same enumeration.
```

- [ ] **Step 3: Build the website**

```bash
npm --prefix website run build
```

Expected: clean (pre-existing `/404.html` warning is unrelated).

- [ ] **Step 4: Commit**

```bash
git add website/src/content/docs/releases.mdx
git commit -m "docs(website): phase 09 release notes"
```

(Co-Authored-By trailer.)

---

## Task 16: API reference + learn updates

**Files (locate first; then update only what's relevant):**
- `website/src/content/docs/reference/configuration/...`
- `website/src/content/docs/reference/process-managers/...`
- `website/src/content/docs/reference/extension-points/persistence/...`
- `examples/ProcessManager/README.md`

- [ ] **Step 1: Locate relevant pages**

```bash
ls website/src/content/docs/reference/configuration/ 2>/dev/null
ls website/src/content/docs/reference/process-managers/ 2>/dev/null
ls website/src/content/docs/reference/extension-points/persistence/ 2>/dev/null
grep -rln "MongoDbPersistenceOptions\|WriteConcern\|MongoDbProcessManagerFinder\|saga\|aggregator\|GuidRepresentation" website/src/content/docs/ examples/ README.md 2>/dev/null | head -20
```

- [ ] **Step 2: Update the configuration reference**

Add the WriteConcern requirement and Guid representation contract to the MongoDB configuration page:

```mdx
### MongoDB persistence requirements (v8)

- **WriteConcern**: ServiceConnect's saga store requires acknowledged writes (`WriteConcern.W1` or higher). Configuring `WriteConcern.Unacknowledged` (w:0) on the `IMongoClient` causes `MongoDbProcessManagerFinder` to throw `InvalidOperationException` at construction time.
- **Guid representation**: ServiceConnect requires `GuidRepresentation.Standard` (UUID subtype 4). The persistence layer sets `BsonDefaults.GuidRepresentationMode = V3` and registers the Guid serializer at startup. If another component has already registered a different Guid serializer, ServiceConnect throws `InvalidOperationException` with an actionable message — configure your driver initialisation to either skip Guid serializer registration or pre-register `GuidRepresentation.Standard`.
- **Startup-time index creation**: A hosted service (`MongoDbProcessManagerIndexInitializer`) pre-creates the unique CorrelationId index for each registered saga type at startup. This closes the cross-process race window where two cold-started processes could admit duplicate saga rows.
```

- [ ] **Step 3: Update the saga store contract page**

In `website/src/content/docs/reference/extension-points/persistence/iprocessmanagerfinder.mdx` (or equivalent):

```mdx
### Saga store contract

- **WriteConcern.Unacknowledged is rejected at startup.** Saga state is correctness-sensitive; w:0 silently loses concurrent updates and wedges sagas on the next real conflict.
- **CorrelationId is unique per saga data type.** Mongo enforces uniqueness via a unique index pre-created at startup. Concurrent inserts of the same CorrelationId result in `PersistenceException` (wrapping the Mongo duplicate-key error) for all but one caller.
- **Property-hierarchy queries coerce types.** `FindDataAsync` wraps the message-side value in `Expression.Convert` against the saga property's declared type, so `int`-vs-`long`, `Nullable<T>`, and interface-typed-property mappings all resolve correctly.
- **Generic saga collection names are sanitized.** Mongo collection names produced from `typeof(T).FullName` strip the characters `+ ` backtick `[ ] ,` — replacing each with `_`. Deployments with generic saga types must rename their existing collections via `db.runCommand({ renameCollection: "old", to: "new" })` before upgrading to v8.
```

- [ ] **Step 4: Update aggregator reference**

In `website/src/content/docs/reference/extension-points/persistence/iaggregatorpersistor.mdx` (or equivalent):

```mdx
### Insertion order semantics

`GetSnapshotAsync` returns documents in insertion order within a single process: same-tick ties are broken by a per-process monotonic `InsertSequence` counter. Cross-process ties (two inserts at the same tick from different processes) tie-break by `Id` (random Guid) — undefined ordering. In the actual aggregator usage pattern (one consumer per `(Name, CorrelationId)`), per-process order matches the global order.

### `RemoveDataAsync` exception contract

- `KeyNotFoundException` — no rows exist for the given `Name`. Indicates caller error or that the rows were removed via `RemoveAllAsync` by another path.
- `ConcurrencyException` — rows for the Name exist, but none with the given `CorrelationId`. Indicates a race or mismatched key.
- `PersistenceException` — wraps any `MongoException` or `BsonException` from the underlying driver.
```

- [ ] **Step 5: Verify examples / READMEs**

```bash
grep -rn "WriteConcern\|new MongoDbProcessManagerFinder\|new MongoDbAggregatorPersistor\|UseMongoDbPersistence" examples/ README.md 2>/dev/null
```

If any sample sets `WriteConcern.Unacknowledged` for sagas, update it (the v8 contract rejects it). If any sample uses a generic saga data type, document the rename step (or call out the affected example).

- [ ] **Step 6: Build the website + commit**

```bash
npm --prefix website run build
```

Expected: clean.

```bash
git add website/src/content/docs/ examples/ 2>/dev/null
git commit -m "docs(website): saga store contract, WriteConcern + Guid + generic-saga rename"
```

(Co-Authored-By trailer.)

---

## Task 17: Final verification gate + code review

This task is mechanical. Runs all verification commands and dispatches the final code reviewer over Phase 9 commits.

- [ ] **Step 1: Per-csproj builds clean**

```bash
dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj -m:1
dotnet build src/ServiceConnect/ServiceConnect.csproj -m:1
dotnet build src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj -m:1
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Focused unit-test pass**

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Mongo" -m:1
```

Expected: all pass.

- [ ] **Step 3: E2E test pass (Docker required)**

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~Mongo&FullyQualifiedName~Persistence" -m:1
```

Expected: all pass.

- [ ] **Step 4: Astro build**

```bash
npm --prefix website run build
```

Expected: clean.

- [ ] **Step 5: Grep verifications**

```bash
# H6: EnsureGuidSerializerRegistered no longer swallows BsonSerializationException
grep -n "BsonSerializationException" src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs
# Expected: at least one occurrence inside a `throw new InvalidOperationException(...)` block.

# H10: Expression.Convert in property-hierarchy queries
grep -n "Expression.Convert" src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs
# Expected: at least one occurrence in FindDataAsync.

# H25: hosted service registered
grep -n "AddHostedService<MongoDbProcessManagerIndexInitializer>" src/ServiceConnect.Persistence.MongoDb/MongoDbPersistenceExtensions.cs
# Expected: 1 hit.

# H26: _concurrencyGuardsEnabled gone
grep -n "_concurrencyGuardsEnabled" src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs
# Expected: zero hits.

# H30: semaphore comment present
grep -n "intentionally NOT Disposed" src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs
# Expected: 1 hit.

# M31: catch (BsonException) in aggregator
grep -nE "catch \(BsonException" src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs
# Expected: 6 hits (one per public method).

# M36: InsertSequence field
grep -n "InsertSequence" src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs
# Expected: at least 4 hits (field declaration, AggregatorDocument property, InsertOne assignment, sort, index).

# M38: _indexed flag
grep -n "_indexed" src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs
# Expected: declaration + Volatile.Read + Volatile.Write.

# Smaller — collection-name sanitizer
grep -n "SanitizeCollectionName\|CollectionNameSanitizerRegex" src/ServiceConnect.Persistence.MongoDb/ProcessManager/MongoDbProcessManagerFinder.cs
# Expected: at least 4 hits.
```

- [ ] **Step 6: Final code review**

Dispatch `superpowers:code-reviewer` (model: opus) over all Phase 9 commits (from `e196f1e9` — the spec commit — through HEAD). Briefing:

```
Review Phase 9 commits (e196f1e9..HEAD) against
docs/superpowers/specs/2026-05-02-phase-09-mongo-aggregator-procmgr-design.md.

Focus on:
- H6: EnsureGuidSerializerRegistered throws InvalidOperationException on
  BsonSerializationException; the volatile flag stays at 0 so retries are
  possible.
- H10: Expression.Convert is wrapped around the runtime-typed Constant in
  FindDataAsync; the saga property's declared type drives the conversion.
- H25: IProcessManagerTypeRegistry exposes saga data types from
  ProcessManagerHandlerRegistry; MongoDbProcessManagerIndexInitializer is
  registered as IHostedService and pre-creates indexes; lazy fallback is
  retained.
- H26: WriteConcern.Unacknowledged → InvalidOperationException at construction;
  _concurrencyGuardsEnabled field + branches removed; UpdateData and DeleteData
  guards run unconditionally.
- H30: semaphore intentionally-not-disposed comment matches Connection /
  Producer / Bus convention.
- M31: every catch (MongoException) site has a sibling catch (BsonException)
  before it.
- M36: InsertSequence assigned via Interlocked.Increment; sort is
  (InsertedAtTicks, InsertSequence, Id); compound index covers the sort.
- M37: RemoveData throws KeyNotFoundException when Name has zero rows;
  ConcurrencyException message names the row count.
- M38: _indexed cache flag uses Volatile.Read/Write; benign-conflict catch
  also flips the flag.
- L24: cert callback handles null and empty certificates collection.
- Smaller: collection-name sanitizer regex covers + ` [ ] , ; SanitizeCollectionName
  is internal; the count-clamp comment is accurate.

Flag: any test that locks in pre-fix behaviour, any race condition the spec
did not anticipate, any public-API surface change beyond IProcessManagerTypeRegistry,
any inconsistency between commit messages and what shipped.
```

- [ ] **Step 7: Cleanup commit (only if review surfaced issues)**

If steps 5-6 found anything actionable, fix in a follow-up commit:

```bash
git add <only-cleanup-files>
git commit -m "cleanup(phase-09): address final-review findings"
```

If nothing needed, skip — Phase 9 is done.

---

## Phase 9 done

All findings closed. The phase ships:

- H6 EnsureGuidSerializerRegistered fail-fast on conflict.
- H10 Expression.Convert for saga property-hierarchy queries.
- H25 startup-time index creation via IHostedService + IProcessManagerTypeRegistry.
- H26 reject WriteConcern.Unacknowledged at startup.
- H30 semaphore rely-on-GC pattern.
- M31 BsonException catch sibling on aggregator.
- M32 null-logger guards.
- M36 InsertSequence tie-break.
- M37 KeyNotFoundException vs ConcurrencyException distinction.
- M38 index-creation cache flag.
- L24 cert callback bounds check.
- Smaller items: collection-name sanitization, count clamp comment, cert cache verification.

Move to writing the closing summary; the user's standard pattern is "phase complete; continue to phase N+1?".
