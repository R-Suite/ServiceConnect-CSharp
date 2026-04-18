# Timeout Ownership Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix timeout claiming and lease ownership regressions so due timeouts are claimed when read, stale workers cannot release or delete another worker's lease, and `ProcessManagerTimeoutService` uses owner-aware cleanup when the store supports it.

**Architecture:** Three sequential tasks. First, add a narrow owner-aware timeout-store extension interface without changing `ITimeoutStore`. Second, make the in-memory and Mongo timeout stores implement the intended claim/lease behavior. Third, teach `ProcessManagerTimeoutService` to prefer the owner-aware path while preserving legacy fallback for any store that only implements `ITimeoutStore`.

**Tech Stack:** C# / .NET, xUnit, Moq, MongoDB.Driver, GitNexus CLI.

**Spec:** [docs/superpowers/specs/2026-04-18-branch-regression-remediation-design.md](../specs/2026-04-18-branch-regression-remediation-design.md)

---

## File Inventory

**Production:**
- `src/ServiceConnect.Interfaces/ILeaseAwareTimeoutStore.cs` — create narrow owner-aware extension interface
- `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs` — claim due rows on read and support lease expiry semantics
- `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs` — implement owner-aware release/remove methods
- `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs` — prefer the owner-aware interface when available

**Unit tests:**
- `src/ServiceConnect.UnitTests/InMemoryTimeoutStoreTests.cs` — extend claim/release behavior coverage
- `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreTests.cs` — add stale-owner and owner-match semantics
- `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs` — verify owner-aware preference and fallback

**E2E tests to re-run:**
- `src/ServiceConnect.EndToEndTests/ProcessManagerTimeoutTests.cs`
- `src/ServiceConnect.EndToEndTests/ProcessManagerMongoDbTests.cs`

---

## Task 1: Add A Narrow Owner-Aware Timeout Interface Without Changing `ITimeoutStore`

**Files:**
- Create: `src/ServiceConnect.Interfaces/ILeaseAwareTimeoutStore.cs`
- Modify: `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs`

- [ ] **Step 1: Create the owner-aware extension interface**

Create `src/ServiceConnect.Interfaces/ILeaseAwareTimeoutStore.cs` with this content:

```csharp
namespace ServiceConnect.Interfaces;

public interface ILeaseAwareTimeoutStore
{
    Task RemoveDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default);
    Task ReleaseDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Add failing hosted-service tests for owner-aware preference and fallback**

In `src/ServiceConnect.UnitTests/Services/ProcessManagerTimeoutServiceTests.cs`, add this test double near the bottom of the file:

```csharp
    private sealed class LeaseAwareTimeoutStoreDouble : ITimeoutStore, ILeaseAwareTimeoutStore
    {
        public TimeoutsBatch Batch { get; set; } = new() { DueTimeouts = [], NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30) };

        public int LegacyRemoveCalls { get; private set; }
        public int LegacyReleaseCalls { get; private set; }
        public int LeaseAwareRemoveCalls { get; private set; }
        public int LeaseAwareReleaseCalls { get; private set; }
        public Guid? LastOwnerForRemove { get; private set; }
        public Guid? LastOwnerForRelease { get; private set; }

        public Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default) => Task.FromResult(Batch);
        public Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
        {
            LegacyRemoveCalls++;
            return Task.CompletedTask;
        }

        public Task ReleaseDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
        {
            LegacyReleaseCalls++;
            return Task.CompletedTask;
        }

        public Task RemoveDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default)
        {
            LeaseAwareRemoveCalls++;
            LastOwnerForRemove = lockOwner;
            return Task.CompletedTask;
        }

        public Task ReleaseDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default)
        {
            LeaseAwareReleaseCalls++;
            LastOwnerForRelease = lockOwner;
            return Task.CompletedTask;
        }
    }
