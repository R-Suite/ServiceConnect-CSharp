using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PolymorphicMessageTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_DerivedMessage_BaseHandlerReceivesItExactlyOnce()
    {
        // Arrange
        var received = new ConcurrentQueue<TestMessage>();
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("polymorphic");

        // A base-type handler subscribes to a category by registering the CONCRETE subtypes it
        // expects — one HandlerReference per concrete type. That binds the queue to each concrete
        // exchange; the dispatcher's type-hierarchy walk routes the concrete delivery to the
        // base-type handler. Do NOT also register the base type: the producer fans a derived
        // publish out to its own exchange AND every ancestor exchange, so binding the base
        // exchange too would deliver the message twice (and the base copy is re-stamped to the
        // base type, which an abstract base can't even deserialise).
        var handlerReferences = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(DerivedTestMessage) },
        };

        var services = new ServiceCollection();
        services.AddLogging();

        // Register handler references before AddServiceConnect so TryAddSingleton keeps this list
        services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);

        // Register the handler for the BASE type — the hierarchy walk dispatches the concrete
        // delivery to it.
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(msg =>
            {
                received.Enqueue(msg);
                firstReceived.TrySetResult();
            }));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();

        try
        {
            // Act — publish a DERIVED message
            var correlationId = Guid.NewGuid();
            var sent = new DerivedTestMessage(correlationId)
            {
                Content = "polymorphic-test",
                Extra = "extra-data"
            };
            await bus.PublishAsync(sent);

            // Wait up to 30s for the first delivery, then a short grace window to surface any
            // duplicate delivery/invocation before asserting exactly-once.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using (cts.Token.Register(() => firstReceived.TrySetCanceled()))
            {
                await firstReceived.Task;
            }
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Exactly one delivery + one handler invocation — no double-bind, no double-walk.
            Assert.Single(received);
            Assert.True(received.TryPeek(out var msg));
            Assert.Equal("polymorphic-test", msg!.Content);
            Assert.Equal(correlationId, msg.CorrelationId);
            Assert.IsType<DerivedTestMessage>(msg);
            Assert.Equal("extra-data", ((DerivedTestMessage)msg).Extra);
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider)
            {
                await asyncProvider.DisposeAsync();
            }
        }
    }
}
