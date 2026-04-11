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
public class StreamOutOfOrderTests
{
    private readonly MessagingFixture _fixture;

    public StreamOutOfOrderTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Stream_ManyChunks_ReassembledCorrectly()
    {
        // Arrange
        var completed = new TaskCompletionSource<byte[]>();
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
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
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
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = consumerQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var consumerProvider = consumerServices.BuildServiceProvider();
        var consumerBus = consumerProvider.GetRequiredService<IBus>();
        await consumerBus.StartConsumingAsync();
        await Task.Delay(500);

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
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = producerQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var producerProvider = producerServices.BuildServiceProvider();
        var producerBus = producerProvider.GetRequiredService<IBus>();

        try
        {
            // Act: create stream, write 5 chunks, close
            using var stream = producerBus.CreateStream(consumerQueue, originalMessage);

            stream.Write(chunk1, 0, chunk1.Length);
            stream.Write(chunk2, 0, chunk2.Length);
            stream.Write(chunk3, 0, chunk3.Length);
            stream.Write(chunk4, 0, chunk4.Length);
            stream.Write(chunk5, 0, chunk5.Length);
            stream.Close();

            // Assert: wait for handler to receive reassembled data
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => completed.TrySetCanceled());
            var receivedBytes = await completed.Task;

            Assert.Equal(serializedBytes, receivedBytes);
        }
        finally
        {
            consumerBus.Dispose();
            producerBus.Dispose();
            (consumerProvider as IDisposable)?.Dispose();
            (producerProvider as IDisposable)?.Dispose();
        }
    }
}

file class StreamCaptureHandler : IStreamHandler<TestMessage>
{
    private readonly TaskCompletionSource<byte[]> _tcs;

    public StreamCaptureHandler(TaskCompletionSource<byte[]> tcs) => _tcs = tcs;

    public IMessageBusReadStream Stream { get; set; } = null!;

    public void Execute(TestMessage message)
    {
        _tcs.TrySetResult(Stream.Read());
    }
}
