using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies the source-generated connection-lifecycle log entries on
/// <see cref="RabbitMqClientLog"/>:
///   - ConnectionOpened (Information) emitted from <see cref="Connection"/>
///     immediately after a fresh <see cref="IConnection"/> is established.
///   - ConnectionRecovered (Information) emitted from
///     <c>RecoverySucceededAsync</c> after auto-recovery rejoins the broker.
///   - ConnectionLost (Information) emitted from
///     <c>ConnectionShutdownAsync</c> when the broker or transport drops the
///     connection.
/// All three carry stable EventIds (2, 4, 5) so log-aggregation pipelines can
/// pin alerts without scraping the formatted message.
/// </summary>
public sealed class ConnectionLifecycleLogsTests
{
    [Fact]
    public async Task CreateConnection_EmitsConnectionOpened_AtInformation()
    {
        var (connection, fakeConn, fakeLogger) = Build();

        // Trigger lazy connection establishment via the public CreateChannelAsync entry
        // point — that's the path operators actually take and the one the source-gen
        // log lives on.
        await connection.CreateChannelAsync(default);

        var record = Assert.Single(
            fakeLogger.Collector.GetSnapshot(),
            r => r.Id.Id == RabbitMqClientLog.ConnectionOpenedEventId);
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Contains("rabbit.example.com", record.Message);
        Assert.Contains("5672", record.Message);
        Assert.Contains("connection-name", record.Message);
        // Vhost falls back to "/" — TransportConfiguration ships the empty default,
        // so the test exercises the fallback path the production setup actually hits.
        Assert.Contains("vhost='/'", record.Message);
        await connection.DisposeAsync();
        // Subscribe-before-attach is verified implicitly: the connection-recovered and
        // connection-lost handlers are now attached on fakeConn (see other tests).
        _ = fakeConn;
    }

