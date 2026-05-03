# Phase A.1 — v8 Foundations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land three independent v8 foundation changes — introduce `IHasCorrelationId`, make `TimeoutData.Destination` nullable, and clean up `IAggregatorPersistor`'s parameter types and unused-ctor convention — all behind a green build and full passing test suite.

**Architecture:** This is the first of three phases delivering Group A of the v8 public-API tightening (see [v8 design spec](../specs/2026-05-03-v8-public-api-tightening-design.md)). The three changes here are independent and ordered for atomic commits: foundations land first because everything in Phases A.2 and A.3 will assume them. Each commit produces a working, testable build.

**Tech Stack:** .NET 8/10, C# 12/14, xUnit, Moq, Microsoft.Extensions.Time.Testing. Dotnet wrapper at `~/.local/bin/dotnet` enforces cgroup limits — call `dotnet` normally; do NOT use `/usr/lib/dotnet/dotnet` directly.

---

## File Structure

**New files:**
- `src/ServiceConnect.Interfaces/Aggregation/IHasCorrelationId.cs` — single-property interface for aggregator data and the new typed contract for everything that has a `Guid CorrelationId`.

**Files modified (production):**
- `src/ServiceConnect.Interfaces/Messages/Message.cs` — declare `: IHasCorrelationId`.
- `src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs` — `Destination` becomes `string?` with no default.
- `src/ServiceConnect.Interfaces/Aggregation/IAggregatorSnapshot.cs` — `ResolvedMessages` becomes `IReadOnlyList<IHasCorrelationId>`.
- `src/ServiceConnect.Interfaces/Aggregation/AggregatorSnapshot.cs` — record positional parameter type matches the interface.
- `src/ServiceConnect.Interfaces/Aggregation/IAggregatorPersistor.cs` — `InsertDataAsync(IHasCorrelationId data, ...)`; `GetDataAsync` returns `IReadOnlyList<IHasCorrelationId>`.
- `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs` — delete `GetCorrelationId` + `CorrelationIdAccessors`; constructor loses unused string parameters; method signatures + `Entry` record updated; direct `data.CorrelationId` access via interface.
- `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs` — DI registration drops the empty-string arguments.
- `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs` — `InsertDataAsync` parameter type tightened; `GetSnapshotAsync` deserialises into `IHasCorrelationId` (BSON `Deserialize` returns `object`, cast at the boundary).

**Files modified (tests):**
- `src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorTests.cs` — every constructor call drops the three empty strings; `ThirdPartyDto` implements `IHasCorrelationId`; one new test confirming `Message : IHasCorrelationId`.
- `src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorConcurrencyTests.cs` — every constructor call drops the three empty strings.
- `src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorUnresolvedCountTests.cs` — every constructor call drops the three empty strings; `Payload` implements `IHasCorrelationId`.
- `src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorLockHoldTests.cs` — every constructor call drops the three empty strings.

**Files deleted:**
- `src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorAccessorTests.cs` — exercises the reflection-fallback path that no longer exists.

**Out of scope for this phase:**
- `IMessageSerializer` redesign and STJ migration — Phase A.2.
- All `IProducer` / `IBus` / `IConsumer` / `IMessageDispatcher` / `IConsumeContext` shape changes — Phases A.2 and A.3.
- `DeepClone.cs` Newtonsoft usage — internal in-memory persistence helper, unrelated to the message serializer migration.

---

## Task 1: Introduce `IHasCorrelationId` interface

**Files:**
- Create: `src/ServiceConnect.Interfaces/Aggregation/IHasCorrelationId.cs`

- [ ] **Step 1: Create the interface file**

