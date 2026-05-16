using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(IsolatedCollection))]
public class CancellationE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    private ServiceProvider BuildBus(string queueName, out IBus bus)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IReadOnlyList<HandlerReference>>([]);

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
                q.AddQueueMapping(typeof(CancellationTestRequest), "cancellation-test-never-replied");
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        bus = provider.GetRequiredService<IBus>();
        return provider;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task StartConsumingAsync_WithPreCancelledToken_DoesNotHang()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("cancellation-start");
        await using var provider = BuildBus(queueName, out var bus);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            // Act + Assert: pre-cancelled token must throw OCE quickly (no hang)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => bus.StartConsumingAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendRequestAsync_ExternalCancel_ThrowsOCE()
    {
        // Arrange — a requester bus that starts consuming (needed for reply routing),
        // but there is no responder, so the request will never be answered.
        var requesterQueue = _fixture.GetUniqueQueueName("cancellation-request");
        await using var provider = BuildBus(requesterQueue, out var bus);
        await bus.StartConsumingAsync();

        // Producer now publishes with mandatory:true so unrouted sends surface as
        // PublishException. The request target ("cancellation-test-never-replied") must
        // exist as a real queue for the test's "request fires, reply never arrives, CT
        // wins the race" scenario — otherwise the send itself fails before cancellation
        // gets a chance to race. The queue stays unconsumed so no reply is ever produced.
        var factory = new global::RabbitMQ.Client.ConnectionFactory
        {
            HostName = _fixture.RabbitMqHostname,
            Port = _fixture.RabbitMqPort,
            UserName = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword,
        };
        await using (var conn = await factory.CreateConnectionAsync())
        await using (var ch = await conn.CreateChannelAsync())
        {
            await ch.QueueDeclareAsync("cancellation-test-never-replied", durable: false, exclusive: false, autoDelete: true);
        }

        using var cts = new CancellationTokenSource();

        // Use a very long internal timeout so the CT races with the request, not the timeout
        var options = new RequestOptions { Timeout = 5 * 60 * 1000 };

        try
        {
            // Act: kick off the request then cancel externally after a short delay
            var task = bus.SendRequestAsync<CancellationTestRequest, CancellationTestReply>(
                new CancellationTestRequest(Guid.NewGuid()),
                options,
                cts.Token);

            cts.CancelAfter(200);

            // Assert: OCE must arrive within 5 seconds (no hang)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }
}

public class CancellationTestRequest(Guid correlationId) : Message(correlationId)
{
}

public class CancellationTestReply(Guid correlationId) : Message(correlationId)
{
}
