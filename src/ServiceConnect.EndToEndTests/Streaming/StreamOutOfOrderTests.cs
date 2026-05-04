using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class StreamOutOfOrderTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Stream_MultipleChunks_ReassembledCorrectly()
    {
        // Arrange
        var completed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerQueue = _fixture.GetUniqueQueueName("stream-ooo-consumer");
        var producerQueue = _fixture.GetUniqueQueueName("stream-ooo-producer");

        var originalMessage = new TestMessage(Guid.NewGuid()) { Content = "multi-chunk-stream" };
        var serializedBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(originalMessage));

        // Split serialized bytes into 5 chunks
        var chunkSize = serializedBytes.Length / 5;
        var chunk1 = serializedBytes[..chunkSize];
        var chunk2 = serializedBytes[chunkSize..(chunkSize * 2)];
        var chunk3 = serializedBytes[(chunkSize * 2)..(chunkSize * 3)];
        var chunk4 = serializedBytes[(chunkSize * 3)..(chunkSize * 4)];
        var chunk5 = serializedBytes[(chunkSize * 4)..];

        // Consumer bus with IStreamHandler<TestMessage>
        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(StreamCaptureHandler),
                MessageType = typeof(TestMessage)
            }
        };

        var consumerServices = new ServiceCollection();
        consumerServices.AddLogging();
        consumerServices.AddSingleton<IList<HandlerReference>>(handlerRefs);
        consumerServices.AddSingleton(completed);
        consumerServices.AddSingleton(completed);
        consumerServices.AddTransient<IStreamHandler<TestMessage>, StreamCaptureHandler>();

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
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q => q.QueueName = consumerQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var consumerProvider = consumerServices.BuildServiceProvider();
        var consumerBus = consumerProvider.GetRequiredService<IBus>();
        await consumerBus.StartConsumingAsync();


        // Producer bus
        var producerServices = new ServiceCollection();
        producerServices.AddLogging();
        producerServices.AddSingleton<IList<HandlerReference>>([]);
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
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q => q.QueueName = producerQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var producerProvider = producerServices.BuildServiceProvider();
        var producerBus = producerProvider.GetRequiredService<IBus>();

        try
        {
            // Act: create stream, write 5 chunks, close
            await using var stream = producerBus.CreateStream<TestMessage>(consumerQueue);

            await stream.WriteAsync(chunk1);
            await stream.WriteAsync(chunk2);
            await stream.WriteAsync(chunk3);
            await stream.WriteAsync(chunk4);
            await stream.WriteAsync(chunk5);
            await stream.CloseAsync();

            // Assert: wait for handler to receive reassembled data
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => completed.TrySetCanceled());
            var receivedBytes = await completed.Task;

            Assert.Equal(serializedBytes, receivedBytes);
        }
        finally
        {
            await consumerBus.DisposeAsync();
            await producerBus.DisposeAsync();
            if (consumerProvider is IAsyncDisposable asyncConsumerProvider)
            {
                await asyncConsumerProvider.DisposeAsync();
            }

            if (producerProvider is IAsyncDisposable asyncProducerProvider)
            {
                await asyncProducerProvider.DisposeAsync();
            }
        }
    }
}

file class StreamCaptureHandler(TaskCompletionSource<byte[]> tcs) : IStreamHandler<TestMessage>
{
    private readonly TaskCompletionSource<byte[]> _tcs = tcs;

    public Task ExecuteAsync(TestMessage message, IMessageBusReadStream stream, CancellationToken cancellationToken = default)
    {
        _tcs.TrySetResult(stream.Read());
        return Task.CompletedTask;
    }
}