```csharp
namespace ServiceConnect.Interfaces;

/// <summary>
/// A correlation-id carrier. Implemented by <see cref="Message"/> and by any
/// aggregator data type that stores a per-message correlation key.
/// </summary>
/// <remarks>
/// Replaces the v7 reflection-based discovery of a public <c>Guid CorrelationId</c>
/// property. Aggregator persistors require this interface on stored data so they
/// can locate entries by correlation id without per-type reflection.
/// </remarks>
public interface IHasCorrelationId
{
    /// <summary>The correlation identifier carried by the implementing instance.</summary>
    Guid CorrelationId { get; }
}
```

- [ ] **Step 2: Build the Interfaces project to confirm it compiles**

Run: `dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj`
Expected: build succeeds, 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/ServiceConnect.Interfaces/Aggregation/IHasCorrelationId.cs
git commit -m "$(cat <<'EOF'
feat(interfaces): introduce IHasCorrelationId

Single-property contract for any type that carries a Guid CorrelationId.
Aggregator persistors switch to this contract in a follow-up commit so they
can locate stored entries by correlation id without per-type reflection.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `Message` implements `IHasCorrelationId`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Messages/Message.cs`
- Test: `src/ServiceConnect.UnitTests/InterfaceCleanupTests.cs` (existing file — add one fact)

- [ ] **Step 1: Add the implementation declaration**

Edit `src/ServiceConnect.Interfaces/Messages/Message.cs`. Change the class declaration:

```csharp
// Before:
public class Message(Guid correlationId)
{
    public Guid CorrelationId { get; init; } = correlationId;
}

// After:
public class Message(Guid correlationId) : IHasCorrelationId
{
    public Guid CorrelationId { get; init; } = correlationId;
}
```

The `Guid CorrelationId { get; init; }` already satisfies `IHasCorrelationId.Guid CorrelationId { get; }`; no body changes needed.

- [ ] **Step 2: Add a test that `Message` is assignable to `IHasCorrelationId`**

Open `src/ServiceConnect.UnitTests/InterfaceCleanupTests.cs` (it already exists — confirm path with `ls src/ServiceConnect.UnitTests/InterfaceCleanupTests.cs`; if it doesn't, create it as below).

Add this test method to the existing class (or to a new file at the same path with a `public class InterfaceCleanupTests` if it must be created):

```csharp
[Fact]
public void Message_ImplementsIHasCorrelationId_AndExposesCorrelationIdViaInterface()
{
    var corrId = Guid.NewGuid();
    var message = new Message(corrId);

    IHasCorrelationId asInterface = message;

    Assert.Equal(corrId, asInterface.CorrelationId);
}
```

If you needed to create the file, the full skeleton is:

```csharp
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class InterfaceCleanupTests
{
    [Fact]
    public void Message_ImplementsIHasCorrelationId_AndExposesCorrelationIdViaInterface()
    {
        var corrId = Guid.NewGuid();
        var message = new Message(corrId);

        IHasCorrelationId asInterface = message;

        Assert.Equal(corrId, asInterface.CorrelationId);
    }
}
```

- [ ] **Step 3: Build and run the new test**

Run: `dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj`
Expected: succeeds.

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InterfaceCleanupTests.Message_ImplementsIHasCorrelationId" --no-restore`
Expected: 1 test passed.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Interfaces/Messages/Message.cs src/ServiceConnect.UnitTests/InterfaceCleanupTests.cs
git commit -m "$(cat <<'EOF'
feat(interfaces)!: Message implements IHasCorrelationId

Existing Guid CorrelationId { get; init; } property satisfies the
interface contract; no body change required. Anything deriving from
Message picks up the interface automatically.

BREAKING CHANGE: callers cannot opt out — Message-derived aggregator
data types are now IHasCorrelationId. Custom non-Message aggregator data
types must implement IHasCorrelationId explicitly (see Phase A.1 plan).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `TimeoutData.Destination` becomes `string?`

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs:16`

- [ ] **Step 1: Edit the property declaration**

Edit `src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs`:

```csharp
// Before (line 16):
public string Destination { get; set; } = string.Empty;

