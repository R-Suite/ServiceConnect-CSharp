using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using System.Collections.Concurrent;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class CompetingConsumersTests
{
    private readonly MessagingFixture _fixture;

    public CompetingConsumersTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Send_TwoConsumersSameQueue_EachMessageDeliveredOnce()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("competing");
        var allReceived = new ConcurrentBag<string>();
        var allDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        const int messageCount = 10;
        int totalReceived = 0;

        (IBus bus, ServiceProvider provider) CreateConsumerBus()
        {
            var handlerRefs = new List<HandlerReference>
            {
                new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage) }
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IList<HandlerReference>>(handlerRefs);
            services.AddTransient<IMessageHandler<TestMessage>>(_ =>
                new CallbackHandler<TestMessage>(msg =>
                {
                    allReceived.Add(msg.Content);
                    if (Interlocked.Increment(ref totalReceived) >= messageCount)
                        allDone.TrySetResult(true);
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
                });
                builder.ConfigureQueues(q => q.QueueName = queueName);
                builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            });

            var provider = services.BuildServiceProvider();
            return (provider.GetRequiredService<IBus>(), provider);
        }

        var (bus1, consumerProvider1) = CreateConsumerBus();
        var (bus2, consumerProvider2) = CreateConsumerBus();

        await bus1.StartConsumingAsync();
        await bus2.StartConsumingAsync();
        

        // We need a separate producer bus (its own queue) to send messages
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
            builder.ConfigureQueues(q => q.QueueName = _fixture.GetUniqueQueueName("competing-producer"));
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var producerProvider = producerServices.BuildServiceProvider();
        var producerBus = producerProvider.GetRequiredService<IBus>();

        try
        {
            // Act: send N messages to the shared queue
            for (int i = 0; i < messageCount; i++)
            {
                await producerBus.SendAsync(
                    new TestMessage(Guid.NewGuid()) { Content = $"msg-{i}" },
                    new SendOptions { EndPoint = queueName });
            }

            // Assert: wait for all messages to be received
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => allDone.TrySetCanceled());
            await allDone.Task;

            // All messages received exactly once (no duplicates, no missing)
            var sorted = allReceived.OrderBy(x => x).ToList();
            var expected = Enumerable.Range(0, messageCount).Select(i => $"msg-{i}").OrderBy(x => x).ToList();
            Assert.Equal(expected, sorted);
        }
        finally
        {
            await bus1.DisposeAsync();
            await bus2.DisposeAsync();
            await producerBus.DisposeAsync();
            if (consumerProvider1 is IAsyncDisposable asyncConsumerProvider1) await asyncConsumerProvider1.DisposeAsync();
            if (consumerProvider2 is IAsyncDisposable asyncConsumerProvider2) await asyncConsumerProvider2.DisposeAsync();
            if (producerProvider is IAsyncDisposable asyncProducerProvider) await asyncProducerProvider.DisposeAsync();
        }
    }
}
