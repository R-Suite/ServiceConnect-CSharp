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
public class StreamingTests
{
    private readonly MessagingFixture _fixture;

    public StreamingTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task CreateStream_WritesChunks_HandlerReceivesCompleteData()
    {
        // Arrange
        var completed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerQueue = _fixture.GetUniqueQueueName("stream-consumer");
        var producerQueue = _fixture.GetUniqueQueueName("stream-producer");

        // Serialize a TestMessage so the StreamProcessor can deserialize the reassembled bytes
        var originalMessage = new TestMessage(Guid.NewGuid()) { Content = "streamed-content" };
        var serializedBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(originalMessage));

        // Split serialized bytes into 3 chunks
        var chunkSize = serializedBytes.Length / 3;
        var chunk1 = serializedBytes[..chunkSize];
        var chunk2 = serializedBytes[chunkSize..(chunkSize * 2)];
        var chunk3 = serializedBytes[(chunkSize * 2)..];

        // Consumer bus with IStreamHandler<TestMessage>
        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(TestStreamHandler),
                MessageType = typeof(TestMessage)
            }
        };

        var consumerServices = new ServiceCollection();
        consumerServices.AddLogging();
        consumerServices.AddSingleton<IList<HandlerReference>>(handlerRefs);
        consumerServices.AddSingleton(completed);
        consumerServices.AddTransient<IStreamHandler<TestMessage>, TestStreamHandler>();

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
            builder.ConfigureQueues(q => q.QueueName = producerQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var producerProvider = producerServices.BuildServiceProvider();
        var producerBus = producerProvider.GetRequiredService<IBus>();

        try
        {
            // Act: create stream, write chunks, close
            await using var stream = producerBus.CreateStream<TestMessage>(consumerQueue);

            await stream.WriteAsync(chunk1, 0, chunk1.Length);
            await stream.WriteAsync(chunk2, 0, chunk2.Length);
            await stream.WriteAsync(chunk3, 0, chunk3.Length);
            await stream.CloseAsync();

            // Assert: wait for handler to receive the complete reassembled data
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => completed.TrySetCanceled());
            var receivedBytes = await completed.Task;

            Assert.Equal(serializedBytes, receivedBytes);
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

file class TestStreamHandler : IStreamHandler<TestMessage>
{
    private readonly TaskCompletionSource<byte[]> _tcs;

    public TestStreamHandler(TaskCompletionSource<byte[]> tcs) => _tcs = tcs;

    public IMessageBusReadStream Stream { get; set; } = null!;

    public void Execute(TestMessage message)
    {
        var data = Stream.Read();
        _tcs.TrySetResult(data);
    }
}