// After:
public string? Destination { get; set; }
```

Also update the XML doc above to mention nullability:

```csharp
/// <summary>
/// The address of the client who requested the timeout, or <see langword="null"/>
/// when no destination is associated with the timeout.
/// </summary>
public string? Destination { get; set; }
```

- [ ] **Step 2: Build the entire solution to surface any null-handling regressions**

Run: `dotnet build src/ServiceConnect.sln`
Expected: succeeds, 0 errors, 0 warnings. (`TreatWarningsAsErrors=true` will fail the build on any newly-introduced CS8602/CS8604/etc. nullability warning at a `Destination` use site.)

If the build fails on a nullability warning, find the warning's file:line, add a guard: `if (data.Destination is null) { ... }` or use the null-coalescing operator. Re-run.

- [ ] **Step 3: Run the timeout-related tests**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~TimeoutStore | FullyQualifiedName~TimeoutData | FullyQualifiedName~MongoDbTimeoutStore" --no-restore`
Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add src/ServiceConnect.Interfaces/Timeouts/TimeoutData.cs
# Plus any null-guard fixes the build surfaced; add those files explicitly too.
git commit -m "$(cat <<'EOF'
feat(interfaces)!: TimeoutData.Destination is nullable

Was: public string Destination { get; set; } = string.Empty;
Now: public string? Destination { get; set; }

Aligns with SendContext.EndPoint and TransportException.Endpoint, which
already type the same domain concept as string?. The empty-string sentinel
silently meant "no destination"; now it's expressed honestly.

BREAKING CHANGE: callers reading Destination must handle null.
Persistence layers treat null as "no destination set" — same semantics
the empty string had before.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Tighten `IAggregatorSnapshot` and `AggregatorSnapshot`

This task and Task 5 must land together with the impl updates (Tasks 6–10) because the build does not compile in intermediate states. Stage them in order, run the final build/test once at the end, and commit as one logical unit (Task 10's commit covers Tasks 4–10).

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Aggregation/IAggregatorSnapshot.cs`
- Modify: `src/ServiceConnect.Interfaces/Aggregation/AggregatorSnapshot.cs`

- [ ] **Step 1: Tighten the interface**

Edit `src/ServiceConnect.Interfaces/Aggregation/IAggregatorSnapshot.cs`:

```csharp
// Before:
IReadOnlyList<object> ResolvedMessages { get; }

// After:
IReadOnlyList<IHasCorrelationId> ResolvedMessages { get; }
```

- [ ] **Step 2: Tighten the record**

Edit `src/ServiceConnect.Interfaces/Aggregation/AggregatorSnapshot.cs`:

```csharp
public sealed record AggregatorSnapshot(
    IReadOnlyList<IHasCorrelationId> ResolvedMessages,
    IReadOnlyList<Guid> ResolvedIds,
    int UnresolvedCount) : IAggregatorSnapshot
{
    public static AggregatorSnapshot Empty { get; } = new([], [], 0);
}
```

- [ ] **Step 3: Build the Interfaces project (other projects expected to break, that's fine for now)**

Run: `dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj`
Expected: succeeds.

(Do not commit yet — proceed to Task 5.)

---

## Task 5: Tighten `IAggregatorPersistor` interface

**Files:**
- Modify: `src/ServiceConnect.Interfaces/Aggregation/IAggregatorPersistor.cs:14, 22`

- [ ] **Step 1: Tighten `InsertDataAsync` and `GetDataAsync` signatures**

Edit `src/ServiceConnect.Interfaces/Aggregation/IAggregatorPersistor.cs`:

```csharp
// Before (line 14):
Task InsertDataAsync(object data, string name, CancellationToken cancellationToken = default);

// After:
Task InsertDataAsync(IHasCorrelationId data, string name, CancellationToken cancellationToken = default);
```

```csharp
// Before (line 22):
Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default);

