using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ConsumerParallelDisposeTests
{
    private static Consumer CreateConsumer(int consumerCount = 5)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.RetryDelay).Returns(0);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("q");

        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.ConsumerCount).Returns(consumerCount);

        return new Consumer(transport.Object, queueConfig.Object, busConfig.Object,
            NullLogger<Consumer>.Instance);
    }

    private static ConcurrentBag<IAsyncDisposable> GetClients(Consumer consumer)
    {
        return (ConcurrentBag<IAsyncDisposable>)typeof(Consumer)
            .GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(consumer)!;
    }

    [Fact]
    public async Task DisposeAsync_DisposesHostsInParallel_NotSequentially()
    {
        const int hostCount = 5;
        var hostDelay = TimeSpan.FromMilliseconds(200);

        var consumer = CreateConsumer(hostCount);
        var clients = GetClients(consumer);

        var fakeHosts = Enumerable.Range(0, hostCount)
            .Select(_ => new DelayingDisposable(hostDelay))
            .ToList();

        foreach (var host in fakeHosts)
        {
            clients.Add(host);
        }

        var sw = Stopwatch.StartNew();
        await consumer.DisposeAsync();
        sw.Stop();

        // Sequential dispose would be ~hostCount * hostDelay = 1000ms.
        // Parallel dispose should be ~hostDelay + small overhead = ~250ms.
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Dispose took {sw.ElapsedMilliseconds}ms; expected < 500ms (parallel). Sequential would be ~{hostCount * hostDelay.TotalMilliseconds}ms.");

        Assert.All(fakeHosts, h => Assert.True(h.WasDisposed));
    }

    [Fact]
    public async Task DisposeAsync_OneHostThrows_OtherHostsStillDisposed()
    {
        const int hostCount = 5;

        var consumer = CreateConsumer(hostCount);
        var clients = GetClients(consumer);

        var goodHosts = Enumerable.Range(0, hostCount - 1)
            .Select(_ => new DelayingDisposable(TimeSpan.FromMilliseconds(50)))
            .ToList();
        var badHost = new ThrowingDisposable();

        foreach (var host in goodHosts)
        {
            clients.Add(host);
        }
        clients.Add(badHost);

        // The throwing host shouldn't propagate — Consumer's inner try/catch swallows
        // per-host failures so the remaining hosts still complete their dispose.
        await consumer.DisposeAsync();

        Assert.All(goodHosts, h => Assert.True(h.WasDisposed));
        Assert.True(badHost.DisposeAttempted);
    }

    private sealed class DelayingDisposable(TimeSpan delay) : IAsyncDisposable
    {
        public bool WasDisposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Task.Delay(delay).ConfigureAwait(false);
            WasDisposed = true;
        }
    }

    private sealed class ThrowingDisposable : IAsyncDisposable
    {
        public bool DisposeAttempted { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeAttempted = true;
            throw new InvalidOperationException("simulated dispose failure");
        }
    }
}
