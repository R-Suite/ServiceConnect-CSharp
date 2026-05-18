using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Pre-fix only the linkedCts catch in SendRequestAsync / SendRequestMultiAsync /
/// PublishRequestAsync installed the unobserved-fault observer continuation. The
/// user-token catch and catch-all rethrew without observing the TCS, so a registration
/// callback that asynchronously faulted the TCS (e.g. with RequestTimeoutException)
/// could surface as TaskScheduler.UnobservedTaskException at finalization. Post-fix:
/// every catch arm installs the observer.
///
/// The user-token catch path is theoretically safe today because the registration uses
/// TrySetCanceled and cancelled tasks generally don't raise UnobservedTaskException in
/// modern .NET. The catch-all is genuinely exposed when the send pipeline throws non-OCE
/// (e.g. transport disconnect raises IOException); the fault observer makes the path
/// safe regardless of which catch arm wins.
/// </summary>
public sealed class RequestReplyManagerUnobservedFaultObserverTests
{
    [Fact]
    public async Task SendRequestAsync_CatchAllOnTransportException_FaultedTcsObserved()
    {
        var unobserved = 0;
        void Handler(object? _, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            Interlocked.Increment(ref unobserved);
        }
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            await RunCatchAllScenarioAsync(static manager =>
                manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                    new FakeMessage1(Guid.NewGuid()),
                    new Dictionary<string, string>(),
                    new RequestOptions { Timeout = 5_000 }));

            ForceFinalization();

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public async Task SendRequestMultiAsync_CatchAllOnTransportException_FaultedTcsObserved()
    {
        var unobserved = 0;
        void Handler(object? _, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            Interlocked.Increment(ref unobserved);
        }
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            await RunCatchAllScenarioAsync(static manager =>
                manager.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
                    new FakeMessage1(Guid.NewGuid()),
                    new Dictionary<string, string>(),
                    new RequestOptions { Timeout = 5_000, ExpectedReplyCount = 1 }));

            ForceFinalization();

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public async Task PublishRequestAsync_CatchAllOnTransportException_FaultedTcsObserved()
    {
        var unobserved = 0;
        void Handler(object? _, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            Interlocked.Increment(ref unobserved);
        }
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var serializer = new Mock<IMessageSerializer>();
            serializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);

            var pipeline = new Mock<ISendMessagePipeline>();
            pipeline
                .Setup(p => p.ExecutePublishMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
                .Throws(new IOException("transport disconnect"));

            var manager = new RequestReplyManager(serializer.Object, pipeline.Object, new BusConfiguration());

            await Assert.ThrowsAsync<IOException>(() =>
                manager.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                    new FakeMessage1(Guid.NewGuid()),
                    new Dictionary<string, string>(),
                    new RequestOptions { Timeout = 5_000, ExpectedReplyCount = 1 },
                    _ => { }));

            ForceFinalization();

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    /// <summary>
    /// The user-token catch is theoretically safe (TrySetCanceled doesn't raise
    /// UnobservedTaskException). This test still exercises the path to confirm the
    /// observer attachment doesn't break the OCE rethrow contract — the bare OCE
    /// must continue to propagate so existing handlers keep working.
    /// </summary>
    [Fact]
    public async Task SendRequestAsync_UserTokenCancelStillThrowsOperationCanceled()
    {
        var unobserved = 0;
        void Handler(object? _, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            Interlocked.Increment(ref unobserved);
        }
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var serializer = new Mock<IMessageSerializer>();
            serializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);

            var pipeline = new Mock<ISendMessagePipeline>();
            pipeline
                .Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
                .Returns(async (SendContext _, CancellationToken ct) =>
                {
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (ct.Register(() => tcs.TrySetCanceled(ct)))
                    {
                        await tcs.Task.ConfigureAwait(false);
                    }
                });

            var manager = new RequestReplyManager(serializer.Object, pipeline.Object, new BusConfiguration());
            using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                manager.SendRequestAsync<FakeMessage1, FakeMessage1>(
                    new FakeMessage1(Guid.NewGuid()),
                    new Dictionary<string, string>(),
                    new RequestOptions { Timeout = 60_000 },
                    callerCts.Token));

            // Must NOT be the typed send-cancel exception — caller-token cancel keeps
            // surfacing as a vanilla OCE for handler compatibility.
            Assert.IsNotType<RequestSendCancelledException>(ex);

            ForceFinalization();

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    private static async Task RunCatchAllScenarioAsync(Func<RequestReplyManager, Task> act)
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);

        var pipeline = new Mock<ISendMessagePipeline>();
        // Synchronous throw of a non-OCE exception forces the catch-all path.
        pipeline
            .Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Throws(new IOException("transport disconnect"));

        var manager = new RequestReplyManager(serializer.Object, pipeline.Object, new BusConfiguration());

        await Assert.ThrowsAsync<IOException>(() => act(manager));
    }

    private static void ForceFinalization()
    {
        // Two GC cycles so the TCS Task that lost its rooted reference to the catch-arm
        // local is collected, its finalizer runs (which is what raises
        // UnobservedTaskException), and then a second collect/wait flushes any pending
        // exception event delivery.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