// After:
Task<IReadOnlyList<IHasCorrelationId>> GetDataAsync(string name, CancellationToken cancellationToken = default);
```

Update the matching XML `<param>` for `InsertDataAsync` to mention "implementations of <see cref="IHasCorrelationId"/>".

`RemoveDataAsync(string name, Guid correlationId, ...)` is **unchanged** — it already takes a `Guid`, not a data object.

- [ ] **Step 2: Build the Interfaces project**

Run: `dotnet build src/ServiceConnect.Interfaces/ServiceConnect.Interfaces.csproj`
Expected: succeeds. (Mongo and InMemory persistor projects will fail to build now; expected.)

(Do not commit yet — proceed to Task 6.)

---

## Task 6: Update `InMemoryAggregatorPersistor`

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs` — large rewrite of the top section.

- [ ] **Step 1: Delete the reflection helpers**

In `InMemoryAggregatorPersistor.cs`, delete lines 15–47 (the `CorrelationIdAccessors` static field comment, the field, the entire `GetCorrelationId(object data)` method).

- [ ] **Step 2: Simplify the constructor — drop unused string parameters**

Replace lines 49–57:

```csharp
// Before:
// Parameters required by IAggregatorPersistor factory convention but unused in InMemory implementation
/// <summary>
/// Initializes a new <see cref="InMemoryAggregatorPersistor"/> instance.
/// </summary>
public InMemoryAggregatorPersistor(string connectionString, string databaseName, string collectionName, TimeProvider? timeProvider = null)
{
    _timeProvider = timeProvider ?? TimeProvider.System;
    _provider = new CacheProvider(_timeProvider);
}

// After:
/// <summary>
/// Initializes a new <see cref="InMemoryAggregatorPersistor"/> instance.
/// </summary>
/// <param name="timeProvider">Time source used by the underlying cache provider; defaults to <see cref="TimeProvider.System"/>.</param>
public InMemoryAggregatorPersistor(TimeProvider? timeProvider = null)
{
    _timeProvider = timeProvider ?? TimeProvider.System;
    _provider = new CacheProvider(_timeProvider);
}
```

- [ ] **Step 3: Tighten `Entry` record's `Data` field type to nullable interface**

Replace line 64:

```csharp
// Before:
private sealed record Entry(Guid Id, object Data);

// After:
// Data is nullable because InMemoryAggregatorPersistorUnresolvedCountTests reflects in
// a null-Data Entry to exercise the GetSnapshotAsync unresolved-count branch. The public
// Insert path always supplies a non-null IHasCorrelationId.
private sealed record Entry(Guid Id, IHasCorrelationId? Data);
```

- [ ] **Step 4: Update `InsertDataAsync` signature and impl**

Replace the existing method:

```csharp
public Task InsertDataAsync(IHasCorrelationId data, string name, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(data);
    // Deep-clone before storing so later caller mutations do not bleed into the
    // buffer. Retrieval does the same on the outbound side.
    var stored = DeepClone.Clone(data);
    lock (_memoryCacheLock)
    {
        var list = GetOrCreateEntries(name);
        list.Add(new Entry(Guid.NewGuid(), stored));
    }
    return Task.CompletedTask;
}
```

`DeepClone.Clone<T>(T value)` returns `T`, so `Clone(data)` returns `IHasCorrelationId` directly — no cast needed.

- [ ] **Step 5: Update `GetDataAsync` return type**

Replace the existing method:

```csharp
public Task<IReadOnlyList<IHasCorrelationId>> GetDataAsync(string name, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    lock (_memoryCacheLock)
    {
        if (!_provider.TryGet<string, object>(name, out var sourceObj) || sourceObj is not List<Entry> source)
        {
            return Task.FromResult<IReadOnlyList<IHasCorrelationId>>([]);
        }
        var copy = new List<IHasCorrelationId>(source.Count);
        foreach (var entry in source)
        {
            // is-pattern narrows to a non-null local — DeepClone.Clone's `where T : notnull`
            // constraint is satisfied. Skip null-Data entries (only producible by the
            // reflection-based unresolved-count test); the public surface never inserts null.
            if (entry.Data is { } data)
            {
                copy.Add(DeepClone.Clone(data));
            }
        }

        return Task.FromResult<IReadOnlyList<IHasCorrelationId>>(copy);
    }
}
```

