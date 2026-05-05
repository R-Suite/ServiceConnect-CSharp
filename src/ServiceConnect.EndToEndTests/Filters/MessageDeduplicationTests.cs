using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

file sealed class TestDeduplicationFilter : IFilter
{
    // Business-level dedup key supplied by the caller. Not a reserved transport header —
    // the transport's MessageId is server-authoritative and cannot be used here.
    public const string BusinessIdHeader = "TestBusinessId";

    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (!envelope.Headers.TryGetValue(BusinessIdHeader, out var rawId))
        {
            return Task.FromResult(FilterAction.Continue);
        }

        var messageId = rawId is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : rawId?.ToString() ?? string.Empty;

        if (string.IsNullOrEmpty(messageId))
        {
            return Task.FromResult(FilterAction.Continue);
        }

        if (_seen.TryAdd(messageId, 0))
        {
            return Task.FromResult(FilterAction.Continue);
        }

        // Already seen — block if this is a redelivery
        if (!envelope.Headers.TryGetValue(HeaderKeys.Redelivered, out var rawRedelivered))
        {
            return Task.FromResult(FilterAction.Continue);
        }

        var redeliveredStr = rawRedelivered is byte[] redeliveredBytes
            ? Encoding.UTF8.GetString(redeliveredBytes)
            : rawRedelivered?.ToString() ?? string.Empty;

        var isRedelivery = string.Equals(redeliveredStr, "True", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(isRedelivery ? FilterAction.Stop : FilterAction.Continue);
    }
}

[Collection(nameof(MessagingCollection))]
public class MessageDeduplicationTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task DeduplicationFilter_BlocksDuplicateRedeliveredMessage()
    {
        // Arrange
        var receivedMessages = new ConcurrentBag<string>();
        var firstReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("dedup");
        var sharedBusinessId = Guid.NewGuid().ToString();
        var dedupFilter = new TestDeduplicationFilter();

        var handlerReferences = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage)
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
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
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
            // Act — send the first message with a specific business dedup id; handler should process it
            var firstMessage = new TestMessage(Guid.NewGuid()) { Content = "first-delivery" };
            await bus.PublishAsync(firstMessage, new PublishOptions
            {
                Headers = new Dictionary<string, string>
                {
                    [TestDeduplicationFilter.BusinessIdHeader] = sharedBusinessId
                }
            });

            // Wait for the first message to arrive
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts1.Token.Register(() => firstReceived.TrySetCanceled());
            await firstReceived.Task;

            // Send second message with same business id AND Redelivered = "True" — filter should block it
            var secondMessage = new TestMessage(Guid.NewGuid()) { Content = "second-delivery-duplicate" };
            await bus.PublishAsync(secondMessage, new PublishOptions
            {
                Headers = new Dictionary<string, string>
                {
                    [TestDeduplicationFilter.BusinessIdHeader] = sharedBusinessId,
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
            if (provider is IAsyncDisposable asyncProvider)
            {
                await asyncProvider.DisposeAsync();
            }
        }
    }
}
