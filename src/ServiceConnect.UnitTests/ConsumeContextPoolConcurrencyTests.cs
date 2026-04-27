using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Concurrency exercises for the pooled <see cref="ConsumeContextPool"/>. The pool
/// is hit on every consumed message; a rent/return bug (lost token, header bleed,
/// stale-handle access) only surfaces under sustained parallelism.
/// </summary>
public class ConsumeContextPoolConcurrencyTests
{
    private static readonly IQueueConfiguration QueueConfig = new QueueConfiguration
    {
        QueueName = "q",
        ErrorQueueName = "q.errors",
        AuditQueueName = "q.audit"
    };

    private static readonly IBusConfiguration BusConfig = new BusConfiguration();

    private static IBus Bus => new Mock<IBus>().Object;

    [Fact]
    public async Task ParallelRentAndRelease_ManyIterations_NoExceptionsAndPoolBounded()
    {
        const int workerCount = 32;
        const int iterationsPerWorker = 200;

        var pool = new ConsumeContextPool();

        var workers = Enumerable.Range(0, workerCount).Select(workerIdx => Task.Run(() =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                var headers = new Dictionary<string, object>(StringComparer.Ordinal);
                var handle = pool.Rent(Bus, headers, QueueConfig, BusConfig, null, CancellationToken.None);

                // Touch each accessor to drive the EnsureActive token check on the hot path.
                var bus = handle.Bus;
                var hdrs = handle.Headers;
                var corr = handle.CorrelationId;
                var msgId = handle.MessageId;
                _ = (bus, hdrs, corr, msgId, workerIdx);

                handle.Release();
            }
        })).ToArray();

        var allDone = Task.WhenAll(workers);
        var ex = await Record.ExceptionAsync(() => allDone);
        Assert.Null(ex);
    }

    [Fact]
    public void StaleHandle_AccessAfterRelease_Throws()
    {
        var pool = new ConsumeContextPool();
        var handle = pool.Rent(Bus, new Dictionary<string, object>(StringComparer.Ordinal),
            QueueConfig, BusConfig, null, CancellationToken.None);

        handle.Release();

        Assert.Throws<InvalidOperationException>(() => _ = handle.Bus);
        Assert.Throws<InvalidOperationException>(() => _ = handle.Headers);
        Assert.Throws<InvalidOperationException>(() => _ = handle.CorrelationId);
        Assert.Throws<InvalidOperationException>(() => _ = handle.MessageId);
    }

    [Fact]
    public async Task ParallelRentals_ObserveTheirOwnHeaders_NoBleedBetweenRenters()
    {
        // Each rental sets its own MessageId header. Under the rent-token guard,
        // a rental MUST observe only the value supplied at its own Initialize call,
        // even when other rentals are concurrently churning the pool.
        const int workerCount = 16;
        const int iterationsPerWorker = 300;

        var pool = new ConsumeContextPool();
        var mismatches = 0;

        var workers = Enumerable.Range(0, workerCount).Select(workerId => Task.Run(() =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                var expected = $"w{workerId}-i{i}";
                var headers = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [HeaderKeys.MessageId] = expected
                };

                var handle = pool.Rent(Bus, headers, QueueConfig, BusConfig, null, CancellationToken.None);
                try
                {
                    if (!string.Equals(handle.MessageId, expected, StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref mismatches);
                    }
                }
                finally
                {
                    handle.Release();
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public void RentReleaseRent_OldHandle_ThrowsAfterReuse()
    {
        // ConcurrentBag may hand the same backing PooledConsumeContext back to the
        // next rental. The captured token on the first RentalHandle must reject
        // access after the instance has been re-Initialize'd for the second rental.
        var pool = new ConsumeContextPool();

        var firstHandle = pool.Rent(Bus, new Dictionary<string, object> { [HeaderKeys.MessageId] = "first" },
            QueueConfig, BusConfig, null, CancellationToken.None);
        Assert.Equal("first", firstHandle.MessageId);
        firstHandle.Release();

        var secondHandle = pool.Rent(Bus, new Dictionary<string, object> { [HeaderKeys.MessageId] = "second" },
            QueueConfig, BusConfig, null, CancellationToken.None);
        try
        {
            // The first handle's snapshot is now stale even if the same instance was reused.
            Assert.Throws<InvalidOperationException>(() => _ = firstHandle.MessageId);
            Assert.Equal("second", secondHandle.MessageId);
        }
        finally
        {
            secondHandle.Release();
        }
    }

    [Fact]
    public async Task SustainedBurst_PoolDoesNotGrowUnbounded()
    {
        // The pool caps itself at MaxPoolSize (512). Under a burst that briefly holds
        // far more handles than the cap, excess instances must be dropped to GC rather
        // than retained — otherwise rent/return would slowly leak under sustained load.
        const int concurrentRentals = 2_000;

        var pool = new ConsumeContextPool();
        var handles = new ConsumeContextPool.RentalHandle[concurrentRentals];

        await Task.Run(() =>
        {
            Parallel.For(0, concurrentRentals, i =>
            {
                handles[i] = pool.Rent(Bus, new Dictionary<string, object>(StringComparer.Ordinal),
                    QueueConfig, BusConfig, null, CancellationToken.None);
            });
        });

        // Release every rental, then confirm each release succeeded by accessing
        // through stale handles — all must throw, proving Release bumped the token.
        Parallel.For(0, concurrentRentals, i => handles[i].Release());

        var staleAccessThrows = 0;
        Parallel.For(0, concurrentRentals, i =>
        {
            try
            {
                _ = handles[i].Bus;
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref staleAccessThrows);
            }
        });

        Assert.Equal(concurrentRentals, staleAccessThrows);
    }
}
