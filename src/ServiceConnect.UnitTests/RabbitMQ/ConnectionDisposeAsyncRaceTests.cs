using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
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
    public async Task CreateChannelAsync_RaceLosesToDispose_ThrowsObjectDisposedExceptionNotInvalidOperation()
    {
        // Sequence the race deterministically using the lifecycle-attach hook.
        //
        // Exact race reproduced:
        //   1. Thread A: CreateChannelAsync → ConnectAsync acquires _connectionLock.
        //   2. CreateConnectionCoreAsync: _disposed check passes (still 0), _connection assigned,
        //      _lifecycle.Attach() is called — this is the hook we exploit.
        //   3. The Attach hook fires DisposeAsync on Thread B. DisposeAsync sets _disposed=1
        //      immediately (Interlocked.Exchange needs no lock), then blocks waiting for the lock.
        //   4. Thread A: ConnectAsync releases _connectionLock.
        //   5. Thread B (DisposeAsync): acquires lock, nulls _connection, releases lock.
        //      (Or Thread A's continuation runs first — either way _disposed=1.)
        //   6. Thread A: post-ConnectAsync re-check (the new fix) sees _disposed=1 → ODE("Connection").
        //      Without the fix: conn = Volatile.Read(ref _connection) → could be null → IOE,
        //      or conn = fakeConnection (captured before step 5) → mock's CreateChannelAsync
        //      throws ODE("IConnection") — wrong ObjectName, test fails either way.
        //
        // The test asserts ODE with ObjectName="Connection". With the fix the re-check throws
        // exactly that. Without the fix the thread-racing outcome produces either IOE or
        // ODE("IConnection") depending on scheduling — both fail the assertion.
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var connection = new Connection(transport.Object, "test-queue", NullLogger.Instance);

        // Tight dispose-lock timeout so DisposeAsync gives up quickly if it can't acquire the lock.
        var timeoutField = typeof(Connection).GetField("_disposeLockTimeout",
            BindingFlags.Instance | BindingFlags.NonPublic);
        timeoutField!.SetValue(connection, TimeSpan.FromMilliseconds(200));

        // Gate: the SetupAdd callback signals this when DisposeAsync has set _disposed=1.
        // Thread A (inside _connectionLock) waits on this before returning from Attach so that
        // _disposed=1 is guaranteed visible at the post-ConnectAsync re-check in CreateChannelAsync.
        var disposedSetSignal = new SemaphoreSlim(0, 1);

        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(false);

        // Without the fix, if Thread A reaches conn.CreateChannelAsync before DisposeAsync
        // nulls _connection, the mock needs to surface a wrong-name ODE so the assertion fails.
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ObjectDisposedException(nameof(IConnection)));

        // When _lifecycle.Attach subscribes to RecoverySucceededAsync, CreateConnectionCoreAsync
        // has already passed its own _disposed check and assigned _connection. We fire DisposeAsync
        // on a pool thread so it sets _disposed=1 (which needs no lock). We then wait in the
        // callback until _disposed=1 is confirmed, so Thread A sees _disposed=1 at the re-check.
        fakeConnection.SetupAdd(c => c.RecoverySucceededAsync += It.IsAny<AsyncEventHandler<AsyncEventArgs>>())
            .Callback(() =>
            {
                // Fire DisposeAsync on a pool thread. Its very first statement is
                // Interlocked.Exchange(ref _disposed, 1) — no lock required.
                _ = Task.Run(async () =>
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    disposedSetSignal.Release();
                });

                // Spin until _disposed=1 is visible on Thread A. DisposeAsync sets _disposed
                // before it tries to acquire the lock, so it's safe to spin here while
                // Thread A holds _connectionLock.
                var disposedField = typeof(Connection).GetField("_disposed",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while ((int)disposedField.GetValue(connection)! == 0 && DateTime.UtcNow < deadline)
                {
                    Thread.SpinWait(100);
                }
            });

        connection.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(fakeConnection.Object);

        var createChannelTask = Task.Run(() => connection.CreateChannelAsync());

        // Wait for DisposeAsync to finish (it acquires the lock after ConnectAsync releases it).
        var signalled = await disposedSetSignal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(signalled, "DisposeAsync did not set _disposed=1 within 5s; synchronization broken.");

        var ex = await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => createChannelTask);
        Assert.Equal(nameof(Connection), ex.ObjectName);
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
