using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that WaitForShutdownOperationAsync attaches a benign continuation to any
/// deadline-abandoned RPC task so the post-deadline fault is observed rather than
/// firing TaskScheduler.UnobservedTaskException.
/// </summary>
public sealed class RabbitMqConsumerHostShutdownDeadlineTests
{
    [Fact]
    public async Task WaitForShutdownOperationAsync_DeadlineExpired_RpcFaultIsObservedAndDoesNotFireUnobservedTaskException()
    {
        // Build host and reflect out the private method.
        var host = BuildHost();
        await host.PrepareAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "deadline-q");

        var method = typeof(RabbitMqConsumerHost).GetMethod(
            "WaitForShutdownOperationAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        var unobservedFaults = new List<Exception>();

        void UnobservedHandler(object? _, UnobservedTaskExceptionEventArgs args)
        {
            args.SetObserved(); // prevent process crash
            lock (unobservedFaults) { unobservedFaults.Add(args.Exception); }
        }

        TaskScheduler.UnobservedTaskException += UnobservedHandler;
        try
        {
            // Run in an isolated helper so all strong Task refs drop out of scope before GC.
            InvokeDeadlinePathAndFaultRpc(host, method!);

            // Force finalisation. Pre-fix: the rpcTask's exception is unobserved → fires
            // UnobservedTaskException. Post-fix: ObserveAbandonedRpc accessed t.Exception →
            // the task is already observed → finaliser does nothing.
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Yield();
            }

            lock (unobservedFaults)
            {
                Assert.Empty(unobservedFaults);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= UnobservedHandler;
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// Calls WaitForShutdownOperationAsync with an expired deadline, then faults the RPC task.
    /// All references to the TaskCompletionSource and the rpcTask are local to this frame and
    /// drop out of scope on return, making the task eligible for GC finalisation.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void InvokeDeadlinePathAndFaultRpc(RabbitMqConsumerHost host, MethodInfo method)
    {
        var rpcTcs = new TaskCompletionSource();
        var rpcTask = rpcTcs.Task;

        // DateTimeOffset.MinValue forces remaining <= TimeSpan.Zero immediately.
        var deadline = DateTimeOffset.MinValue;
        var resultTask = (Task<bool>)method.Invoke(host, [rpcTask, deadline])!;
#pragma warning disable VSTHRD002 // the zero-remaining path returns a synchronously-completed Task; no deadlock risk.
        resultTask.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        // Fault the rpc. Post-fix, ObserveAbandonedRpc's ExecuteSynchronously continuation
        // runs inline here and accesses t.Exception → the task is marked "observed".
        rpcTcs.TrySetException(new InvalidOperationException("simulated post-deadline RPC abort (channel close)"));

        // rpcTcs and rpcTask go out of scope here. The task holds the continuation tree
        // registered by ObserveAbandonedRpc (post-fix) but has no external strong references.
    }

    private static RabbitMqConsumerHost BuildHost()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());
        transport.SetupGet(t => t.GracefulShutdownTimeoutMilliseconds).Returns(1000);
        transport.SetupGet(t => t.PrefetchCount).Returns((ushort)10);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("deadline-q");
        queueConfig.SetupGet(q => q.DisableErrors).Returns(false);
        queueConfig.SetupGet(q => q.ErrorQueueName).Returns("deadline-q.errors");
        queueConfig.SetupGet(q => q.AuditQueueName).Returns(string.Empty);
        queueConfig.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);

        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);
        busConfig.SetupGet(b => b.DeadLetterUnhandledMessages).Returns(false);

        var channel = new Mock<IChannel>(MockBehavior.Loose);
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicQosAsync(
                It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.BasicConsumeAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("tag");
        channel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        channel.SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        channel.SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>())).ReturnsAsync(channel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(channel.Object);
        conn.SetupGet(c => c.UnderlyingConnection).Returns((global::RabbitMQ.Client.IConnection?)null);

        var retryHandler = new MessageRetryHandler(0, "deadline-q.errors", "deadline-q", NullLogger.Instance);
        var auditPublisher = new MessageAuditPublisher(queueConfig.Object);
        var admissionGate = new RabbitMqAdmissionGate("deadline-q");

        return new RabbitMqConsumerHost(
            conn.Object, transport.Object, queueConfig.Object, busConfig.Object,
            retryHandler, admissionGate, auditPublisher, NullLogger.Instance);
    }
}