```

Then add these tests:

```csharp
    [Fact]
    public async Task PollOnce_LeaseAwareStore_UsesOwnerAwareRemoveOnSuccess()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);
        var owner = Guid.NewGuid();
        var store = new LeaseAwareTimeoutStoreDouble
        {
            Batch = new TimeoutsBatch
            {
                DueTimeouts =
                [
                    new TimeoutData
                    {
                        Id = Guid.NewGuid(),
                        ProcessManagerId = Guid.NewGuid(),
                        Destination = "test-queue",
                        Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                        Headers = new Dictionary<string, object>(),
                        Locked = true,
                        LockedBy = owner,
                        LockExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
                    }
                ],
                NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30)
            }
        };

        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.Is<SendOptions>(o => o.EndPoint == "test-queue"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(store);
        await sut.PollOnceAsync();

        Assert.Equal(1, store.LeaseAwareRemoveCalls);
        Assert.Equal(owner, store.LastOwnerForRemove);
        Assert.Equal(0, store.LegacyRemoveCalls);
    }

    [Fact]
    public async Task PollOnce_LeaseAwareStore_WithEmptyOwner_FallsBackToLegacyRemove()
    {
        _mockConfig.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);
        var store = new LeaseAwareTimeoutStoreDouble
        {
            Batch = new TimeoutsBatch
            {
                DueTimeouts =
                [
                    new TimeoutData
                    {
                        Id = Guid.NewGuid(),
                        ProcessManagerId = Guid.NewGuid(),
                        Destination = "test-queue",
                        Time = DateTimeOffset.UtcNow.AddMinutes(-1),
                        Headers = new Dictionary<string, object>(),
                        Locked = false,
                        LockedBy = Guid.Empty
                    }
                ],
                NextQueryTime = DateTimeOffset.UtcNow.AddSeconds(30)
            }
        };

        _mockBus.Setup(bus => bus.SendAsync(
                It.IsAny<TimeoutMessage>(),
                It.IsAny<SendOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(store);
        await sut.PollOnceAsync();

        Assert.Equal(1, store.LegacyRemoveCalls);
        Assert.Equal(0, store.LeaseAwareRemoveCalls);
    }
```

- [ ] **Step 3: Run the hosted-service tests to confirm they fail first**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProcessManagerTimeoutServiceTests"
```

Expected before production changes:
- tests fail because `ILeaseAwareTimeoutStore` does not exist in production code yet and `ProcessManagerTimeoutService` never prefers it

- [ ] **Step 4: Add the runtime cast in `ProcessManagerTimeoutService`**

In `src/ServiceConnect/Services/ProcessManagerTimeoutService.cs`, add a field next to `_finder`:

```csharp
    private readonly ILeaseAwareTimeoutStore? _leaseAwareFinder = finder as ILeaseAwareTimeoutStore;
```

Then replace the cleanup block inside `PollOnceAsync`:

```csharp
                    await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
                    if (_leaseAwareFinder != null && timeout.LockedBy != Guid.Empty)
                        await _leaseAwareFinder.RemoveDispatchedTimeoutAsync(timeout.Id, timeout.LockedBy, cancellationToken).ConfigureAwait(false);
                    else
                        await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, cancellationToken).ConfigureAwait(false);
```

And replace the release block:

```csharp
                        await _finder.ReleaseDispatchedTimeoutAsync(timeout.Id, cancellationToken).ConfigureAwait(false);
```

with:

```csharp
                        if (_leaseAwareFinder != null && timeout.LockedBy != Guid.Empty)
                            await _leaseAwareFinder.ReleaseDispatchedTimeoutAsync(timeout.Id, timeout.LockedBy, cancellationToken).ConfigureAwait(false);
                        else
                            await _finder.ReleaseDispatchedTimeoutAsync(timeout.Id, cancellationToken).ConfigureAwait(false);
```

- [ ] **Step 5: Re-run the hosted-service tests and confirm they pass**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~ProcessManagerTimeoutServiceTests"
```

Expected: all `ProcessManagerTimeoutServiceTests` pass.

## Task 2: Claim Due Timeouts In `InMemoryTimeoutStore`

**Files:**
- Modify: `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs`
- Modify: `src/ServiceConnect.UnitTests/InMemoryTimeoutStoreTests.cs`

- [ ] **Step 1: Add the failing in-memory claim tests**

In `src/ServiceConnect.UnitTests/InMemoryTimeoutStoreTests.cs`, add these tests:

```csharp
    [Fact]
    public async Task GetTimeoutsBatch_DueTimeoutClaimed_IsHiddenFromNextPollUntilReleased()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        var first = await store.GetTimeoutsBatchAsync();
        var claimed = Assert.Single(first.DueTimeouts);
        Assert.True(claimed.Locked);
        Assert.NotEqual(Guid.Empty, claimed.LockedBy);
        Assert.NotNull(claimed.LockExpiresAt);

        var second = await store.GetTimeoutsBatchAsync();
        Assert.Empty(second.DueTimeouts);
    }

    [Fact]
    public async Task GetTimeoutsBatch_ReleasedTimeout_IsReturnedAgainOnNextPoll()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        var first = await store.GetTimeoutsBatchAsync();
        var claimed = Assert.Single(first.DueTimeouts);
        await store.ReleaseDispatchedTimeoutAsync(claimed.Id);

        var second = await store.GetTimeoutsBatchAsync();
        Assert.Single(second.DueTimeouts);
    }
