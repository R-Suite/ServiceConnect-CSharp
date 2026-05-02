using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ConnectionDisposeAsyncRaceTests
{
    [Fact]
    public async Task ConcurrentConnectAndDispose_NoSemaphoreObjectDisposedExceptionEscapes()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var connection = new Connection(transport.Object, "test-queue", NullLogger.Instance);

        // Tighten the dispose-lock timeout so DisposeAsync gives up waiting for a slow
        // ConnectAsync rather than waiting the full 30-second default.
        var timeoutField = typeof(Connection).GetField("_disposeLockTimeout",
            BindingFlags.Instance | BindingFlags.NonPublic);
        timeoutField!.SetValue(connection, TimeSpan.FromMilliseconds(50));

        // Inject a fake connection-creator that hangs until the test cancels it.
        var hangGate = new TaskCompletionSource();
        connection.CreateConnectionForTests = async (_, _, _, ct) =>
        {
            await hangGate.Task.WaitAsync(ct).ConfigureAwait(false);
            return Mock.Of<IConnection>(c => c.IsOpen == true);
        };

        var connectExceptions = new ConcurrentBag<Exception>();
        var disposeExceptions = new ConcurrentBag<Exception>();

        // 8 concurrent connect attempts + 8 concurrent dispose calls.
        var connectTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await connection.CreateChannelAsync(cts.Token);
            }
            catch (ObjectDisposedException ex) when (ex.ObjectName == nameof(Connection))
            {
                // Connection was disposed before this caller got past the explicit ThrowIf check;
                // legitimate ODE on the Connection itself, NOT on the SemaphoreSlim.
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the expected outcome when the hangGate never resolves.
            }
            catch (Exception ex)
            {
                // Anything else — especially ObjectDisposedException with ObjectName "SemaphoreSlim" —
                // indicates the H2 race fired.
                connectExceptions.Add(ex);
            }
        })).ToArray();

        var disposeTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try { await connection.DisposeAsync(); }
            catch (Exception ex) { disposeExceptions.Add(ex); }
        })).ToArray();

        // Let the dispose tasks run a moment before cancelling the hang gate, so the
        // dispose-timeout has fired and the lock has been disposed (pre-fix) before the
        // connect tasks reach their finally block.
        await Task.Delay(200);
        hangGate.TrySetCanceled();

        await Task.WhenAll(connectTasks.Concat(disposeTasks));

        // Post-fix invariant: no ObjectDisposedException from SemaphoreSlim escapes.
        var semaphoreOdes = connectExceptions
            .Where(e => e is ObjectDisposedException ode &&
                        (ode.ObjectName?.Contains("Semaphore", StringComparison.Ordinal) == true ||
                         ode.Message.Contains("Semaphore", StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(semaphoreOdes);
        Assert.Empty(disposeExceptions);
    }
}
