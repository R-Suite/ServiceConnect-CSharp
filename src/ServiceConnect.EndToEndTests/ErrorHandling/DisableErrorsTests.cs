using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class DisableErrorsTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task DisableErrors_DoesNotAffectAuditing_AuditIsStillPublished()
    {
        // Audit is orthogonal to DisableErrors — disabling the error/retry/DLQ topology
        // must NOT also disable audit. Audit is gated separately by IQueueConfiguration
        // .AuditingEnabled inside MessageAuditPublisher; combining the two would silently
        // ack successful messages whenever errors were disabled, losing observability with
        // no operator signal. This test asserts the decoupled contract: with DisableErrors
        // =true AND AuditingEnabled=true, audit messages DO arrive on the audit queue.

        // Arrange
        var tcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("disable-errors");
        var auditQueueName = _fixture.GetUniqueQueueName("disable-errors-aq");

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
        services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(msg => tcs.TrySetResult(msg)));

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
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.AuditingEnabled = true;
                q.AuditQueueName = auditQueueName;
                q.DisableErrors = true;
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
            var sent = new TestMessage(correlationId) { Content = "errors-disabled" };
            await bus.PublishAsync(sent);

            // Assert: handler still receives the message
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            var received = await tcs.Task;
            Assert.Equal("errors-disabled", received.Content);

            // Assert: audit queue DOES receive the message — AuditingEnabled=true is
            // honoured independently of DisableErrors=true.
            await Task.Delay(2000);

            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword
            };
            await using var conn = await factory.CreateConnectionAsync();
            await using var channel = await conn.CreateChannelAsync();

            var auditMsg = await channel.BasicGetAsync(auditQueueName, autoAck: true);
            Assert.NotNull(auditMsg);
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
