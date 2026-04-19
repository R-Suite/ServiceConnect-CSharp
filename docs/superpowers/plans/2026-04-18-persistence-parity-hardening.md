# Persistence Parity Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align process-manager persistence behavior across the in-memory and MongoDB backends so inserts mean inserts, duplicate detection matches across stores, and in-memory reads no longer leak live persisted state.

**Architecture:** Preserve the existing persistence interfaces while tightening backend semantics behind them. Add regression tests first, then implement minimal internal changes in each finder so duplicate handling and mutation isolation behave the same across both stores.

**Tech Stack:** C#, .NET 8/10, xUnit, Moq, MongoDB.Driver

---

## File Map

- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs`
  Detach returned data and keep update semantics explicit.
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs`
  Make insert semantics reject duplicates instead of upserting.
- Test: `src/ServiceConnect.UnitTests/InMemoryProcessManagerFinderTests.cs`
- Test: `src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderTests.cs`
- Test: `src/ServiceConnect.UnitTests/Processors/ProcessManagerProcessorTests.cs`

### Task 1: Make MongoDB Duplicate Inserts Fail

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs:157-172`
- Test: `src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderTests.cs`

- [ ] **Step 1: Add a failing Mongo duplicate-insert test**

In `MongoDbProcessManagerFinderTests`, add a test that proves insert uses `InsertOneAsync` and wraps duplicate errors as `PersistenceException`.

```csharp
[Fact]
public async Task InsertDataAsync_ThrowsPersistenceException_WhenDuplicateCorrelationIdExists()
{
    var finder = CreateFinder(out var database, out _);
    var collection = new Mock<IMongoCollection<MongoDbData<TestProcessManagerData>>>();
    var data = new TestProcessManagerData();

    database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
            nameof(TestProcessManagerData),
            It.IsAny<MongoCollectionSettings>()))
        .Returns(collection.Object);

    collection.Setup(c => c.InsertOneAsync(
            It.IsAny<MongoDbData<TestProcessManagerData>>(),
            It.IsAny<InsertOneOptions>(),
            It.IsAny<CancellationToken>()))
        .ThrowsAsync(new TestMongoException("duplicate"));

    await Assert.ThrowsAsync<PersistenceException>(() => finder.InsertDataAsync(data, CancellationToken.None));
}
```

- [ ] **Step 2: Run the focused Mongo duplicate-insert test to verify it fails**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~MongoDbProcessManagerFinderTests.InsertDataAsync_ThrowsPersistenceException_WhenDuplicateCorrelationIdExists"`
Expected: FAIL because `InsertDataTypedAsync` currently calls `ReplaceOneAsync(..., IsUpsert = true)`.

- [ ] **Step 3: Replace upsert behavior with insert behavior**

Change `InsertDataTypedAsync<T>` to create the document and call `InsertOneAsync` instead of `ReplaceOneAsync`.

```csharp
private async Task InsertDataTypedAsync<T>(T data, string collectionName, CancellationToken cancellationToken)
    where T : class, IProcessManagerData
{
    var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
    await EnsureCorrelationIdIndexAsync(collection).ConfigureAwait(false);

    var mongoDbData = new MongoDbData<T>
    {
        Data = data,
        Version = 1,
        Id = Guid.NewGuid()
    };

    await collection.InsertOneAsync(mongoDbData, cancellationToken: cancellationToken).ConfigureAwait(false);
}
```

- [ ] **Step 4: Run the focused Mongo tests to verify they pass**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~MongoDbProcessManagerFinderTests.InsertDataAsync_ThrowsPersistenceException_WhenDuplicateCorrelationIdExists|FullyQualifiedName~MongoDbProcessManagerFinderTests.UpdateDataAsync_RestoresOriginalVersion_WhenReplaceFails|FullyQualifiedName~MongoDbProcessManagerFinderTests.InsertDataAsync_UnwrapsTargetInvocationException"`
Expected: PASS with all targeted Mongo tests green.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Persistence.MongoDb/MongoDbProcessManagerFinder.cs src/ServiceConnect.UnitTests/MongoDbProcessManagerFinderTests.cs
git commit -m "fix: make mongodb process-manager inserts fail on duplicates"
```

### Task 2: Return Detached Copies From In-Memory Finder Reads

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs:86-120`
- Test: `src/ServiceConnect.UnitTests/InMemoryProcessManagerFinderTests.cs`

