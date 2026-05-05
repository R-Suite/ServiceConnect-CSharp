using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class QueueMappingTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendAsync_WithQueueMapping_MessageRoutedToMappedQueue()
    {
        var mappedQueue = _fixture.GetUniqueQueueName("qmap-target");
        var senderQueue = _fixture.GetUniqueQueueName("qmap-sender");
        var tcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Consumer on mapped queue
        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage) }
        };
        var consumerServices = new ServiceCollection();
        consumerServices.AddLogging();
        consumerServices.AddSingleton<IList<HandlerReference>>(handlerRefs);
        consumerServices.AddTransient<IMessageHandler<TestMessage>>(_ => new CallbackHandler<TestMessage>(msg => tcs.TrySetResult(msg)));
        consumerServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3); t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q => q.QueueName = mappedQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var consumerProvider = consumerServices.BuildServiceProvider();
        var consumerBus = consumerProvider.GetRequiredService<IBus>();
        await consumerBus.StartConsumingAsync();

        // Sender with QueueMapping configured (no explicit endpoint)
        var senderServices = new ServiceCollection();
        senderServices.AddLogging();
        senderServices.AddSingleton<IList<HandlerReference>>([]);
        senderServices.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname; t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword; t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3); t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = senderQueue;
                q.AddQueueMapping(typeof(TestMessage), mappedQueue);
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });
        var senderProvider = senderServices.BuildServiceProvider();
        var senderBus = senderProvider.GetRequiredService<IBus>();



        try
        {
            // SendAsync without explicit endpoint — should use QueueMapping
            await senderBus.SendAsync(new TestMessage(Guid.NewGuid()) { Content = "mapped" });

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            var received = await tcs.Task;

            Assert.Equal("mapped", received.Content);
        }
        finally
        {
            await consumerBus.DisposeAsync(); if (consumerProvider is IAsyncDisposable asyncConsumerProvider)
            {
                await asyncConsumerProvider.DisposeAsync();
            }

            await senderBus.DisposeAsync(); if (senderProvider is IAsyncDisposable asyncSenderProvider)
            {
                await asyncSenderProvider.DisposeAsync();
            }
        }
    }
}