    [Fact]
    public async Task RecoverySucceeded_EmitsConnectionRecovered_AtInformation()
    {
        var (connection, fakeConn, fakeLogger) = Build();
        await connection.CreateChannelAsync(default);
        // Drop the ConnectionOpened entry from the snapshot baseline so the assertion
        // pins the post-event state precisely.
        fakeLogger.Collector.Clear();

        await fakeConn.RaiseRecoverySucceededAsync();

        var record = Assert.Single(fakeLogger.Collector.GetSnapshot());
        Assert.Equal(RabbitMqClientLog.ConnectionRecoveredEventId, record.Id.Id);
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Contains("rabbit.example.com", record.Message);
        Assert.Contains("connection-name", record.Message);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionShutdown_EmitsConnectionLost_AtInformation()
    {
        var (connection, fakeConn, fakeLogger) = Build();
        await connection.CreateChannelAsync(default);
        fakeLogger.Collector.Clear();

        await fakeConn.RaiseConnectionShutdownAsync(
            new ShutdownEventArgs(ShutdownInitiator.Peer, replyCode: 320, replyText: "CONNECTION_FORCED - broker shutdown"));

        var record = Assert.Single(fakeLogger.Collector.GetSnapshot());
        Assert.Equal(RabbitMqClientLog.ConnectionLostEventId, record.Id.Id);
        Assert.Equal(LogLevel.Information, record.Level);
        Assert.Contains("rabbit.example.com", record.Message);
        Assert.Contains("Peer", record.Message);
        Assert.Contains("CONNECTION_FORCED", record.Message);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_DetachesLifecycleHandlers()
    {
        var (connection, fakeConn, fakeLogger) = Build();
        await connection.CreateChannelAsync(default);
        await connection.DisposeAsync();

        fakeLogger.Collector.Clear();
        // After dispose the handlers must be unsubscribed; raising the events should be a no-op.
        await fakeConn.RaiseRecoverySucceededAsync();
        await fakeConn.RaiseConnectionShutdownAsync(
            new ShutdownEventArgs(ShutdownInitiator.Application, 0, "post-dispose"));

        Assert.Empty(fakeLogger.Collector.GetSnapshot());
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="Connection"/> wired to a <see cref="FakeUnderlyingConnection"/>
    /// and a <see cref="FakeLogger"/>. The connection is not yet established; the caller
    /// drives it through <see cref="Connection.CreateChannelAsync(CancellationToken)"/>.
    /// </summary>
    private static (Connection connection, FakeUnderlyingConnection fakeConn, FakeLogger<ConnectionLifecycleTag> fakeLogger) Build()
    {
        var fakeConn = new FakeUnderlyingConnection
        {
            EndpointHostName = "rabbit.example.com",
            EndpointPort = 5672,
            ClientProvidedName = "connection-name",
        };

        // Channel-open is needed because CreateChannelAsync delegates to the underlying
        // IConnection. Loose mock with a default IChannel is enough here.
        var channel = Mock.Of<IChannel>();
        fakeConn.ChannelToReturn = channel;

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("rabbit.example.com");
        transport.SetupGet(t => t.VirtualHost).Returns(string.Empty);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var fakeLogger = new FakeLogger<ConnectionLifecycleTag>();

        var connection = new Connection(transport.Object, "test-queue", fakeLogger)
        {
            CreateConnectionForTests = (_, _, _, _) => Task.FromResult<IConnection>(fakeConn),
        };

        return (connection, fakeConn, fakeLogger);
    }

    /// <summary>Placeholder type so FakeLogger has a category — the actual ILogger
    /// passed to <see cref="Connection"/> is the non-generic <see cref="ILogger"/>.</summary>
    public sealed class ConnectionLifecycleTag { }

    // ── Test double ───────────────────────────────────────────────────────────

    /// <summary>
    /// Concrete stub that implements only the parts of <see cref="IConnection"/>
    /// the lifecycle hooks actually touch. Exposes RaiseXxx helpers so tests can
    /// fire <c>RecoverySucceededAsync</c> / <c>ConnectionShutdownAsync</c> directly,
    /// which Moq cannot do cleanly for AsyncEventHandler-shaped events.
    /// </summary>
    private sealed class FakeUnderlyingConnection : IConnection
    {
        public string EndpointHostName { get; set; } = "localhost";
        public int EndpointPort { get; set; } = 5672;
        public string ClientProvidedName { get; set; } = string.Empty;
        public IChannel? ChannelToReturn { get; set; }

        private AsyncEventHandler<AsyncEventArgs>? _recoverySucceeded;
        private AsyncEventHandler<ShutdownEventArgs>? _connectionShutdown;

        public event AsyncEventHandler<AsyncEventArgs> RecoverySucceededAsync
        {
            add => _recoverySucceeded += value;
            remove => _recoverySucceeded -= value;
        }

        public event AsyncEventHandler<ShutdownEventArgs> ConnectionShutdownAsync
        {
            add => _connectionShutdown += value;
            remove => _connectionShutdown -= value;
        }

        public Task RaiseRecoverySucceededAsync()
        {
            var handler = _recoverySucceeded;
            return handler is not null ? handler(this, AsyncEventArgs.CreateOrDefault(CancellationToken.None)) : Task.CompletedTask;
        }

        public Task RaiseConnectionShutdownAsync(ShutdownEventArgs args)
        {
            var handler = _connectionShutdown;
            return handler is not null ? handler(this, args) : Task.CompletedTask;
        }

        public AmqpTcpEndpoint Endpoint => new(EndpointHostName, EndpointPort);
        string IConnection.ClientProvidedName => ClientProvidedName;

        public bool IsOpen => true;

        public Task<IChannel> CreateChannelAsync(CreateChannelOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(ChannelToReturn ?? throw new InvalidOperationException("ChannelToReturn not set"));

        public Task CloseAsync(ushort reasonCode, string reasonText, TimeSpan timeout, bool abort, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateSecretAsync(string newSecret, string reason, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // ── Unused IConnection members ────────────────────────────────────────

        public ushort ChannelMax => throw new NotImplementedException();
        public IDictionary<string, object?> ClientProperties => throw new NotImplementedException();
        public TimeSpan Heartbeat => throw new NotImplementedException();
        public IProtocol Protocol => throw new NotImplementedException();
        public uint FrameMax => throw new NotImplementedException();
        public ShutdownEventArgs? CloseReason => throw new NotImplementedException();
        public IDictionary<string, object?>? ServerProperties => throw new NotImplementedException();
        public IEnumerable<ShutdownReportEntry> ShutdownReport => throw new NotImplementedException();
        public int LocalPort => throw new NotImplementedException();
        public int RemotePort => throw new NotImplementedException();

#pragma warning disable CS0067
        public event AsyncEventHandler<CallbackExceptionEventArgs>? CallbackExceptionAsync;
        public event AsyncEventHandler<ConnectionBlockedEventArgs>? ConnectionBlockedAsync;
        public event AsyncEventHandler<AsyncEventArgs>? ConnectionUnblockedAsync;
        public event AsyncEventHandler<ConnectionRecoveryErrorEventArgs>? ConnectionRecoveryErrorAsync;
        public event AsyncEventHandler<QueueNameChangedAfterRecoveryEventArgs>? QueueNameChangedAfterRecoveryAsync;
        public event AsyncEventHandler<RecoveringConsumerEventArgs>? RecoveringConsumerAsync;
        public event AsyncEventHandler<ConsumerTagChangedAfterRecoveryEventArgs>? ConsumerTagChangeAfterRecoveryAsync;
#pragma warning restore CS0067
    }
}
