using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using System.Collections.Concurrent;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(IsolatedCollection))]
public class PriorityQueueTests
{
    private readonly MessagingFixture _fixture;

    public PriorityQueueTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Send_MessagesWithDifferentPriorities_HighPriorityConsumedFirst()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("priority");
        var receivedPriorities = new ConcurrentQueue<int>();
        const int messageCount = 6;
        var allReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int totalReceived = 0;

        // Pre-declare the priority queue using raw RabbitMQ.Client before consumer starts,
        // because RabbitMQ only guarantees priority ordering when messages are already queued.
        var connFactory = new global::RabbitMQ.Client.ConnectionFactory
        {
            HostName = _fixture.RabbitMqHostname,
            Port = _fixture.RabbitMqPort,
            UserName = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword
        };
        await using var rawConn = await connFactory.CreateConnectionAsync();
        await using var rawModel = await rawConn.CreateChannelAsync();
        await rawModel.QueueDeclareAsync(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { { "x-max-priority", 10 } });

        // Set up producer bus (its own queue, only sends)
        var producerServices = new ServiceCollection();
        producerServices.AddLogging();
        producerServices.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        producerServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
            });
            builder.ConfigureQueues(q => q.QueueName = _fixture.GetUniqueQueueName("priority-producer"));
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var producerProvider = producerServices.BuildServiceProvider();
        var producerBus = producerProvider.GetRequiredService<IBus>();

        // Send 6 messages with alternating priorities: 1, 10, 1, 10, 1, 10
        // All messages are queued BEFORE the consumer starts to ensure RabbitMQ priority ordering.
        int[] sendPriorities = { 1, 10, 1, 10, 1, 10 };
        for (int i = 0; i < messageCount; i++)
        {
            var msg = new PriorityMessage(Guid.NewGuid())
            {
                Priority = sendPriorities[i]
            };
            await producerBus.SendAsync(msg, new SendOptions
            {
                EndPoint = queueName,
                Headers = new Dictionary<string, string> { ["Priority"] = sendPriorities[i].ToString() }
            });
        }

        

        // Set up consumer bus AFTER all messages are queued
        var handlerRefs = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(CallbackHandler<PriorityMessage>),
                MessageType = typeof(PriorityMessage)
            }
        };

        var consumerServices = new ServiceCollection();
        consumerServices.AddLogging();
        consumerServices.AddSingleton<IList<HandlerReference>>(handlerRefs);
        consumerServices.AddTransient<IMessageHandler<PriorityMessage>>(_ =>
            new CallbackHandler<PriorityMessage>(msg =>
            {
                receivedPriorities.Enqueue(msg.Priority);
                if (Interlocked.Increment(ref totalReceived) >= messageCount)
                    allReceived.TrySetResult(true);
            }));

        consumerServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                t.SetClientSetting("Arguments", new Dictionary<string, object> { { "x-max-priority", 10 } });
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var consumerProvider = consumerServices.BuildServiceProvider();
        var consumerBus = consumerProvider.GetRequiredService<IBus>();

        try
        {
            await consumerBus.StartConsumingAsync();

            // Assert: wait up to 30 seconds for all messages to be received
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => allReceived.TrySetCanceled());
            await allReceived.Task;

            // All priority-10 messages should come before all priority-1 messages
            var ordered = receivedPriorities.ToArray();
            Assert.Equal(messageCount, ordered.Length);

            // Verify descending priority order: all high-priority first, then all low-priority
            var expectedOrder = ordered.OrderByDescending(p => p).ToArray();
            Assert.Equal(expectedOrder, ordered);
        }
        finally
        {
            await consumerBus.DisposeAsync();
            await producerBus.DisposeAsync();
            if (consumerProvider is IAsyncDisposable asyncConsumerProvider) await asyncConsumerProvider.DisposeAsync();
            if (producerProvider is IAsyncDisposable asyncProducerProvider) await asyncProducerProvider.DisposeAsync();
        }
    }
}