```

Also strengthen the existing `GetTimeoutsBatch_OnlyDueTimeouts_AreReturned` assertions by appending:

```csharp
        Assert.All(batch.DueTimeouts, timeout =>
        {
            Assert.True(timeout.Locked);
            Assert.NotEqual(Guid.Empty, timeout.LockedBy);
            Assert.NotNull(timeout.LockExpiresAt);
        });
```

- [ ] **Step 2: Run the in-memory timeout tests and confirm they fail first**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryTimeoutStoreTests"
```

Expected before the production change:
- the new tests fail because due timeouts are returned repeatedly and not claimed

- [ ] **Step 3: Claim due rows on read in `InMemoryTimeoutStore`**

In `src/ServiceConnect.Persistence.InMemory/InMemoryTimeoutStore.cs`:

1. Add a lease duration constant below `DefaultNextQueryInterval`:

```csharp
    private static readonly TimeSpan LockLeaseDuration = TimeSpan.FromMinutes(5);
```

2. Replace the entire `GetTimeoutsBatchAsync` method with:

```csharp
    public Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var retval = new TimeoutsBatch { DueTimeouts = [] };
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        var nextQueryTime = DateTimeOffset.MaxValue;
        var sessionId = Guid.NewGuid();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            foreach (var entry in _state.TimeoutIndex)
            {
                if (entry.Time > utcNow)
                {
                    nextQueryTime = entry.Time;
                    break;
                }

                bool leaseExpired = entry.Data.LockExpiresAt.HasValue && entry.Data.LockExpiresAt <= utcNow;
                bool eligible = !entry.Data.Locked || leaseExpired;
                if (!eligible)
                    continue;

                entry.Data.Locked = true;
                entry.Data.LockedBy = sessionId;
                entry.Data.LockExpiresAt = utcNow.Add(LockLeaseDuration);
                retval.DueTimeouts.Add(entry.Data);
            }
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        if (nextQueryTime == DateTimeOffset.MaxValue)
            nextQueryTime = utcNow.Add(DefaultNextQueryInterval);

        retval.NextQueryTime = nextQueryTime;
        return Task.FromResult(retval);
    }
```