- [ ] **Step 1: Add a failing test proving returned data is detached**

In `InMemoryProcessManagerFinderTests`, add a test that mutates a loaded process manager without calling `UpdateDataAsync` and verifies the stored state remains unchanged.

```csharp
[Fact]
public async Task FindDataAsync_ReturnsDetachedCopy_AndDoesNotLeakMutationsWithoutUpdate()
{
    IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
    IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "Original" };
    await finder.InsertDataAsync(data, CancellationToken.None);

    var loaded = await finder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None);
    Assert.NotNull(loaded);

    ((TestData)loaded!.Data).Name = "Mutated";

    var loadedAgain = await finder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None);
    Assert.Equal("Original", ((TestData)loadedAgain!.Data).Name);
}
```

- [ ] **Step 2: Run the focused in-memory test to verify it fails**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~InMemoryProcessManagerFinderTests.FindDataAsync_ReturnsDetachedCopy_AndDoesNotLeakMutationsWithoutUpdate"`
Expected: FAIL because `FindMatchingItem` currently returns the live `typed.Data` reference.

- [ ] **Step 3: Add a minimal clone helper inside the in-memory finder**

Implement a private helper that deep-copies the process-manager data object before returning it from `FindMatchingItem`.

```csharp
private static T CloneData<T>(T data) where T : class, IProcessManagerData
{
    var clone = Activator.CreateInstance<T>();
    foreach (var property in typeof(T).GetProperties().Where(p => p.CanRead && p.CanWrite))
    {
        var value = property.GetValue(data);
        property.SetValue(clone, value is byte[] bytes ? bytes.Clone() : value);
    }
    return clone;
}
```

Use it when constructing the returned `MemoryData<T>` instance.

```csharp
var candidate = new MemoryData<T> { Data = CloneData(typed.Data), Version = typed.Version };
```

- [ ] **Step 4: Run the focused in-memory tests to verify they pass**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~InMemoryProcessManagerFinderTests.FindDataAsync_ReturnsDetachedCopy_AndDoesNotLeakMutationsWithoutUpdate|FullyQualifiedName~InMemoryProcessManagerFinderTests.ShouldUpdateData|FullyQualifiedName~InMemoryProcessManagerFinderTests.ShouldThrowWhenUpdatingTwoInstancesOfSameDataAtTheSameTime"`
Expected: PASS with all three tests green.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.Persistence.InMemory/InMemoryProcessManagerFinder.cs src/ServiceConnect.UnitTests/InMemoryProcessManagerFinderTests.cs
git commit -m "fix: detach in-memory process-manager reads"
```

### Task 3: Prove Failed Handlers Do Not Leak In-Memory Mutations

**Files:**
- Test: `src/ServiceConnect.UnitTests/Processors/ProcessManagerProcessorTests.cs`

- [ ] **Step 1: Add a failing processor regression test**

In `ProcessManagerProcessorTests`, add a handler that mutates the loaded data and then throws. Re-read via the in-memory finder and assert the mutation did not persist.

```csharp
[Fact]
public async Task ProcessAsync_WhenHandlerMutatesAndThrows_DoesNotLeakMutationIntoInMemoryStore()
{
    var finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
    var existing = new PmMutableData { CorrelationId = Guid.NewGuid(), Counter = 5 };
    await finder.InsertDataAsync(existing, CancellationToken.None);

    var services = new ServiceCollection();
    services.AddSingleton<IBus>(new Mock<IBus>().Object);
    services.AddSingleton<IProcessManagerFinder>(finder);
    services.AddSingleton<IProcessHandler<PmMutableData, PmMutableMessage>>(new PmMutatingThrowingHandler());

    var registry = BuildRegistry(new HandlerReference { MessageType = typeof(PmMutableMessage), HandlerType = typeof(PmMutatingThrowingHandler) });
    var provider = services.BuildServiceProvider();
    var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig);

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
        processor.ProcessAsync(new byte[] { 1 }, typeof(PmMutableMessage), new PmMutableMessage(existing.CorrelationId), new Dictionary<string, object>(), new Envelope()));

    var mapper = new TestProcessManagerPropertyMapper();
    mapper.ConfigureMapping<PmMutableData, PmMutableMessage>(d => d.CorrelationId, m => m.CorrelationId);
    var reloaded = await finder.FindDataAsync<PmMutableData>(mapper, new PmMutableMessage(existing.CorrelationId), CancellationToken.None);

    Assert.Equal(5, reloaded!.Data.Counter);
}
```

- [ ] **Step 2: Run the focused processor regression test to verify it fails**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~ProcessManagerProcessorTests.ProcessAsync_WhenHandlerMutatesAndThrows_DoesNotLeakMutationIntoInMemoryStore"`
Expected: FAIL before the detached-read change is in place, or PASS only after Task 2 is implemented.

