using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Concurrency exercises for <see cref="InMemoryProcessManagerFinder"/>. The finder
/// uses a reader/writer lock and optimistic version checking — a busted version
/// gate would silently lose saga updates under concurrent fan-in.
/// </summary>
public class InMemoryProcessManagerFinderConcurrencyTests
{
    private static IProcessManagerPropertyMapper BuildMapper()
    {
        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);
        return mapper;
    }

    [Fact]
    public async Task ParallelInsert_DistinctIds_AllPersist()
    {
        const int writers = 16;
        const int perWriter = 50;
        var finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
        var mapper = BuildMapper();

        var ids = Enumerable.Range(0, writers * perWriter).Select(idx => Guid.NewGuid()).ToArray();

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                var data = new TestData { CorrelationId = ids[(w * perWriter) + i], Name = $"w{w}-i{i}" };
                await finder.InsertDataAsync(data, CancellationToken.None);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Every id must be findable.
        foreach (var id in ids)
        {
            var found = await finder.FindDataAsync<IProcessManagerData>(mapper, new Message(id), CancellationToken.None);
            Assert.NotNull(found);
        }
    }

    [Fact]
    public async Task ParallelInsert_SameCorrelationId_OneSucceedsRestThrowPersistence()
    {
        // The InMemory finder rejects duplicate ids with PersistenceException; under a
        // race only one inserter may win.
        var finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
        var corrId = Guid.NewGuid();
        const int contenders = 16;
        var successes = 0;
        var conflicts = 0;
        var unexpected = 0;

        var tasks = Enumerable.Range(0, contenders).Select(_ => Task.Run(async () =>
        {
            try
            {
                await finder.InsertDataAsync(
                    new TestData { CorrelationId = corrId, Name = "first-or-loser" },
                    CancellationToken.None);
                Interlocked.Increment(ref successes);
            }
            catch (PersistenceException)
            {
                Interlocked.Increment(ref conflicts);
            }
            catch
            {
                Interlocked.Increment(ref unexpected);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, successes);
        Assert.Equal(contenders - 1, conflicts);
        Assert.Equal(0, unexpected);
    }

    [Fact]
    public async Task ParallelUpdate_OnSameRecord_ExactlyOneWinsPerVersion()
    {
        // Optimistic concurrency: many workers each have version=1 in hand. Exactly one
        // wins per round; the rest must surface ConcurrencyException so the caller
        // (typically ProcessManagerProcessor) can re-read and retry.
        var finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
        var mapper = BuildMapper();
        var corrId = Guid.NewGuid();

        await finder.InsertDataAsync(
            new TestData { CorrelationId = corrId, Name = "initial" },
            CancellationToken.None);

        const int contenders = 16;
        var successes = 0;
        var conflicts = 0;

        var tasks = Enumerable.Range(0, contenders).Select(i => Task.Run(async () =>
        {
            try
            {
                await finder.UpdateDataAsync(new MemoryData<IProcessManagerData>
                {
                    Data = new TestData { CorrelationId = corrId, Name = $"upd-{i}" },
                    Version = 1,
                }, CancellationToken.None);
                Interlocked.Increment(ref successes);
            }
            catch (ConcurrencyException)
            {
                Interlocked.Increment(ref conflicts);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, successes);
        Assert.Equal(contenders - 1, conflicts);

        // After exactly one successful update the stored version is 2.
        var found = await finder.FindDataAsync<IProcessManagerData>(mapper, new Message(corrId), CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(2, ((MemoryData<IProcessManagerData>)found).Version);
    }

    [Fact]
    public async Task ParallelDelete_OnSameRecord_OnlyOneSucceeds_RestThrowConcurrency()
    {
        var finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
        var corrId = Guid.NewGuid();
        await finder.InsertDataAsync(
            new TestData { CorrelationId = corrId, Name = "doomed" },
            CancellationToken.None);

        const int contenders = 16;
        var successes = 0;
        var conflicts = 0;

        var tasks = Enumerable.Range(0, contenders).Select(_ => Task.Run(async () =>
        {
            try
            {
                await finder.DeleteDataAsync(new MemoryData<IProcessManagerData>
                {
                    Data = new TestData { CorrelationId = corrId, Name = "doomed" },
                    Version = 1,
                }, CancellationToken.None);
                Interlocked.Increment(ref successes);
            }
            catch (ConcurrencyException)
            {
                Interlocked.Increment(ref conflicts);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, successes);
        Assert.Equal(contenders - 1, conflicts);
    }

    [Fact]
    public async Task ParallelInsertAndFind_NoExceptions_AndFindOnlySeesCommittedRows()
    {
        // Many writers insert distinct rows while readers concurrently call FindDataAsync.
        // The reader must either find the row (after the writer commits) or not find it
        // (before the commit) — never throw because of a half-published row.
        const int writers = 8;
        const int perWriter = 200;

        var finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
        var mapper = BuildMapper();
        var ids = Enumerable.Range(0, writers * perWriter).Select(idx => Guid.NewGuid()).ToArray();

        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await finder.InsertDataAsync(
                    new TestData { CorrelationId = ids[(w * perWriter) + i], Name = $"w{w}-i{i}" },
                    CancellationToken.None);
            }
        })).ToArray();

        var readerStop = new CancellationTokenSource();
        var readerTasks = Enumerable.Range(0, 4).Select(readerIdx => Task.Run(async () =>
        {
            var rng = new Random(Environment.TickCount);
            while (!readerStop.Token.IsCancellationRequested)
            {
                var probe = ids[rng.Next(ids.Length)];
                var probed = await finder.FindDataAsync<IProcessManagerData>(mapper, new Message(probe), CancellationToken.None);
                _ = (probed, readerIdx);
            }
        })).ToArray();

        await Task.WhenAll(writerTasks);
        readerStop.Cancel();
        var readerEx = await Record.ExceptionAsync(() => Task.WhenAll(readerTasks));
        Assert.Null(readerEx);

        // After writers complete, every id is findable.
        foreach (var id in ids)
        {
            Assert.NotNull(await finder.FindDataAsync<IProcessManagerData>(mapper, new Message(id), CancellationToken.None));
        }
    }
}
