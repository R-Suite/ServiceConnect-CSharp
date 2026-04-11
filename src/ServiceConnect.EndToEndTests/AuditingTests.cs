using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class AuditingTests
{
    private readonly MessagingFixture _fixture;

    public AuditingTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task AuditingEnabled_SuccessfulMessage_CopiedToAuditQueue()
    {
        // Arrange
        var tcs = new TaskCompletionSource<TestMessage>();
        var queueName = _fixture.GetUniqueQueueName("audit-enabled");
        var auditQueueName = _fixture.GetUniqueQueueName("audit-enabled-aq");

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
            new CallbackHandler<TestMessage>(msg => tcs.TrySetResult(msg)));

        services.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.AuditingEnabled = true;
                q.AuditQueueName = auditQueueName;
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Act
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId) { Content = "audit-me" };
            await bus.PublishAsync(sent);

            // Assert: handler receives message
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            var received = await tcs.Task;
            Assert.Equal("audit-me", received.Content);

            // Assert: message also appears in audit queue
            await Task.Delay(1000); // allow audit publish to complete

            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword
            };
            using var conn = factory.CreateConnection();
            using var channel = conn.CreateModel();

            BasicGetResult? auditMsg = null;
            for (int i = 0; i < 10 && auditMsg == null; i++)
            {
                auditMsg = channel.BasicGet(auditQueueName, autoAck: true);
                if (auditMsg == null) await Task.Delay(500);
            }

            Assert.NotNull(auditMsg);

            // Verify the audited message has the original headers
            var headers = auditMsg.BasicProperties.Headers;
            Assert.True(headers.ContainsKey("MessageId"));
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task AuditingDisabled_SuccessfulMessage_NotCopiedToAuditQueue()
    {
        // Arrange
        var tcs = new TaskCompletionSource<TestMessage>();
        var queueName = _fixture.GetUniqueQueueName("audit-disabled");
        var auditQueueName = _fixture.GetUniqueQueueName("audit-disabled-aq");

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
            new CallbackHandler<TestMessage>(msg => tcs.TrySetResult(msg)));

        services.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.AuditingEnabled = false; // default, explicit for clarity
                q.AuditQueueName = auditQueueName;
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Act
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId) { Content = "no-audit" };
            await bus.PublishAsync(sent);

            // Assert: handler receives message
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            var received = await tcs.Task;
            Assert.Equal("no-audit", received.Content);

            // Assert: audit queue should be empty (or not even created)
            await Task.Delay(2000); // wait to be sure nothing arrives

            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword
            };
            using var conn = factory.CreateConnection();
            using var channel = conn.CreateModel();

            // Queue may not exist at all if auditing is disabled — BasicGet on
            // a non-existent queue throws, so declare it passively first
            try
            {
                var auditMsg = channel.BasicGet(auditQueueName, autoAck: true);
                Assert.Null(auditMsg);
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException)
            {
                // Queue doesn't exist — expected when auditing is disabled
            }
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
