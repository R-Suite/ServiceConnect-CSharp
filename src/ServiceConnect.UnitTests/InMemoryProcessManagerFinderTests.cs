using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.InMemory;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class TestData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; } = "";
    }

    public class NonJsonRoundTrippableTestData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public Type ValueType { get; set; } = typeof(object);
    }

    public class NoPublicParameterlessCtorTestData : IProcessManagerData
    {
        private NoPublicParameterlessCtorTestData() { }

        public NoPublicParameterlessCtorTestData(Guid correlationId, string name)
        {
            CorrelationId = correlationId;
            Name = name;
        }

        public Guid CorrelationId { get; set; }
        public string Name { get; set; } = "";
    }

    public class InMemoryProcessManagerFinderTests
    {
        readonly Guid _correlationId = Guid.NewGuid();
        private readonly IProcessManagerPropertyMapper _mapper;

        public InMemoryProcessManagerFinderTests()
        {
            _mapper = new TestProcessManagerPropertyMapper();
            _mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);
        }

        [Fact]
        public async Task ShouldInsertData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            // Act
            await processManagerFinder.InsertDataAsync(data, CancellationToken.None);

            // Assert
            // InsertDataAsync wraps as MemoryData<IProcessManagerData>, so FindDataAsync must use IProcessManagerData
            var found = await processManagerFinder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None);
            Assert.NotNull(found);
            Assert.Equal("TestData", ((TestData)found.Data).Name);
        }

        [Fact]
        public async Task ShouldThrowWhenInsertingDataWithExistingId()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerData dataWithDuplicateId = new TestData { CorrelationId = _correlationId, Name = "TestDataWithDuplicateId" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            await processManagerFinder.InsertDataAsync(data, CancellationToken.None);

            // Act / Assert
            await Assert.ThrowsAsync<PersistenceException>(() => processManagerFinder.InsertDataAsync(dataWithDuplicateId, CancellationToken.None));
        }

    [Fact]
    public async Task ShouldUpdateData()
    {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerData dataUpdated = new TestData { CorrelationId = _correlationId, Name = "TestDataUpdated" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            await processManagerFinder.InsertDataAsync(data, CancellationToken.None);

            // Act
            await processManagerFinder.UpdateDataAsync(new MemoryData<IProcessManagerData> { Data = dataUpdated, Version = 1 }, CancellationToken.None);

            // Assert
            var found = await processManagerFinder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal("TestDataUpdated", ((TestData)found.Data).Name);
    }

    [Fact]
    public async Task FindDataAsync_ReturnsDetachedCopy_AndDoesNotLeakMutationsWithoutUpdate()
    {
        // Arrange
        IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "Original" };
        IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
        await processManagerFinder.InsertDataAsync(data, CancellationToken.None);

        // Act
        var loaded = await processManagerFinder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None);
        Assert.NotNull(loaded);
        ((TestData)loaded.Data).Name = "Mutated";

        var reloaded = await processManagerFinder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None);

        // Assert
        Assert.NotNull(reloaded);
        Assert.Equal("Original", ((TestData)reloaded.Data).Name);
    }

    [Fact]
    public async Task FindDataAsync_TypedRead_ReturnsDetachedCopy_AndDoesNotLeakMutationsWithoutUpdate()
    {
        // Arrange
        var data = new TestData { CorrelationId = _correlationId, Name = "Original" };
        IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
        await processManagerFinder.InsertDataAsync(data, CancellationToken.None);

        // Act
        var loaded = await processManagerFinder.FindDataAsync<TestData>(_mapper, new Message(_correlationId), CancellationToken.None);
        Assert.NotNull(loaded);
        loaded.Data.Name = "Mutated";

        var reloaded = await processManagerFinder.FindDataAsync<TestData>(_mapper, new Message(_correlationId), CancellationToken.None);

        // Assert
        Assert.NotNull(reloaded);
        Assert.Equal("Original", reloaded.Data.Name);
    }

    [Fact]
    public async Task FindDataAsync_TypedRead_SupportsRuntimeTypesThatAreNotJsonRoundTrippable()
    {
        // Arrange
        var data = new NonJsonRoundTrippableTestData
        {
            CorrelationId = _correlationId,
            ValueType = typeof(TestData)
        };
        IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
        await processManagerFinder.InsertDataAsync(data, CancellationToken.None);

        // Act
        var found = await processManagerFinder.FindDataAsync<NonJsonRoundTrippableTestData>(_mapper, new Message(_correlationId), CancellationToken.None);

        // Assert
        Assert.NotNull(found);
        Assert.Equal(typeof(TestData), found.Data.ValueType);
    }

    [Fact]
    public async Task FindDataAsync_TypedRead_SupportsRuntimeTypesWithoutPublicParameterlessConstructor()
    {
        // Arrange
        var data = new NoPublicParameterlessCtorTestData(_correlationId, "Original");
        IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
        await processManagerFinder.InsertDataAsync(data, CancellationToken.None);

        // Act
        var found = await processManagerFinder.FindDataAsync<NoPublicParameterlessCtorTestData>(_mapper, new Message(_correlationId), CancellationToken.None);

        // Assert
        Assert.NotNull(found);
        Assert.Equal("Original", found.Data.Name);
    }

    [Fact]
    public async Task ShouldThrowWhenUpdatingDataThatDoesNotExist()
    {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            // Act / Assert
            await Assert.ThrowsAsync<PersistenceException>(() => processManagerFinder.UpdateDataAsync(new MemoryData<IProcessManagerData> { Data = data }, CancellationToken.None));
        }

        [Fact]
        public async Task ShouldThrowConcurrencyExceptionWhenUpdatingStaleVersion()
        {
            // Arrange
            IProcessManagerData data1 = new TestData { CorrelationId = _correlationId, Name = "TestData1" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            await processManagerFinder.InsertDataAsync(data1, CancellationToken.None);

            var foundData1 = (MemoryData<IProcessManagerData>)(await processManagerFinder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None))!;
            var foundData2 = (MemoryData<IProcessManagerData>)(await processManagerFinder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None))!;

            var foundData1Temp = new MemoryData<IProcessManagerData> { Data = foundData1.Data, Version = foundData1.Version };
            var foundData2Temp = new MemoryData<IProcessManagerData> { Data = foundData2.Data, Version = foundData2.Version };

            await processManagerFinder.UpdateDataAsync(foundData1Temp, CancellationToken.None); // first update should be fine

            // Act / Assert — second update is a stale-version conflict; ProcessManagerProcessor
            // only retries on ConcurrencyException, so the in-memory finder must raise that type
            // to match MongoDbProcessManagerFinder's contract.
            await Assert.ThrowsAsync<ConcurrencyException>(() => processManagerFinder.UpdateDataAsync(foundData2Temp, CancellationToken.None));
        }

        [Fact]
        public async Task ShouldDeleteData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            await processManagerFinder.InsertDataAsync(data, CancellationToken.None);
            var loaded = await processManagerFinder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None);
            Assert.NotNull(loaded);

            // Act
            await processManagerFinder.DeleteDataAsync(loaded, CancellationToken.None);

            // Assert
            Assert.Null(await processManagerFinder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None));
        }

        [Fact]
        public async Task DeleteDataAsync_WithStaleVersion_ThrowsConcurrencyException()
        {
            // Arrange
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            await finder.InsertDataAsync(new TestData { CorrelationId = _correlationId, Name = "v1" }, CancellationToken.None);
            var stale = (MemoryData<IProcessManagerData>)(await finder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None))!;

            // Bump the stored version via a successful update so `stale` is behind.
            await finder.UpdateDataAsync(new MemoryData<IProcessManagerData>
            {
                Data = new TestData { CorrelationId = _correlationId, Name = "v2" },
                Version = stale.Version
            }, CancellationToken.None);

            // Act / Assert — delete with the stale version is a conflict, not a silent no-op.
            await Assert.ThrowsAsync<ConcurrencyException>(
                () => finder.DeleteDataAsync(stale, CancellationToken.None));

            // The record must still be present.
            Assert.NotNull(await finder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None));
        }

        [Fact]
        public async Task DeleteDataAsync_WhenKeyMissing_ThrowsConcurrencyException()
        {
            // Matches the UpdateDataAsync contract: deleting a record that no longer exists
            // is a conflict (another consumer already completed the saga), not a silent no-op.
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            var stub = new MemoryData<IProcessManagerData>
            {
                Data = new TestData { CorrelationId = _correlationId, Name = "ghost" },
                Version = 1
            };

            await Assert.ThrowsAsync<ConcurrencyException>(
                () => finder.DeleteDataAsync(stub, CancellationToken.None));
        }

        [Fact]
        public async Task ShouldReturnNullWhenDataNotFound()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            // Act
            var result = await processManagerFinder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None);

            // Assert
            Assert.Null(result);
        }

        // --- Timeout tests ---

        private static TimeoutData MakeTimeoutData(Guid id, DateTimeOffset time) => new TimeoutData
        {
            Id = id,
            Time = time,
            Headers = new Dictionary<string, object>()
        };

        [Fact]
        public async Task InsertTimeout_StoresTimeoutData()
        {
            ITimeoutStore finder = new InMemoryTimeoutStore(string.Empty, string.Empty);
            var id = Guid.NewGuid();
            await finder.InsertTimeoutAsync(MakeTimeoutData(id, DateTimeOffset.UtcNow.AddHours(-1)), CancellationToken.None);

            var batch = await finder.GetTimeoutsBatchAsync(cancellationToken: CancellationToken.None);
            Assert.Single(batch.DueTimeouts);
            Assert.Equal(id, batch.DueTimeouts[0].Id);
        }

        [Fact]
        public async Task InsertTimeout_ThrowsWhenDuplicateId()
        {
            ITimeoutStore finder = new InMemoryTimeoutStore(string.Empty, string.Empty);
            var id = Guid.NewGuid();
            await finder.InsertTimeoutAsync(MakeTimeoutData(id, DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None);

            await Assert.ThrowsAsync<PersistenceException>(() => finder.InsertTimeoutAsync(MakeTimeoutData(id, DateTimeOffset.UtcNow.AddMinutes(10)), CancellationToken.None));
        }

        [Fact]
        public async Task GetTimeoutsBatch_WhenNoTimeouts_ReturnEmptyDueList()
        {
            ITimeoutStore finder = new InMemoryTimeoutStore(string.Empty, string.Empty);

            var batch = await finder.GetTimeoutsBatchAsync(cancellationToken: CancellationToken.None);

            Assert.Empty(batch.DueTimeouts);
        }

        [Fact]
        public async Task GetTimeoutsBatch_FutureTimeout_NotInDueList()
        {
            ITimeoutStore finder = new InMemoryTimeoutStore(string.Empty, string.Empty);
            await finder.InsertTimeoutAsync(MakeTimeoutData(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);

            var batch = await finder.GetTimeoutsBatchAsync(cancellationToken: CancellationToken.None);

            Assert.Empty(batch.DueTimeouts);
        }

        [Fact]
        public async Task GetTimeoutsBatch_PastTimeout_IsInDueList()
        {
            ITimeoutStore finder = new InMemoryTimeoutStore(string.Empty, string.Empty);
            await finder.InsertTimeoutAsync(MakeTimeoutData(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-1)), CancellationToken.None);

            var batch = await finder.GetTimeoutsBatchAsync(cancellationToken: CancellationToken.None);

            Assert.Single(batch.DueTimeouts);
        }

        [Fact]
        public async Task RemoveDispatchedTimeout_RemovesTimeoutFromBatch()
        {
            ITimeoutStore finder = new InMemoryTimeoutStore(string.Empty, string.Empty);
            var id = Guid.NewGuid();
            await finder.InsertTimeoutAsync(MakeTimeoutData(id, DateTimeOffset.UtcNow.AddSeconds(-1)), CancellationToken.None);

            await finder.RemoveDispatchedTimeoutAsync(id, cancellationToken: CancellationToken.None);

            var batch = await finder.GetTimeoutsBatchAsync(cancellationToken: CancellationToken.None);
            Assert.Empty(batch.DueTimeouts);
        }

        [Fact]
        public async Task RemoveDispatchedTimeout_WhenIdDoesNotExist_DoesNotThrow()
        {
            ITimeoutStore finder = new InMemoryTimeoutStore(string.Empty, string.Empty);

            var ex = await Record.ExceptionAsync(() => finder.RemoveDispatchedTimeoutAsync(Guid.NewGuid(), cancellationToken: CancellationToken.None));

            Assert.Null(ex);
        }

        // --- Pre-cancelled token tests ---

        [Fact]
        public async Task FindDataAsync_PreCancelledToken_ThrowsOCE()
        {
            var finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => finder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), cts.Token));
        }

        [Fact]
        public async Task InsertDataAsync_PreCancelledToken_ThrowsOCE()
        {
            var finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => finder.InsertDataAsync(new TestData { CorrelationId = _correlationId }, cts.Token));
        }

        [Fact]
        public async Task UpdateDataAsync_PreCancelledToken_ThrowsOCE()
        {
            var finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => finder.UpdateDataAsync(new MemoryData<IProcessManagerData> { Data = new TestData { CorrelationId = _correlationId }, Version = 1 }, cts.Token));
        }

        [Fact]
        public async Task DeleteDataAsync_PreCancelledToken_ThrowsOCE()
        {
            var finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => finder.DeleteDataAsync(new MemoryData<IProcessManagerData> { Data = new TestData { CorrelationId = _correlationId } }, cts.Token));
        }

        [Fact]
        public async Task InsertTimeoutAsync_PreCancelledToken_ThrowsOCE()
        {
            var finder = new InMemoryTimeoutStore(string.Empty, string.Empty);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => finder.InsertTimeoutAsync(MakeTimeoutData(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)), cts.Token));
        }

        [Fact]
        public async Task GetTimeoutsBatchAsync_PreCancelledToken_ThrowsOCE()
        {
            var finder = new InMemoryTimeoutStore(string.Empty, string.Empty);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => finder.GetTimeoutsBatchAsync(cancellationToken: cts.Token));
        }

        [Fact]
        public async Task RemoveDispatchedTimeoutAsync_PreCancelledToken_ThrowsOCE()
        {
            var finder = new InMemoryTimeoutStore(string.Empty, string.Empty);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => finder.RemoveDispatchedTimeoutAsync(Guid.NewGuid(), cancellationToken: cts.Token));
        }

        [Fact]
        public async Task FindDataAsync_IgnoresFallbackEntriesWithoutIntegerVersion()
        {
            var cache = new ProcessManagerPredicateCache();
            var state = new InMemoryPersistenceState();
            var finder = new InMemoryProcessManagerFinder(cache, state);
            var correlationId = Guid.NewGuid();
            var mapper = new TestProcessManagerPropertyMapper();
            mapper.ConfigureMapping<TestData, FakeMessage1>(data => data.CorrelationId, message => message.CorrelationId);

            state.Provider.Add(correlationId.ToString(), new LegacyMemoryData
            {
                Data = new TestData { CorrelationId = correlationId },
                Version = null
            }, DateTimeOffset.UtcNow.AddMinutes(5));

            var exception = await Record.ExceptionAsync(() => finder.FindDataAsync<TestData>(
                mapper,
                new FakeMessage1(correlationId),
                CancellationToken.None));

            Assert.Null(exception);
        }

        [Fact]
        public void InMemoryPersistenceState_UsesReaderWriterLockSlim()
        {
            Assert.Equal(
                typeof(System.Threading.ReaderWriterLockSlim),
                typeof(InMemoryPersistenceState).GetProperty(nameof(InMemoryPersistenceState.SyncRoot))!.PropertyType);
        }

        /// <summary>
        /// Saga data with a nested mutable collection, used to prove that the in-memory
        /// finder deep-clones rather than aliasing caller state.
        /// </summary>
        public class SagaWithNested : IProcessManagerData
        {
            public Guid CorrelationId { get; set; }
            public List<string> Tags { get; set; } = new();
        }

        [Fact]
        public async Task InsertDataAsync_ThenMutateCallerObject_DoesNotCorruptStoredEntry()
        {
            // Insert must deep-clone so that post-insert mutation of a nested
            // collection on the caller's instance does not leak into the stored saga.
            var data = new SagaWithNested { CorrelationId = Guid.NewGuid(), Tags = { "original" } };
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            await finder.InsertDataAsync(data, CancellationToken.None);

            data.Tags.Add("after-insert-mutation");

            var mapper = new TestProcessManagerPropertyMapper();
            mapper.ConfigureMapping<SagaWithNested, Message>(d => d.CorrelationId, m => m.CorrelationId);
            var found = await finder.FindDataAsync<SagaWithNested>(mapper, new Message(data.CorrelationId), CancellationToken.None);

            Assert.NotNull(found);
            Assert.Equal(new[] { "original" }, found.Data.Tags);
        }

        [Fact]
        public async Task UpdateDataAsync_ThenMutateCallerObject_DoesNotCorruptStoredEntry()
        {
            // Update must deep-clone so the caller's subsequent mutation of a
            // nested collection does not bleed into the stored snapshot.
            var initial = new SagaWithNested { CorrelationId = Guid.NewGuid(), Tags = { "first" } };
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            await finder.InsertDataAsync(initial, CancellationToken.None);

            var updated = new SagaWithNested { CorrelationId = initial.CorrelationId, Tags = { "second" } };
            await finder.UpdateDataAsync(
                new MemoryData<SagaWithNested> { Data = updated, Version = 1 },
                CancellationToken.None);

            updated.Tags.Add("post-update-mutation");

            var mapper = new TestProcessManagerPropertyMapper();
            mapper.ConfigureMapping<SagaWithNested, Message>(d => d.CorrelationId, m => m.CorrelationId);
            var found = await finder.FindDataAsync<SagaWithNested>(mapper, new Message(initial.CorrelationId), CancellationToken.None);

            Assert.NotNull(found);
            Assert.Equal(new[] { "second" }, found.Data.Tags);
        }

        [Fact]
        public void InMemoryPersistenceState_Dispose_ReleasesSyncRoot_AndIsIdempotent()
        {
            // SyncRoot is a ReaderWriterLockSlim that holds kernel handles. Dispose
            // must release it exactly once so rebuilding the DI container does not
            // leak one RWSL per container, and repeat Dispose calls must be a no-op.
            var state = new InMemoryPersistenceState();
            var syncRoot = state.SyncRoot;

            state.Dispose();

            // Re-entering a disposed RWSL throws ObjectDisposedException — observable
            // proof that Dispose released the kernel resource.
            Assert.Throws<ObjectDisposedException>(() => syncRoot.EnterReadLock());

            var second = Record.Exception(() => state.Dispose());
            Assert.Null(second);
        }

        // --- Saga lifetime is caller-managed: no background TTL ---

        [Fact]
        public async Task Inserted_SagaStillResolvable_After3Days()
        {
            // Saga state must persist until Delete regardless of elapsed time.
            // Background expiry must never silently drop a live saga.
            var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero));
            var finder = new InMemoryProcessManagerFinder(new ProcessManagerPredicateCache(), timeProvider);
            var data = new TestData { CorrelationId = _correlationId, Name = "LongLivedSaga" };
            await finder.InsertDataAsync(data, CancellationToken.None);

            timeProvider.Advance(TimeSpan.FromDays(3));

            var found = await finder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None);
            Assert.NotNull(found);
            Assert.Equal("LongLivedSaga", ((TestData)found.Data).Name);
        }

        [Fact]
        public async Task Updated_SagaStillResolvable_After3DaysFromInsert()
        {
            var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero));
            var finder = new InMemoryProcessManagerFinder(new ProcessManagerPredicateCache(), timeProvider);
            await finder.InsertDataAsync(new TestData { CorrelationId = _correlationId, Name = "v1" }, CancellationToken.None);

            timeProvider.Advance(TimeSpan.FromDays(1));

            var loaded = (MemoryData<IProcessManagerData>)(await finder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None))!;
            await finder.UpdateDataAsync(new MemoryData<IProcessManagerData>
            {
                Data = new TestData { CorrelationId = _correlationId, Name = "v2" },
                Version = loaded.Version
            }, CancellationToken.None);

            timeProvider.Advance(TimeSpan.FromDays(2));

            var found = await finder.FindDataAsync<IProcessManagerData>(_mapper, new Message(_correlationId), CancellationToken.None);
            Assert.NotNull(found);
            Assert.Equal("v2", ((TestData)found.Data).Name);
        }

        // M18: Keys() snapshot + concurrent external IKeyValueStore.Remove(key) between
        // snapshot and per-key Get(key) returns null, then value.GetType() on null → NRE.
        [Fact]
        public async Task FindDataAsync_ConcurrentKeyValueStoreRemoval_DoesNotNre()
        {
            // Arrange: use InMemoryPersistenceState directly so we hold the IKeyValueStore ref.
            var cache = new ProcessManagerPredicateCache();
            var sharedState = new InMemoryPersistenceState(TimeProvider.System);
            var finder = new InMemoryProcessManagerFinder(cache, sharedState);
            var kvStore = (IKeyValueStore)sharedState.Provider;

            // Seed a batch of items so the scan loop has multiple keys to traverse.
            const int itemCount = 50;
            var ids = Enumerable.Range(0, itemCount).Select(_ => Guid.NewGuid()).ToArray();
            foreach (var id in ids)
                await finder.InsertDataAsync(new TestData { CorrelationId = id, Name = "seed" }, CancellationToken.None);

            var mapper = new TestProcessManagerPropertyMapper();
            mapper.ConfigureMapping<IProcessManagerData, Message>(pm => pm.CorrelationId, m => m.CorrelationId);

            // Act: run many concurrent scans and concurrent removals via the IKeyValueStore
            // facet, which is NOT protected by the finder's ReaderWriterLockSlim.
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var iterations = 200;

            var scanTasks = Enumerable.Range(0, iterations).Select(_ => Task.Run(async () =>
            {
                try
                {
                    // Pick a random id; result may be null if removed — that is fine.
                    var id = ids[Random.Shared.Next(ids.Length)];
                    await finder.FindDataAsync<IProcessManagerData>(mapper, new Message(id), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));

            var removeTasks = Enumerable.Range(0, iterations).Select(_ => Task.Run(() =>
            {
                try
                {
                    // Remove random keys via the unguarded IKeyValueStore interface.
                    foreach (var k in kvStore.Keys().Take(3).ToList())
                        kvStore.Remove(k);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));

            await Task.WhenAll(scanTasks.Concat(removeTasks));

            Assert.Empty(exceptions);
        }

        // M18 completeness: UpdateDataAsync and DeleteDataAsync have the same Contains→Get
        // race window. A mock provider whose Contains returns true but Get returns null
        // deterministically exercises the race without any timing dependency.

        [Fact]
        public async Task UpdateDataAsync_WhenProviderReturnsNullFromGet_ThrowsConcurrencyException()
        {
            // Arrange: provider whose Contains says yes but Get returns null,
            // simulating an external IKeyValueStore.Remove between Contains and Get.
            var provider = new Mock<ICacheProvider>();
            provider.Setup(p => p.Contains(It.IsAny<string>())).Returns(true);
            provider.Setup(p => p.Get<string, object>(It.IsAny<string>())).Returns((object?)null!);

            var state = new InMemoryPersistenceState(provider.Object);
            var finder = new InMemoryProcessManagerFinder(new ProcessManagerPredicateCache(), state);

            var pm = new MemoryData<TestData>
            {
                Data = new TestData { CorrelationId = Guid.NewGuid() },
                Version = 0
            };

            // Act + Assert: must throw ConcurrencyException, NOT NullReferenceException.
            var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => finder.UpdateDataAsync(pm));
            Assert.Contains("concurrently removed", ex.Message);
        }

        [Fact]
        public async Task DeleteDataAsync_WhenProviderReturnsNullFromGet_ThrowsConcurrencyException()
        {
            // Arrange: provider whose Contains says yes but Get returns null,
            // simulating an external IKeyValueStore.Remove between Contains and Get.
            var provider = new Mock<ICacheProvider>();
            provider.Setup(p => p.Contains(It.IsAny<string>())).Returns(true);
            provider.Setup(p => p.Get<string, object>(It.IsAny<string>())).Returns((object?)null!);

            var state = new InMemoryPersistenceState(provider.Object);
            var finder = new InMemoryProcessManagerFinder(new ProcessManagerPredicateCache(), state);

            var pm = new MemoryData<TestData>
            {
                Data = new TestData { CorrelationId = Guid.NewGuid() },
                Version = 0
            };

            // Act + Assert: must throw ConcurrencyException, NOT NullReferenceException.
            var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => finder.DeleteDataAsync(pm));
            Assert.Contains("concurrently removed", ex.Message);
        }

        [Fact]
        public async Task UpdateDataAsync_BumpsCallerVersion_AllowsConsecutiveUpdates()
        {
            // Insert a saga, find it, then do two consecutive updates on the same
            // MemoryData<T> handle. The second update must not throw ConcurrencyException.
            // Mongo persistor returns the post-update document via FindOneAndUpdate; the
            // InMemory persistor must reflect the new Version back to the caller's instance
            // so consecutive updates with the same handle behave identically.
            var correlationId = Guid.NewGuid();
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            await finder.InsertDataAsync(new TestData { CorrelationId = correlationId, Name = "v0" }, CancellationToken.None);

            var mapper = new TestProcessManagerPropertyMapper();
            mapper.ConfigureMapping<IProcessManagerData, Message>(d => d.CorrelationId, m => m.CorrelationId);

            var found = await finder.FindDataAsync<IProcessManagerData>(mapper, new Message(correlationId), CancellationToken.None);
            Assert.NotNull(found);

            // Initial version after Insert is 1; the caller's handle reflects that on Find.
            Assert.Equal(1, ((MemoryData<IProcessManagerData>)found!).Version);

            // First update — store goes 1 → 2; caller's handle must move in lockstep.
            ((TestData)found.Data).Name = "v1";
            await finder.UpdateDataAsync(found, CancellationToken.None);
            Assert.Equal(2, ((MemoryData<IProcessManagerData>)found).Version);

            // Second update on the same handle — must not throw because the caller's
            // Version was incremented to match what the store now holds.
            ((TestData)found.Data).Name = "v2";
            await finder.UpdateDataAsync(found, CancellationToken.None);
            Assert.Equal(3, ((MemoryData<IProcessManagerData>)found).Version);

            // Confirm the second write was persisted.
            var reloaded = await finder.FindDataAsync<IProcessManagerData>(mapper, new Message(correlationId), CancellationToken.None);
            Assert.NotNull(reloaded);
            Assert.Equal("v2", ((TestData)reloaded!.Data).Name);
        }
    }

    /// <summary>
    /// Minimal IProcessManagerPropertyMapper implementation for tests,
    /// replacing the old ProcessManagerPropertyMapper from ServiceConnect.Core.
    /// </summary>
    public class TestProcessManagerPropertyMapper : IProcessManagerPropertyMapper
    {
        private readonly List<ProcessManagerToMessageMap> _mappings = new();
        public IReadOnlyList<ProcessManagerToMessageMap> Mappings => _mappings;

        public void ConfigureMapping<TProcessManagerData, TMessage>(
            System.Linq.Expressions.Expression<Func<TProcessManagerData, object>> processManagerProperty,
            System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
            where TProcessManagerData : IProcessManagerData
        {
            var propertiesHierarchy = new Dictionary<string, Type>();

            // Extract property hierarchy from processManagerProperty
            var body = processManagerProperty.Body;
            if (body is System.Linq.Expressions.UnaryExpression unary)
                body = unary.Operand;

            if (body is System.Linq.Expressions.MemberExpression member)
            {
                var propInfo = (System.Reflection.PropertyInfo)member.Member;
                propertiesHierarchy[propInfo.Name] = propInfo.PropertyType;
            }

            var map = new ProcessManagerToMessageMap
            {
                MessageType = typeof(TMessage),
                PropertiesHierarchy = propertiesHierarchy,
                MessageProp = BuildMessageFunc(messageExpression)
            };

            _mappings.Add(map);
        }

        private static Func<object, object> BuildMessageFunc<TMessage>(
            System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
        {
            var compiled = messageExpression.Compile();
            return obj => compiled((TMessage)obj);
        }
    }

    internal sealed class LegacyMemoryData
    {
        public required TestData Data { get; init; }
        public object? Version { get; init; }
    }
}
