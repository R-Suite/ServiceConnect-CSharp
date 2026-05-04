using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class MultiEndpointSendTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendAsync_MultipleEndpoints_AllReceiveMessage()
    {
        // Arrange
        var queue1 = _fixture.GetUniqueQueueName("multi-endpoint-1");
        var queue2 = _fixture.GetUniqueQueueName("multi-endpoint-2");
        var senderQueue = _fixture.GetUniqueQueueName("multi-endpoint-sender");

        var tcs1 = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs2 = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        // --- Consumer 1 bus setup ---
        var consumer1HandlerReferences = new List<HandlerReference>
        {
            new() {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage)
            }
        };

        var consumer1Services = new ServiceCollection();
        consumer1Services.AddLogging();
        consumer1Services.AddSingleton<IList<HandlerReference>>(consumer1HandlerReferences);
        consumer1Services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(msg => tcs1.TrySetResult(msg)));

        consumer1Services.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = queue1);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var consumer1Provider = consumer1Services.BuildServiceProvider();
        var consumer1Bus = consumer1Provider.GetRequiredService<IBus>();
        await consumer1Bus.StartConsumingAsync();

        // --- Consumer 2 bus setup ---
        var consumer2HandlerReferences = new List<HandlerReference>
        {
            new() {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage)
            }
        };

        var consumer2Services = new ServiceCollection();
        consumer2Services.AddLogging();
        consumer2Services.AddSingleton<IList<HandlerReference>>(consumer2HandlerReferences);
        consumer2Services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(msg => tcs2.TrySetResult(msg)));

        consumer2Services.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = queue2);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var consumer2Provider = consumer2Services.BuildServiceProvider();
        var consumer2Bus = consumer2Provider.GetRequiredService<IBus>();
        await consumer2Bus.StartConsumingAsync();

        // --- Sender bus setup (no handlers) ---
        var senderServices = new ServiceCollection();
        senderServices.AddLogging();
        senderServices.AddSingleton<IList<HandlerReference>>([]);

        senderServices.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = senderQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var senderProvider = senderServices.BuildServiceProvider();
        var senderBus = senderProvider.GetRequiredService<IBus>();

        // Give consumers time to set up


        try
        {
            // Act
            var correlationId = Guid.NewGuid();
            var message = new TestMessage(correlationId) { Content = "multi-endpoint send" };
            await senderBus.SendToManyAsync(message, [queue1, queue2]);

            // Assert: wait up to 30 seconds for both handlers to be called
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() =>
            {
                tcs1.TrySetCanceled();
                tcs2.TrySetCanceled();
            });

            var received1 = await tcs1.Task;
            var received2 = await tcs2.Task;

            Assert.Equal("multi-endpoint send", received1.Content);
            Assert.Equal(correlationId, received1.CorrelationId);

            Assert.Equal("multi-endpoint send", received2.Content);
            Assert.Equal(correlationId, received2.CorrelationId);
        }
        finally
        {
            await consumer1Bus.DisposeAsync();
            if (consumer1Provider is IAsyncDisposable asyncConsumer1Provider)
            {
                await asyncConsumer1Provider.DisposeAsync();
            }

            await consumer2Bus.DisposeAsync();
            if (consumer2Provider is IAsyncDisposable asyncConsumer2Provider)
            {
                await asyncConsumer2Provider.DisposeAsync();
            }

            await senderBus.DisposeAsync();
            if (senderProvider is IAsyncDisposable asyncSenderProvider)
            {
                await asyncSenderProvider.DisposeAsync();
            }
        }
    }
}
