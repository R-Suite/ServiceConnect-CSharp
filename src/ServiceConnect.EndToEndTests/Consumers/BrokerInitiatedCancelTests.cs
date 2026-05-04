using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests.Consumers;

/// <summary>
/// End-to-end guard that a broker-initiated queue deletion (basic.cancel) is observed
/// and logged by the consumer rather than causing a silent stall. Without ShutdownAsync /
/// ChannelShutdownAsync subscriptions the consumer would never learn its queue was gone.
/// </summary>
[Collection(nameof(IsolatedCollection))]
public class BrokerInitiatedCancelTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    /// <summary>
    /// When the broker deletes the queue the consumer is active on, the consumer host
    /// must detect the shutdown via the ShutdownAsync / ChannelShutdownAsync event
    /// subscription, log a Warning, and allow clean DisposeAsync without hanging.
    /// </summary>
    [Fact]
    [Trait("Category", "Docker")]
    public async Task Consumer_WhenBrokerDeletesQueue_LogsShutdownAndDisposesCleanly()
    {
        var queueName = _fixture.GetUniqueQueueName("broker-cancel");

        // Set up a custom logger provider that captures Warning+ messages.
        var shutdownLogged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capturingProvider = new CapturingLoggerProvider(
            (level, message) =>
            {
                if (level >= LogLevel.Warning && message.Contains("shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    shutdownLogged.TrySetResult(message);
                }
            });

        var handlerReferences = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage)
            }
        };

        var services = new ServiceCollection();
        services.AddLogging(lb => lb.AddProvider(capturingProvider).SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(_ => { /* no-op consumer */ }));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 0);
                t.SetClientSetting("RetrySeconds", 0);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();

        // Delete the queue via a raw connection — this triggers broker-initiated basic.cancel.
        var factory = new ConnectionFactory
        {
            HostName = _fixture.RabbitMqHostname,
            Port = _fixture.RabbitMqPort,
            UserName = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword
        };

        await using var adminConn = await factory.CreateConnectionAsync();
        await using var adminChannel = await adminConn.CreateChannelAsync();
        await adminChannel.QueueDeleteAsync(queueName, ifUnused: false, ifEmpty: false);

        // Wait for the consumer to log the shutdown — must observe within 10 s.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cts.Token.Register(() => shutdownLogged.TrySetCanceled());

        string shutdownMessage;
        try
        {
            shutdownMessage = await shutdownLogged.Task;
        }
        catch (OperationCanceledException)
        {
            // Reaching this branch means ShutdownAsync / ChannelShutdownAsync was never subscribed —
            // the consumer is deaf to broker-initiated cancellation.
            throw new TimeoutException(
                "Consumer did not log a shutdown warning within 10 s after broker deleted the queue. " +
                "ShutdownAsync / ChannelShutdownAsync events are not subscribed.");
        }

        Assert.Contains("shutdown", shutdownMessage, StringComparison.OrdinalIgnoreCase);

        // Also assert that DisposeAsync completes cleanly (no hang) within 15 s.
        // Note: bus.DisposeAsync() is called explicitly here (before provider.DisposeAsync) as
        // a behavioural assertion on the timeout bound; this relies on Connection.DisposeAsync
        // being idempotent since the provider scope will dispose it again via DI teardown.
        using var disposeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var disposeTask = bus.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(Timeout.Infinite, disposeCts.Token));
        Assert.Same(disposeTask, completed);
        await disposeTask; // surface any exceptions

        if (provider is IAsyncDisposable asyncProvider)
        {
            await asyncProvider.DisposeAsync();
        }
    }

    /// <summary>
    /// Minimal ILoggerProvider that invokes a callback on every log call.
    /// </summary>
    private sealed class CapturingLoggerProvider(Action<LogLevel, string> onLog) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(onLog);
        public void Dispose() { }

        private sealed class CapturingLogger(Action<LogLevel, string> onLog) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                onLog(logLevel, message);
            }
        }
    }
}
