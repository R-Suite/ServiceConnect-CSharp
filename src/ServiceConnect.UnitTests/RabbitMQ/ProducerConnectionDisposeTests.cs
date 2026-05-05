using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.UnitTests;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

[Collection(SerialConcurrencyCollection.Name)]
public sealed class ProducerConnectionDisposeTests
{
    private static ProducerConnection CreateProducerConnection()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        return new ProducerConnection(transport.Object, NullLogger.Instance);
    }

    [Fact]
    public async Task ReconnectAsync_CancellationTokenCancelled_DisposeReturnsWithinBoundedTime()
    {
        // Arrange: construct a ProducerConnection, acquire its _connectionSemaphore in a
        // background task and hold it for longer than any reasonable test timeout, then
        // cancel the token quickly and assert ReconnectAsync returns within ~2s (well under
        // the 30s ceiling).
        var producerConnection = CreateProducerConnection();

        // Install a no-op ReconnectForTests hook so EnsureConnectedAsync is not called after
        // DisposeConnectionAsync (that would attempt a real AMQP connection).
        producerConnection.ReconnectForTests = _ => Task.CompletedTask;

        var semaphoreField = typeof(ProducerConnection).GetField(
            "_connectionSemaphore",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var semaphore = (SemaphoreSlim)semaphoreField!.GetValue(producerConnection)!;

        using var holderRelease = new ManualResetEventSlim(false);
        var holderTask = Task.Run(async () =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try { holderRelease.Wait(); }
            finally { semaphore.Release(); }
        });

        // Allow the holder task a moment to acquire the semaphore.
        await Task.Delay(50);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var sw = Stopwatch.StartNew();
        // ReconnectAsync routes through DisposeConnectionAsync which honours both the
        // 30s ceiling and the cancellation token rather than blocking indefinitely.
        await producerConnection.ReconnectAsync(new InvalidOperationException("test"), cts.Token);
        sw.Stop();

        holderRelease.Set();
        await holderTask;

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"DisposeConnectionAsync took {sw.Elapsed} — should have returned within ~50ms+best-effort-teardown after cancellation, well under 2s.");
    }
}