- [ ] **Step 6: Update `GetSnapshotAsync` to build a snapshot of `IHasCorrelationId`**

Inside `GetSnapshotAsync`, change the locals' types and the snapshot construction:

```csharp
// Inside the existing method, replace the relevant block:

var messages = new List<IHasCorrelationId>(entriesCopy.Length);
var ids = new List<Guid>(entriesCopy.Length);
var unresolved = 0;
foreach (var entry in entriesCopy)
{
    // is-pattern narrows to non-null for DeepClone's `where T : notnull` constraint.
    if (entry.Data is { } data)
    {
        messages.Add(DeepClone.Clone(data));
        ids.Add(entry.Id);
    }
    else
    {
        unresolved++;
    }
}
return Task.FromResult<IAggregatorSnapshot>(new AggregatorSnapshot(messages, ids, unresolved));
```

(`entry.Data is null` stays as a defence even though `Entry.Data` is now non-nullable, because `InMemoryAggregatorPersistorUnresolvedCountTests` reflects-in a null-Data Entry to exercise the unresolved path.)

- [ ] **Step 7: Update `RemoveDataAsync` to use direct interface access**

Inside `RemoveDataAsync`, replace the reflection-based correlation-id lookup:

```csharp
// Before (line 157):
if (GetCorrelationId(list[index].Data) is { } id && id == correlationId)

// After:
// Null-conditional handles the test-only reflection-injected null Data; production
// inserts never produce null.
if (list[index].Data?.CorrelationId == correlationId)
```

- [ ] **Step 8: Build the InMemory persistence project**

Run: `dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj`
Expected: succeeds.

(Do not commit yet — proceed to Task 7.)

---

## Task 7: Update `InMemoryPersistenceExtensions` DI registration

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs:40-41`

- [ ] **Step 1: Remove the unused string arguments from the registration**

Edit `src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs`:

```csharp
// Before (lines 40-41):
services.TryAddSingleton<IAggregatorPersistor>(sp =>
    new InMemoryAggregatorPersistor("", "", "", sp.GetRequiredService<TimeProvider>()));

// After:
services.TryAddSingleton<IAggregatorPersistor>(sp =>
    new InMemoryAggregatorPersistor(sp.GetRequiredService<TimeProvider>()));
```

- [ ] **Step 2: Build to confirm**

Run: `dotnet build src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj`
Expected: succeeds.

(Do not commit yet — proceed to Task 8.)

---

## Task 8: Update `MongoDbAggregatorPersistor`

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs` — tighten `InsertDataAsync`'s parameter type, cast deserialised data at the boundary inside `GetSnapshotAsync` and `GetDataAsync`.

- [ ] **Step 1: Tighten `InsertDataAsync` parameter type**

Edit lines 94–95:

```csharp
// Before:
public async Task InsertDataAsync(object data, string name, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(data);

    try
    {
        await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

        var dataType = data.GetType();
        var dataBson = data.ToBsonDocument(dataType);

// After:
public async Task InsertDataAsync(IHasCorrelationId data, string name, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(data);

    try
    {
        await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

        var dataType = data.GetType();
        var dataBson = data.ToBsonDocument(dataType);
```

The `ToBsonDocument` extension still works against a typed argument (the runtime type comes from `GetType()`).

- [ ] **Step 2: Update `GetSnapshotAsync` to cast deserialised data to `IHasCorrelationId`**

Inside `GetSnapshotAsync` (around lines 153–185), change the messages list type and the deserialise call:

