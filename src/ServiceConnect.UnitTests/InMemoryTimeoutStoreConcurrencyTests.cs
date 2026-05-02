using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Concurrency exercises for <see cref="InMemoryTimeoutStore"/>. Multiple
/// dispatcher polls may race on <see cref="ITimeoutStore.GetTimeoutsBatchAsync"/>;
/// each due timeout must be claimed by exactly one poll, and the lease state
/// must remain coherent under interleaved insert/remove/release.
/// </summary>
public class InMemoryTimeoutStoreConcurrencyTests
{
    [Fact]
    public async Task ParallelInsert_DueTimeouts_AllClaimedAcrossPolls()
    {
        var now = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: time);

        const int writers = 8;
        const int perWriter = 100;
        const int batchSize = 25;
        var ids = new ConcurrentBag<Guid>();

        var writerTasks = Enumerable.Range(0, writers).Select(workerIdx => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                await store.InsertTimeoutAsync(
                    new TimeoutData { Id = id, Time = now.AddMinutes(-1) },
                    CancellationToken.None);
            }
        })).ToArray();

        await Task.WhenAll(writerTasks);

        // Drain in batches without advancing time. Each batch leases up to batchSize
        // rows for 5 minutes; the next batch leases the next chunk because the lease
        // hasn't expired yet. Eventually every row is leased and the batch is empty.
        // Every inserted id must show up exactly once.
        var seen = new HashSet<Guid>();
        var totalRows = writers * perWriter;
        var maxIterations = (totalRows / batchSize) + 5;

        for (var attempts = 0; attempts < maxIterations; attempts++)
        {
            var batch = await store.GetTimeoutsBatchAsync(batchSize);
            if (batch.DueTimeouts.Count == 0)
            {
                break;
            }
            foreach (var t in batch.DueTimeouts)
            {
                Assert.True(seen.Add(t.Id), $"Timeout {t.Id} returned twice");
            }
        }

        Assert.Equal(ids.OrderBy(g => g), seen.OrderBy(g => g));
    }

    [Fact]
    public async Task ConcurrentGetBatch_DueRow_ClaimedByExactlyOneCaller()
    {
        // The lease guard must be tight enough that two pollers running at the same
        // moment cannot both observe the same row as unlocked.
        const int parallelPolls = 16;
        const int rounds = 50;

        var now = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: time);

        for (var r = 0; r < rounds; r++)
        {
            var id = Guid.NewGuid();
            await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

            var observers = new ConcurrentBag<Guid>();
            var pollers = Enumerable.Range(0, parallelPolls).Select(_ => Task.Run(async () =>
            {
                var batch = await store.GetTimeoutsBatchAsync();
                foreach (var t in batch.DueTimeouts)
                {
                    observers.Add(t.LockedBy);
                }
            })).ToArray();

            await Task.WhenAll(pollers);

            // Exactly one poller's session id should have claimed the row.
            Assert.Single(observers);
            Assert.NotEqual(Guid.Empty, observers.Single());

            // Advance past the 5-minute lease and the row's due time so we can re-test.
            time.Advance(TimeSpan.FromMinutes(10));
            await store.RemoveDispatchedTimeoutAsync(id);
        }
    }

    [Fact]
    public async Task ParallelInsertAndRemove_StateRemainsCoherent()
    {
        var now = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: time);

        const int rounds = 500;
        var inserted = new ConcurrentBag<Guid>();

        var inserter = Task.Run(async () =>
        {
            for (var i = 0; i < rounds; i++)
            {
                var id = Guid.NewGuid();
                inserted.Add(id);
                await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });
            }
        });

        var remover = Task.Run(async () =>
        {
            var rng = new Random(42);
            for (var i = 0; i < rounds; i++)
            {
                if (inserted.Count > 0)
                {
                    var snapshot = inserted.ToArray();
                    var id = snapshot[rng.Next(snapshot.Length)];
                    // Unconditional remove path — id-only contract is "remove if present".
                    await store.RemoveDispatchedTimeoutAsync(id);
                }
                await Task.Yield();
            }
        });

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(inserter, remover));
        Assert.Null(ex);

        // The store is still functional after the burst: a fresh poll succeeds
        // and any rows that survived are well-formed.
        time.Advance(TimeSpan.FromMinutes(10));
        var residual = await store.GetTimeoutsBatchAsync();
        Assert.All(residual.DueTimeouts, t => Assert.NotEqual(Guid.Empty, t.Id));
    }

    [Fact]
    public async Task LeasedRow_ReleasedConcurrentlyWithRemove_NoDeadlockOrException()
    {
        // After a dispatcher claims a row, two paths might race: the dispatcher
        // calling RemoveDispatchedTimeoutAsync (success) versus a lease-reaper
        // releasing it (also valid). Both branches must complete without throwing.
        var now = new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: time);

        const int rounds = 100;

        for (var i = 0; i < rounds; i++)
        {
            var id = Guid.NewGuid();
            await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

            var batch = await store.GetTimeoutsBatchAsync();
            var claimed = batch.DueTimeouts.Single();
            var owner = claimed.LockedBy;

            // Race: remove-by-owner versus release-by-owner. Either order is
            // acceptable as long as neither throws beyond the documented contract.
            var removeTask = Task.Run(async () =>
            {
                try { await store.RemoveDispatchedTimeoutAsync(claimed.Id, owner); }
                catch (Interfaces.Exceptions.ConcurrencyException) { /* lease reaped first — fine */ }
            });
            var releaseTask = Task.Run(async () =>
            {
                try { await store.ReleaseDispatchedTimeoutAsync(claimed.Id, owner); }
                catch (Interfaces.Exceptions.ConcurrencyException) { /* removed first — fine */ }
            });

            await Task.WhenAll(removeTask, releaseTask);

            // Best-effort cleanup: forget the row regardless of which path won.
            await store.RemoveDispatchedTimeoutAsync(id);
            time.Advance(TimeSpan.FromSeconds(1));
        }
    }
}
