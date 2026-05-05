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
public class PoisonMessageRedeliveryTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RetryPublishFailure_DoesNotCauseUnboundedRedelivery()
    {
        // RabbitMqConsumerHost.ProcessMessageAsync must catch publish exceptions inside both
        // HandleFailureAsync and HandleTerminalFailureAsync, log at Error, and still return true
        // so EventAsync acks the message. Letting the exception propagate would set processed=false
        // and the outer catch would NACK with requeue:true — a hot-loop on a poison message.
        //
        // Setup: a handler that always throws with MaxRetries=0 and an error queue pre-declared with
        // x-overflow=reject-publish so the broker nacks the terminal-failure publish under publisher
        // confirms.
        //
        // NOTE: RabbitMqConsumerHost's _publishChannel is ALWAYS created with publisher confirms
        // enabled (hardcoded in StartConsumingAsync — CreateChannelOptions(publisherConfirmationsEnabled:true)).
        // The reject-publish on the error queue will therefore surface as a thrown exception from
        // BasicPublishAsync inside MessageRetryHandler.PublishErrorAsync, exercising the redelivery path.
        //
        // We pass matching UtilityQueueArguments to the bus so its QueueDeclareAsync call sees
        // equivalent arguments and doesn't fail with PRECONDITION_FAILED — inequivalent arg.

        var queueName = _fixture.GetUniqueQueueName("poison");
        var errorQueueName = _fixture.GetUniqueQueueName("poison-eq");
        int attemptCount = 0;

        var errorQueueArgs = new Dictionary<string, object?>
        {
            ["x-max-length"] = 0,
            ["x-overflow"] = "reject-publish",
        };

        // Pre-declare the error queue with reject-publish so the broker nacks publishes to it
        // once it hits the max-length of 0. This forces HandleTerminalFailureAsync to throw
        // under publisher confirms, simulating the broker-outage scenario from the issue.
        var factory = new ConnectionFactory
        {
            HostName = _fixture.RabbitMqHostname,
            Port = _fixture.RabbitMqPort,
            UserName = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword,
        };
        await using (var conn = await factory.CreateConnectionAsync())
        await using (var channel = await conn.CreateChannelAsync())
        {
            // Declare the exchange first (bus expects a direct exchange with same name as queue)
            await channel.ExchangeDeclareAsync(errorQueueName, ExchangeType.Direct, durable: true, autoDelete: false);
            // Declare queue with special args. Must match exactly what we pass to UtilityQueueArguments
            // below so the bus's subsequent QueueDeclareAsync (isInitialSetup=true) sees equivalent args.
            await channel.QueueDeclareAsync(errorQueueName, durable: true, exclusive: false, autoDelete: false, arguments: errorQueueArgs);
            await channel.QueueBindAsync(errorQueueName, errorQueueName, string.Empty);
        }

        var handlerReferences = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage),
            },
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
                t.MaxRetries = 0; // every failure → terminal-failure → publishes to error exchange
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                // Pass the same args the queue was pre-declared with so the bus's QueueDeclareAsync
                // sees equivalent arguments and doesn't fail with inequivalent_arg on initial setup.
                t.SetClientSetting("UtilityQueueArguments", errorQueueArgs);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
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
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId) { Content = "poison" };
            await bus.PublishAsync(sent);

            // Observe for 10 seconds. Redelivery loop symptom: attemptCount grows rapidly.
            await Task.Delay(TimeSpan.FromSeconds(10));

            // HandleTerminalFailureAsync throws (broker rejects the publish), the inner catch
            // swallows it, ProcessMessageAsync returns true, and EventAsync acks the message —
            // so the handler is called exactly once and there is no redelivery.
            Assert.True(attemptCount == 1,
                $"Expected the handler to be invoked exactly once (attemptCount == 1). " +
                $"attemptCount={attemptCount}. Unbounded redelivery loop may have regressed, or the message was never delivered.");
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