- [ ] **Step 4: Re-run the in-memory timeout tests and confirm they pass**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~InMemoryTimeoutStoreTests"
```

Expected: all `InMemoryTimeoutStoreTests` pass.

## Task 3: Enforce Lease Ownership In `MongoDbTimeoutStore`

**Files:**
- Modify: `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs`
- Modify: `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreTests.cs`

- [ ] **Step 1: Add owner-aware Mongo tests**

In `src/ServiceConnect.UnitTests/MongoDbTimeoutStoreTests.cs`, add these tests below the current index-handling coverage. Use Moq to verify the filters passed to Mongo:

```csharp
    [Fact]
    public async Task RemoveDispatchedTimeout_WithMatchingOwner_DeletesLockedTimeout()
    {
        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.Setup(c => c.DeleteOneAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeleteResult.Acknowledged(1));

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null)).Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        var store = new MongoDbTimeoutStore(client.Object, new MongoDbPersistenceOptions { DatabaseName = "test" }, NullLogger<MongoDbTimeoutStore>.Instance);
        var owner = Guid.NewGuid();

        await ((ILeaseAwareTimeoutStore)store).RemoveDispatchedTimeoutAsync(Guid.NewGuid(), owner);

        collection.Verify(c => c.DeleteOneAsync(It.IsAny<FilterDefinition<TimeoutData>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
```

Also add these exact tests in the same file:

```csharp
    [Fact]
    public async Task ReleaseDispatchedTimeout_WithMatchingOwner_ClearsLockFields()
    {
        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateResult.Acknowledged(1, 1, null));

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null)).Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        var store = new MongoDbTimeoutStore(client.Object, new MongoDbPersistenceOptions { DatabaseName = "test" }, NullLogger<MongoDbTimeoutStore>.Instance);

        await ((ILeaseAwareTimeoutStore)store).ReleaseDispatchedTimeoutAsync(Guid.NewGuid(), Guid.NewGuid());

        collection.Verify(c => c.UpdateOneAsync(
            It.IsAny<FilterDefinition<TimeoutData>>(),
            It.IsAny<UpdateDefinition<TimeoutData>>(),
            It.IsAny<UpdateOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveDispatchedTimeout_WithStaleOwner_IsNoOp()
    {
        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.Setup(c => c.DeleteOneAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DeleteResult.Acknowledged(0));

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null)).Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        var store = new MongoDbTimeoutStore(client.Object, new MongoDbPersistenceOptions { DatabaseName = "test" }, NullLogger<MongoDbTimeoutStore>.Instance);

        var exception = await Record.ExceptionAsync(() =>
            ((ILeaseAwareTimeoutStore)store).RemoveDispatchedTimeoutAsync(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_WithStaleOwner_IsNoOp()
    {
        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateResult.Acknowledged(1, 0, null));

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null)).Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        var store = new MongoDbTimeoutStore(client.Object, new MongoDbPersistenceOptions { DatabaseName = "test" }, NullLogger<MongoDbTimeoutStore>.Instance);

        var exception = await Record.ExceptionAsync(() =>
            ((ILeaseAwareTimeoutStore)store).ReleaseDispatchedTimeoutAsync(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(exception);
    }
```

For the stale-owner tests, the zero-row `DeleteResult` / `UpdateResult` is the expected benign no-op outcome.

- [ ] **Step 2: Run the Mongo timeout tests and confirm the new ones fail first**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStoreTests"
```

Expected before the production change:
- the new owner-aware tests fail because the interface methods do not exist yet

- [ ] **Step 3: Implement `ILeaseAwareTimeoutStore` on `MongoDbTimeoutStore`**

In `src/ServiceConnect.Persistence.MongoDb/MongoDbTimeoutStore.cs`:

1. Change the class declaration:

```csharp
public sealed class MongoDbTimeoutStore : ITimeoutStore, ILeaseAwareTimeoutStore
```

2. Add these two methods below the legacy `RemoveDispatchedTimeoutAsync` / `ReleaseDispatchedTimeoutAsync` methods:

```csharp
    public async Task RemoveDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            var filter = Builders<TimeoutData>.Filter.Eq(x => x.Id, id) &
                         Builders<TimeoutData>.Filter.Eq(x => x.Locked, true) &
                         Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, lockOwner);
            await collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove dispatched timeout with Id '{id}' for owner '{lockOwner}'.", ex);
        }
    }

    public async Task ReleaseDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            var filter = Builders<TimeoutData>.Filter.Eq(x => x.Id, id) &
                         Builders<TimeoutData>.Filter.Eq(x => x.Locked, true) &
                         Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, lockOwner);
            var update = Builders<TimeoutData>.Update
                .Set(x => x.Locked, false)
                .Set(x => x.LockedBy, Guid.Empty)
                .Set(x => x.LockExpiresAt, null);
            await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to release dispatched timeout with Id '{id}' for owner '{lockOwner}'.", ex);
        }
    }
```

Keep the legacy `ITimeoutStore` methods unchanged so callers that only know about `ITimeoutStore` keep working.

- [ ] **Step 4: Re-run the Mongo timeout tests and then the combined timeout suite**

Run:

```bash
dotnet test src/ServiceConnect.UnitTests/ServiceConnect.UnitTests.csproj --filter "FullyQualifiedName~MongoDbTimeoutStoreTests|FullyQualifiedName~InMemoryTimeoutStoreTests|FullyQualifiedName~ProcessManagerTimeoutServiceTests"
```

Expected: all timeout-related unit tests pass.

- [ ] **Step 5: Run the process-manager timeout E2E tests**

Run:

```bash
dotnet test src/ServiceConnect.EndToEndTests/ServiceConnect.EndToEndTests.csproj --filter "FullyQualifiedName~ProcessManagerTimeoutTests|FullyQualifiedName~ProcessManagerMongoDbTests"
```

Expected: process-manager timeout flows pass for both persistence modes.

- [ ] **Step 6: Run GitNexus impact verification before commit**

Run:

```bash
rtk gitnexus impact ITimeoutStore --include-tests
rtk gitnexus impact MongoDbTimeoutStore --include-tests
rtk gitnexus impact ProcessManagerTimeoutService --include-tests
```

Expected:
- `ITimeoutStore` remains unchanged
- changed scope is limited to the new extension interface, two timeout stores, timeout service, and timeout-focused tests
