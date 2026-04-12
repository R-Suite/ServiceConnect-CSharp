using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class RetryAndErrorQueueTests
{
    private readonly MessagingFixture _fixture;

    public RetryAndErrorQueueTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task HandlerFailure_MessageRetriedUpToMaxRetries_ThenSentToErrorQueue()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("retry-error");
        var errorQueueName = _fixture.GetUniqueQueueName("retry-error-eq");
        int attemptCount = 0;
        const int maxRetries = 2;
        const int retryDelay = 1000; // 1 second

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
            new CallbackHandler<TestMessage>(_ =>
            {
                Interlocked.Increment(ref attemptCount);
                throw new InvalidOperationException("Simulated handler failure");
            }));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.MaxRetries = maxRetries;
                t.RetryDelay = retryDelay;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.ErrorQueueName = errorQueueName;
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId) { Content = "will-fail" };
            await bus.PublishAsync(sent);

            // Assert: poll error queue for the dead-lettered message
            // Wait long enough for retries: (maxRetries * retryDelay) + buffer
            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword
            };
            await using var conn = await factory.CreateConnectionAsync();
            await using var channel = await conn.CreateChannelAsync();

            BasicGetResult? errorMsg = null;
            for (int i = 0; i < 30 && errorMsg == null; i++)
            {
                errorMsg = await channel.BasicGetAsync(errorQueueName, autoAck: true);
                if (errorMsg == null) await Task.Delay(1000);
            }

            Assert.NotNull(errorMsg);

            // Verify RetryCount header equals maxRetries
            var headers = errorMsg.BasicProperties.Headers!;
            Assert.True(headers.ContainsKey("RetryCount"));
            Assert.Equal(maxRetries, (int)headers["RetryCount"]!);

            // Verify Exception header is present with serialized exception details
            Assert.True(headers.ContainsKey("Exception"));
            var exceptionJson = Encoding.UTF8.GetString((byte[])headers["Exception"]!);
            Assert.Contains("Simulated handler failure", exceptionJson);

            // Handler should have been called 1 (initial) + maxRetries times
            Assert.Equal(1 + maxRetries, attemptCount);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task HandlerFailure_TransientError_MessageRetriedAndSucceeds()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("retry-transient");
        var errorQueueName = _fixture.GetUniqueQueueName("retry-transient-eq");
        var tcs = new TaskCompletionSource<TestMessage>();
        int attemptCount = 0;
        const int failuresBeforeSuccess = 1;

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
                int attempt = Interlocked.Increment(ref attemptCount);
                if (attempt <= failuresBeforeSuccess)
                    throw new InvalidOperationException("Transient failure");
                tcs.TrySetResult(msg);
            }));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.MaxRetries = 3;
                t.RetryDelay = 1000;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.ErrorQueueName = errorQueueName;
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId) { Content = "transient-recovery" };
            await bus.PublishAsync(sent);

            // Assert: message eventually processed successfully
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            var received = await tcs.Task;

            Assert.Equal("transient-recovery", received.Content);
            Assert.Equal(correlationId, received.CorrelationId);
            Assert.Equal(1 + failuresBeforeSuccess, attemptCount);

            // Verify error queue is empty (message was NOT dead-lettered)
            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword
            };
            await using var conn = await factory.CreateConnectionAsync();
            await using var channel = await conn.CreateChannelAsync();

            // Declare the error queue passively to check if it has messages
            var errorMsg = await channel.BasicGetAsync(errorQueueName, autoAck: true);
            Assert.Null(errorMsg);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
