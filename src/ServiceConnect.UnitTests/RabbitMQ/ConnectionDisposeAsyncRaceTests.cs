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
                // indicates a connect/dispose race against the SemaphoreSlim leaked through.
                connectExceptions.Add(ex);
            }
        })).ToArray();

        var disposeTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try { await connection.DisposeAsync(); }
            catch (Exception ex) { disposeExceptions.Add(ex); }
        })).ToArray();

        // Let the dispose tasks run a moment before cancelling the hang gate, so the
        // dispose-timeout fires and the lock has been disposed before the connect tasks
        // reach their finally block — this is the window the race would exploit.
        await Task.Delay(200);
        hangGate.TrySetCanceled();

        await Task.WhenAll(connectTasks.Concat(disposeTasks));

        // Invariant: no ObjectDisposedException from SemaphoreSlim escapes.
        var semaphoreOdes = connectExceptions
            .Where(e => e is ObjectDisposedException ode &&
                        (ode.ObjectName?.Contains("Semaphore", StringComparison.Ordinal) == true ||
                         ode.Message.Contains("Semaphore", StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(semaphoreOdes);
        Assert.Empty(disposeExceptions);
    }

    [Fact]
    public async Task DisposeDuringCreate_DoesNotOrphanConnection()
    {
        // Stage: a CreateConnectionForTests that blocks until the test releases. While the
        // create is hanging inside _connectionLock, drive DisposeAsync with a tight lock
        // timeout. DisposeAsync sets _disposed=1 (line 119), times out on _connectionLock,
        // and proceeds to its forced-teardown path — but _connection is still null at this
        // point, so the teardown is a no-op. The just-built connection that the create is
        // about to return must be torn down by the post-build disposed check, NOT orphaned.
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var connection = new Connection(transport.Object, "test-queue", NullLogger.Instance);

        // Tight dispose-lock timeout so DisposeAsync gives up after 50ms instead of 30s.
        var timeoutField = typeof(Connection).GetField("_disposeLockTimeout",
            BindingFlags.Instance | BindingFlags.NonPublic);
        timeoutField!.SetValue(connection, TimeSpan.FromMilliseconds(50));

        var disposeCount = 0;
        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(false); // avoid mocking CloseAsync's overload
        fakeConnection.Setup(c => c.Dispose()).Callback(() => Interlocked.Increment(ref disposeCount));

        var createInvoked = new TaskCompletionSource();
        var createReleased = new TaskCompletionSource();
        connection.CreateConnectionForTests = async (_, _, _, _) =>
        {
            createInvoked.TrySetResult();
            await createReleased.Task; // hold here until the test releases
            return fakeConnection.Object;
        };

        // Begin a connect on a worker task. CreateChannelAsync calls ConnectAsync which
        // acquires _connectionLock and calls into CreateConnectionForTests, blocking.
        var connectTask = Task.Run(async () =>
        {
            try { _ = await connection.CreateChannelAsync(); }
            catch { /* expected — the create will throw ObjectDisposedException post-build */ }
        });
        await createInvoked.Task;

        // DisposeAsync sets _disposed first, fails to acquire the lock within 50ms, falls
        // through to the forced-teardown path, returns. _connection is still null.
        var disposeTask = connection.DisposeAsync().AsTask();
        await disposeTask;

        // Release the create. The post-build disposed check should detect _disposed and
        // tear down the just-built fakeConnection.
        createReleased.TrySetResult();
        await connectTask;

        Assert.Equal(1, disposeCount);
    }
}
