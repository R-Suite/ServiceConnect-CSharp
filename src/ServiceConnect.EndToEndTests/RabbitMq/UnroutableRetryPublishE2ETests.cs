using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests.RabbitMq;

/// <summary>
/// Verifies the unroutable-retry-publish contract: when a handler throws and the retry
/// queue has been deleted (so the mandatory:true BasicPublishAsync raises PublishException),
/// InboundMessageProcessor catches the PublishException, logs Error, and acks the original
/// message to break the redelivery loop. The original message must not be redelivered to
/// the handler after the retry-publish failure.
/// </summary>
[Collection(nameof(IsolatedCollection))]
public sealed class UnroutableRetryPublishE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RetryQueueGoneAtPublishTime_OriginalMessageAcked_HandlerCalledOnce()
    {
        var queueName = _fixture.GetUniqueQueueName("unroutable-retry");
        var retryQueueName = queueName + ".Retries";

        // Counter shared across DI-resolved handler instances via a captured reference type.
        var callState = new HandlerCallState();

        var handlerReferences = new List<HandlerReference>
        {
            new() { HandlerType = typeof(AlwaysThrowsHandler), MessageType = typeof(UnroutableRetryProbe) }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<UnroutableRetryProbe>>(_ =>
            new AlwaysThrowsHandler(callState));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                // One retry configured so the retry queue is provisioned at startup.
                // After the retry queue is deleted, the mandatory:true publish to it
                // raises PublishException, which InboundMessageProcessor catches and acks.
                t.SetClientSetting("RetryCount", 1);
                t.SetClientSetting("RetrySeconds", 1);
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        await using var sp = services.BuildServiceProvider();
        var bus = sp.GetRequiredService<IBus>();

        // StartConsumingAsync provisions the retry queue topology.
        await bus.StartConsumingAsync();

        try
        {
            // Delete the retry queue out-of-band, AFTER topology provisioning, so the
            // next retry-publish finds no binding and raises PublishException.
            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword,
            };
            await using var sideConn = await factory.CreateConnectionAsync();
            await using var sideChannel = await sideConn.CreateChannelAsync();

            // Passive-declare to confirm the retry queue was provisioned before we delete it.
            await sideChannel.QueueDeclarePassiveAsync(retryQueueName);
            await sideChannel.QueueDeleteAsync(retryQueueName, ifUnused: false, ifEmpty: false);

            // Send a message. The handler will throw, forcing the retry publish path.
            // With the retry queue gone, BasicPublishAsync(mandatory:true) raises PublishException,
            // which InboundMessageProcessor catches and swallows — returning true so EventAsync acks.
            await bus.PublishAsync(new UnroutableRetryProbe(Guid.NewGuid()));

            // Wait for the handler to be called at least once.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            cts.Token.Register(() => callState.CalledTcs.TrySetCanceled());
            await callState.CalledTcs.Task;

            // Wait 2 s more. If the message were nacked with requeue:true (regression), the
            // handler would be called again within this window.
            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.Equal(1, callState.AttemptCount);
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }

    // Scoped message type avoids collision with any other test's fanout exchange.
    private sealed class UnroutableRetryProbe(Guid correlationId) : Message(correlationId);

    // Shared mutable state for handler instances created by DI across multiple invocations.
    private sealed class HandlerCallState
    {
        public int AttemptCount;
        public readonly TaskCompletionSource CalledTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class AlwaysThrowsHandler(HandlerCallState state) : IMessageHandler<UnroutableRetryProbe>
    {
        private readonly HandlerCallState _state = state;

        public IConsumeContext Context { get; set; } = null!;

        public Task HandleAsync(UnroutableRetryProbe message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _state.AttemptCount);
            _state.CalledTcs.TrySetResult();
            throw new InvalidOperationException("Deliberate handler failure for unroutable-retry test.");
        }
    }
}
