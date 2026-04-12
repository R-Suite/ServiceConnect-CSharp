using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

file sealed class TestDeduplicationFilter : IFilter
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public IBus Bus { get; set; } = null!;

    public bool Process(Envelope envelope)
    {
        if (!envelope.Headers.TryGetValue(HeaderKeys.MessageId, out var rawId))
            return true; // no MessageId header — let it through

        var messageId = rawId is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : rawId?.ToString() ?? string.Empty;

        if (string.IsNullOrEmpty(messageId))
            return true;

        if (_seen.TryAdd(messageId, 0))
            return true; // first time seeing this ID — allow processing

        // Already seen — block if this is a redelivery
        if (!envelope.Headers.TryGetValue(HeaderKeys.Redelivered, out var rawRedelivered))
            return true;

        var redeliveredStr = rawRedelivered is byte[] redeliveredBytes
            ? Encoding.UTF8.GetString(redeliveredBytes)
            : rawRedelivered?.ToString() ?? string.Empty;

        return !string.Equals(redeliveredStr, "True", StringComparison.OrdinalIgnoreCase);
    }
}

[Collection(nameof(MessagingCollection))]
public class MessageDeduplicationTests
{
    private readonly MessagingFixture _fixture;

    public MessageDeduplicationTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task DeduplicationFilter_BlocksDuplicateRedeliveredMessage()
    {
        // Arrange
        var receivedMessages = new ConcurrentBag<string>();
        var firstReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("dedup");
        var sharedMessageId = Guid.NewGuid().ToString();
        var dedupFilter = new TestDeduplicationFilter();

        var handlerReferences = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();

        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(msg =>
            {
                receivedMessages.Add(msg.Content ?? string.Empty);
                firstReceived.TrySetResult(true);
            }));

        // Register the shared filter instance so DI resolves the same object
        services.AddSingleton<TestDeduplicationFilter>(dedupFilter);

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
            builder.AddBeforeConsumingFilter<TestDeduplicationFilter>();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act — send the first message with a specific MessageId; handler should process it
            var firstMessage = new TestMessage(Guid.NewGuid()) { Content = "first-delivery" };
            await bus.PublishAsync(firstMessage, new PublishOptions
            {
                Headers = new Dictionary<string, string>
                {
                    [HeaderKeys.MessageId] = sharedMessageId
                }
            });

            // Wait for the first message to arrive
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts1.Token.Register(() => firstReceived.TrySetCanceled());
            await firstReceived.Task;

            // Send second message with same MessageId AND Redelivered = "True" — filter should block it
            var secondMessage = new TestMessage(Guid.NewGuid()) { Content = "second-delivery-duplicate" };
            await bus.PublishAsync(secondMessage, new PublishOptions
            {
                Headers = new Dictionary<string, string>
                {
                    [HeaderKeys.MessageId] = sharedMessageId,
                    [HeaderKeys.Redelivered] = "True"
                }
            });

            // Wait 2 seconds to give the second message a chance to be processed (it should not be)
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Assert: handler was invoked exactly once — the duplicate was blocked
            Assert.Single(receivedMessages);
            Assert.Equal("first-delivery", receivedMessages.First());
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider) await asyncProvider.DisposeAsync();
        }
    }
}
