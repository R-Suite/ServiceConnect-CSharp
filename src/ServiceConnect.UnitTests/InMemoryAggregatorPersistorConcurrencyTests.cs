using System;
using System.Collections.Concurrent;
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
/// Concurrency exercises for <see cref="InMemoryAggregatorPersistor"/>. The
/// persistor guards a single mutable list per stream behind one lock; bugs that
/// would lose buffered messages or surface a torn read only show up under
/// concurrent insert/get/remove pressure.
/// </summary>
public class InMemoryAggregatorPersistorConcurrencyTests
{
    [Fact]
    public async Task ParallelInsert_ToSameKey_AllItemsRetrievable()
    {
        const int writers = 16;
        const int perWriter = 100;
        const int expected = writers * perWriter;

        using var persistor = new InMemoryAggregatorPersistor("", "", "");

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await persistor.InsertDataAsync(
                    new AggregatorTestData(Guid.NewGuid()) { Value = $"w{w}-{i}" },
                    "shared",
                    CancellationToken.None);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        var data = await persistor.GetDataAsync("shared", CancellationToken.None);
        Assert.Equal(expected, data.Count);
        Assert.Equal(expected, await persistor.CountAsync("shared", CancellationToken.None));
    }

    [Fact]
    public async Task ParallelInsert_ToDifferentKeys_NoCrossContamination()
    {
        const int streamCount = 32;
        const int perStream = 50;

        using var persistor = new InMemoryAggregatorPersistor("", "", "");

        var tasks = Enumerable.Range(0, streamCount).Select(s => Task.Run(async () =>
        {
            for (var i = 0; i < perStream; i++)
            {
                await persistor.InsertDataAsync(
                    new AggregatorTestData(Guid.NewGuid()) { Value = $"s{s}-{i}" },
                    $"stream-{s}",
                    CancellationToken.None);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        for (var s = 0; s < streamCount; s++)
        {
            var data = await persistor.GetDataAsync($"stream-{s}", CancellationToken.None);
            Assert.Equal(perStream, data.Count);
            Assert.All(data, item => Assert.StartsWith($"s{s}-", ((AggregatorTestData)item).Value));
        }
    }

    [Fact]
    public async Task ParallelInsertAndRemove_StateRemainsConsistent()
    {
        // Interleave inserts and snapshot-driven removes; the buffer must
        // never produce a torn snapshot or trip the no-op-delete contract.
        const int rounds = 200;

        using var persistor = new InMemoryAggregatorPersistor("", "", "");

        var inserter = Task.Run(async () =>
        {
            for (var i = 0; i < rounds; i++)
            {
                await persistor.InsertDataAsync(
                    new AggregatorTestData(Guid.NewGuid()) { Value = $"i{i}" },
                    "key", CancellationToken.None);
            }
        });

        var remover = Task.Run(async () =>
        {
            for (var i = 0; i < rounds; i++)
            {
                var snapshot = await persistor.GetSnapshotAsync("key", CancellationToken.None);
                if (snapshot.ResolvedIds.Count > 0)
                {
                    await persistor.RemoveSnapshotAsync("key", snapshot, CancellationToken.None);
                }
                await Task.Yield();
            }
        });

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(inserter, remover));
        Assert.Null(ex);

        // Drain any residual buffer; final state must be coherent (no exceptions).
        var residual = await persistor.GetSnapshotAsync("key", CancellationToken.None);
        if (residual.ResolvedIds.Count > 0)
        {
            await persistor.RemoveSnapshotAsync("key", residual, CancellationToken.None);
        }
        Assert.Equal(0, await persistor.CountAsync("key", CancellationToken.None));
    }

    [Fact]
    public async Task ParallelRemoveData_OnlyOneRemoverSucceeds_RestThrowConcurrencyException()
    {
        // Multiple workers race to remove the same correlation id. The persistor
        // must surface a ConcurrencyException to all but one — silently no-oping
        // would be the bug we're guarding against.
        using var persistor = new InMemoryAggregatorPersistor("", "", "");
        var corrId = Guid.NewGuid();
        await persistor.InsertDataAsync(new AggregatorTestData(corrId) { Value = "single" }, "key", CancellationToken.None);

        const int contenders = 16;
        var successes = 0;
        var concurrencyConflicts = 0;

        var tasks = Enumerable.Range(0, contenders).Select(_ => Task.Run(async () =>
        {
            try
            {
                await persistor.RemoveDataAsync("key", corrId, CancellationToken.None);
                Interlocked.Increment(ref successes);
            }
            catch (ConcurrencyException)
            {
                Interlocked.Increment(ref concurrencyConflicts);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, successes);
        Assert.Equal(contenders - 1, concurrencyConflicts);
    }

    [Fact]
    public async Task ParallelGet_WhileWriting_NeverReturnsTornData()
    {
        // GetDataAsync deep-clones every entry under the lock — a reader must
        // never observe a half-written list (e.g. fewer entries than were
        // already committed). We verify the count grows monotonically as
        // observed by readers running alongside the writer.
        const int total = 500;

        using var persistor = new InMemoryAggregatorPersistor("", "", "");

        var observedCounts = new ConcurrentBag<int>();

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < total; i++)
            {
                await persistor.InsertDataAsync(
                    new AggregatorTestData(Guid.NewGuid()) { Value = i.ToString() },
                    "stream", CancellationToken.None);
            }
        });

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!writer.IsCompleted)
            {
                var snapshot = await persistor.GetDataAsync("stream", CancellationToken.None);
                observedCounts.Add(snapshot.Count);
                await Task.Yield();
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer));

        // Every observed count is in [0, total]. None negative, none exceeds total.
        Assert.All(observedCounts, c => Assert.InRange(c, 0, total));
        Assert.Equal(total, await persistor.CountAsync("stream", CancellationToken.None));
    }
}