```csharp
// Replace:
var messages = new List<object>(docs.Count);
// ...
messages.Add(BsonSerializer.Deserialize(doc.DataBson, type));

// With:
var messages = new List<IHasCorrelationId>(docs.Count);
// ...
var deserialised = BsonSerializer.Deserialize(doc.DataBson, type);
if (deserialised is not IHasCorrelationId withCorrId)
{
    _logger.LogWarning(
        "Aggregator document {Id} of type '{TypeName}' does not implement IHasCorrelationId; counting as unresolved",
        doc.Id, doc.DataTypeName);
    unresolved++;
    continue;
}
messages.Add(withCorrId);
ids.Add(doc.Id);
```

The `ids.Add(doc.Id)` line that previously sat after `messages.Add(...)` moves into the success branch (the inner `if` already counts unresolved; the original code added to both lists unconditionally after the `messages.Add`).

Verify the surrounding `try`/`catch` block still wraps the new code; the `BsonException`/`FormatException` catch is now positioned around `BsonSerializer.Deserialize` and the cast, which is correct.

- [ ] **Step 3: Update `GetDataAsync` return type**

Lines 129–134:

```csharp
// Before:
public async Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default)
{
    var snapshot = await GetSnapshotAsync(name, cancellationToken).ConfigureAwait(false);
    return [.. snapshot.ResolvedMessages];
}

// After:
public async Task<IReadOnlyList<IHasCorrelationId>> GetDataAsync(string name, CancellationToken cancellationToken = default)
{
    var snapshot = await GetSnapshotAsync(name, cancellationToken).ConfigureAwait(false);
    return [.. snapshot.ResolvedMessages];
}
```

- [ ] **Step 4: Build the MongoDb persistence project**

Run: `dotnet build src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj`
Expected: succeeds.

(Do not commit yet — proceed to Task 9.)

---

## Task 9: Update unit tests for the aggregator persistor

**Files:**
- Modify: `src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorTests.cs` — multiple constructor calls + `ThirdPartyDto`.
- Modify: `src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorConcurrencyTests.cs` — multiple constructor calls.
- Modify: `src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorUnresolvedCountTests.cs` — multiple constructor calls + `Payload`.
- Modify: `src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorLockHoldTests.cs` — constructor calls.
- Delete: `src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorAccessorTests.cs`.

- [ ] **Step 1: Update `InMemoryAggregatorPersistorTests.cs` constructor calls**

Run a project-aware replace: every `new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty)` becomes `new InMemoryAggregatorPersistor()`. Every `new InMemoryAggregatorPersistor("", "", "")` becomes `new InMemoryAggregatorPersistor()`. Every `new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty, timeProvider)` becomes `new InMemoryAggregatorPersistor(timeProvider)`.

The simplest reliable approach: open the file in your editor and find/replace, then verify with a final grep:

```bash
grep -n 'new InMemoryAggregatorPersistor(' src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorTests.cs
```

Expected: every match is `new InMemoryAggregatorPersistor()` or `new InMemoryAggregatorPersistor(timeProvider)`.

- [ ] **Step 2: Make `ThirdPartyDto` implement `IHasCorrelationId`**

In `InMemoryAggregatorPersistorTests.cs` (around line 341):

```csharp
// Before:
private sealed class ThirdPartyDto
{
    public Guid CorrelationId { get; init; }
    public string Payload { get; init; } = string.Empty;
}

// After:
private sealed class ThirdPartyDto : IHasCorrelationId
{
    public Guid CorrelationId { get; init; }
    public string Payload { get; init; } = string.Empty;
}
```

- [ ] **Step 3: Update `InMemoryAggregatorPersistorConcurrencyTests.cs` constructor calls**

Same find/replace as Step 1, applied to this file.

```bash
grep -n 'new InMemoryAggregatorPersistor(' src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorConcurrencyTests.cs
```