- [ ] **Step 3: Add any missing helper types required by the test**

Keep them local to `ProcessManagerProcessorTests.cs`.

```csharp
file class PmMutableMessage : Message
{
    public PmMutableMessage(Guid correlationId) : base(correlationId) { }
}

file class PmMutableData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public int Counter { get; set; }
}

file class PmMutatingThrowingHandler : IProcessHandler<PmMutableData, PmMutableMessage>
{
    public IConsumeContext? Context { get; set; }
    public void ConfigureMapper(IProcessManagerPropertyMapper mapper) => mapper.ConfigureMapping<PmMutableData, PmMutableMessage>(d => d.CorrelationId, m => m.CorrelationId);
    public Task HandleAsync(PmMutableMessage message, PmMutableData data)
    {
        data.Counter++;
        throw new InvalidOperationException("handler failure");
    }
}
```

- [ ] **Step 4: Re-run the focused processor test to verify it passes**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~ProcessManagerProcessorTests.ProcessAsync_WhenHandlerMutatesAndThrows_DoesNotLeakMutationIntoInMemoryStore|FullyQualifiedName~ProcessManagerProcessorTests.ProcessAsync_HandlerThrows_InsertDataAsyncNotCalled"`
Expected: PASS with both processor tests green.

- [ ] **Step 5: Commit**

```bash
git add src/ServiceConnect.UnitTests/Processors/ProcessManagerProcessorTests.cs
git commit -m "test: cover in-memory process-manager mutation isolation"
```

### Task 4: Run The Persistence Verification Suite

**Files:**
- No code changes expected

- [ ] **Step 1: Run the persistence-focused unit tests**

Run: `rtk dotnet test "src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj" --filter "FullyQualifiedName~InMemoryProcessManagerFinderTests|FullyQualifiedName~MongoDbProcessManagerFinderTests|FullyQualifiedName~ProcessManagerProcessorTests"`
Expected: PASS with all persistence-focused tests green.

- [ ] **Step 2: Run a wider build for the three touched projects**

Run: `rtk dotnet build "src/ServiceConnect.Persistence.InMemory/ServiceConnect.Persistence.InMemory.csproj" -f net8.0 && rtk dotnet build "src/ServiceConnect.Persistence.MongoDb/ServiceConnect.Persistence.MongoDb.csproj" -f net8.0 && rtk dotnet build "src/ServiceConnect/ServiceConnect.csproj" -f net8.0`
Expected: PASS for all three builds.

- [ ] **Step 3: Commit verification-only follow-up if needed**

If no files changed, do not create an empty commit.