Expected: every match is `new InMemoryAggregatorPersistor()`.

- [ ] **Step 4: Update `InMemoryAggregatorPersistorUnresolvedCountTests.cs` constructor calls and `Payload`**

Constructor replace as Steps 1/3.

Make `Payload` implement `IHasCorrelationId`:

```csharp
// Before:
private sealed class Payload
{
    public Guid CorrelationId { get; set; }
    public int X { get; set; }
}

// After:
private sealed class Payload : IHasCorrelationId
{
    public Guid CorrelationId { get; set; }
    public int X { get; set; }
}
```

(Note: `IHasCorrelationId.CorrelationId` is `{ get; }` only. The `set` here is a more-permissive implementation, which is allowed — the test mutates the property, which is fine for a class-level setter even though the interface only requires a getter.)

- [ ] **Step 5: Update `InMemoryAggregatorPersistorLockHoldTests.cs` constructor calls**

Apply the same constructor replace.

```bash
grep -n 'new InMemoryAggregatorPersistor(' src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorLockHoldTests.cs
```

Expected: every match is `new InMemoryAggregatorPersistor()`.

- [ ] **Step 6: Delete `InMemoryAggregatorPersistorAccessorTests.cs`**

This file specifically tests the reflection-fallback (`GetCorrelationId`) path that no longer exists. Per the spec, the reflection path is removed; the test file's premise is gone.

```bash
git rm src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorAccessorTests.cs
```

- [ ] **Step 7: Build the unit-tests project**

Run: `dotnet build src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj`
Expected: succeeds.

(Do not commit yet — proceed to Task 10.)

---

## Task 10: Run the full aggregator + persistence test suite, then commit Tasks 4–10

**Files:** all modified files from Tasks 4–9 plus the deleted accessor-tests file.

- [ ] **Step 1: Run the aggregator-related test suite**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~Aggregator" --no-restore`
Expected: every test passes; the suite includes all four `InMemoryAggregatorPersistor*Tests` classes plus all `MongoDbAggregatorPersistor*Tests`.

Sample expected counts from the test inventory (use as a sanity check; numbers may shift):
- InMemoryAggregatorPersistorTests: ~24 tests
- InMemoryAggregatorPersistorConcurrencyTests: ~5 tests
- InMemoryAggregatorPersistorUnresolvedCountTests: 2 tests
- InMemoryAggregatorPersistorLockHoldTests: 2 tests
- MongoDbAggregatorPersistor*Tests: ~30+ tests (BsonException, ConstructorTests, ForwardCompat, IndexCache, NullData, RemoveDataDistinction)

If any test fails, read the failure, fix the test or the impl, re-run before proceeding.

- [ ] **Step 2: Run the full unit-test suite**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore`
Expected: all tests pass. (Catches anything outside the aggregator sweep — interface compatibility, integration with `Bus`, etc.)

- [ ] **Step 3: Commit Tasks 4–10 as one atomic change**

```bash
git add \
  src/ServiceConnect.Interfaces/Aggregation/IAggregatorSnapshot.cs \
  src/ServiceConnect.Interfaces/Aggregation/AggregatorSnapshot.cs \
  src/ServiceConnect.Interfaces/Aggregation/IAggregatorPersistor.cs \
  src/ServiceConnect.Persistence.InMemory/Aggregator/InMemoryAggregatorPersistor.cs \
  src/ServiceConnect.Persistence.InMemory/InMemoryPersistenceExtensions.cs \
  src/ServiceConnect.Persistence.MongoDb/Aggregator/MongoDbAggregatorPersistor.cs \
  src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorTests.cs \
  src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorConcurrencyTests.cs \
  src/ServiceConnect.UnitTests/InMemoryAggregatorPersistorUnresolvedCountTests.cs \
  src/ServiceConnect.UnitTests/Persistence/InMemoryAggregatorPersistorLockHoldTests.cs

# Plus the deletion staged earlier:
# (git rm of the accessor test file is already in the index from Task 9 Step 6)

git commit -m "$(cat <<'EOF'
refactor(persistence)!: aggregator persistor uses IHasCorrelationId

IAggregatorPersistor.InsertDataAsync takes IHasCorrelationId;
GetDataAsync returns IReadOnlyList<IHasCorrelationId>; IAggregatorSnapshot
and AggregatorSnapshot match.

InMemoryAggregatorPersistor:
- drops the unused (connectionString, databaseName, collectionName) ctor
  parameters — they were a leftover from a former factory convention that
  Mongo's persistor never actually shared.
- deletes the per-type CorrelationIdAccessors reflection cache and the
  GetCorrelationId helper. Lookup is now direct: list[i].Data.CorrelationId.
- Entry record's Data field is typed as IHasCorrelationId.

MongoDbAggregatorPersistor:
- InsertDataAsync's parameter is tightened.
- GetSnapshotAsync casts each deserialised document to IHasCorrelationId
  and counts non-implementers as unresolved (with a warning log) so a
  forward-compat document of an older shape doesn't crash the snapshot.

InMemoryPersistenceExtensions drops the empty-string args from the
InMemoryAggregatorPersistor registration.

Tests:
- All InMemoryAggregatorPersistor* test files drop the empty-string ctor
  args.
- ThirdPartyDto and Payload (non-Message DTOs in tests) explicitly
  implement IHasCorrelationId.
- InMemoryAggregatorPersistorAccessorTests is deleted: it exclusively
  tests the reflection-fallback path that no longer exists.

BREAKING CHANGE: custom non-Message aggregator data types must implement
IHasCorrelationId. Anything deriving from Message picks it up
automatically (Message implements the interface as of an earlier commit
in this phase).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Final phase verification

**Files:** none (read-only verification).

- [ ] **Step 1: Full solution build**

Run: `dotnet build src/ServiceConnect.sln`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Full unit-test sweep**

Run: `dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --no-restore`
Expected: every test passes.

- [ ] **Step 3: Inspect the commit history**

Run: `git log --oneline v7-clean-architecture..HEAD`
Expected: four new commits — `feat(interfaces): introduce IHasCorrelationId`, `feat(interfaces)!: Message implements IHasCorrelationId`, `feat(interfaces)!: TimeoutData.Destination is nullable`, `refactor(persistence)!: aggregator persistor uses IHasCorrelationId`.

- [ ] **Step 4: Sanity-check the diff against the plan**

Run: `git diff --stat v7-clean-architecture..HEAD`
Expected: the changed-file list matches "Files modified" in the file structure section above. No surprises.

- [ ] **Step 5: Update phase status in the roadmap**

Edit `architecture-fix-plan.md`. In the "Group A — v8 public-API tightening" section, change the status from `brainstorming` to in-progress, and add a sub-bullet noting Phase A.1 is `done`. Commit.

```bash
git add architecture-fix-plan.md
git commit -m "docs(architecture): mark Phase A.1 done in the fix plan"
```

(Phase A.1 is now complete. Phases A.2 and A.3 are queued; come back when ready to brainstorm those.)

---

## Risks and rollback

- **Breaking change for custom aggregator data types**: anything not deriving from `Message` and not yet implementing `IHasCorrelationId` will fail to compile against v8. This is the intended outcome and is loud; users add a one-line interface declaration. Release notes (Group C, separate phase) call this out explicitly.
- **MongoDb forward-compat documents**: a stored aggregator document whose CLR type used to be a non-Message POCO without a `Guid CorrelationId` property would now also fail the `IHasCorrelationId` cast in `GetSnapshotAsync`. The change handles it as an unresolved row (warning logged, not exception) so the snapshot still surfaces the rest of the aggregator's messages.
- **Rollback** is `git revert` of the four commits in reverse order.
