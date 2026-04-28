using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests.HealthChecks;

[Collection(nameof(IsolatedCollection))]
public class HealthCheckEndToEndTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task AllThreeChecks_ReportHealthy_WhenBusAndBrokerAreAlive()
    {
        var queueName = _fixture.GetUniqueQueueName("healthcheck");

        // Register a handler so StartConsumingAsync can open the consumer connection.
        // The bus check needs IsConsuming = true; the consumer check needs IsConnected = true.
        var handlerReferences = new List<HandlerReference>
        {
            new() {
                HandlerType = typeof(NoOpHandler),
                MessageType = typeof(HealthCheckProbe)
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();

        // Register handler references before AddServiceConnect so TryAddSingleton keeps this list.
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<HealthCheckProbe>>(_ => new NoOpHandler());

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
        });

        services.AddHealthChecks()
            .AddServiceConnectBus(tags: ["live"])
            .AddServiceConnectConsumer(tags: ["ready"])
            .AddServiceConnectProducer(tags: ["ready"]);

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();

        try
        {
            // Publish a message so the producer's lazy EnsureConnectedAsync runs
            // and IProducer.IsHealthy becomes true before we call CheckHealthAsync.
            await bus.PublishAsync(new HealthCheckProbe(Guid.NewGuid()));

            var hcService = provider.GetRequiredService<HealthCheckService>();
            var report = await hcService.CheckHealthAsync();

            Assert.Equal(HealthStatus.Healthy, report.Status);
            Assert.Equal(3, report.Entries.Count);

            Assert.True(report.Entries.ContainsKey("serviceconnect-bus"));
            Assert.Equal(HealthStatus.Healthy, report.Entries["serviceconnect-bus"].Status);

            Assert.True(report.Entries.ContainsKey("serviceconnect-consumer"));
            Assert.Equal(HealthStatus.Healthy, report.Entries["serviceconnect-consumer"].Status);

            Assert.True(report.Entries.ContainsKey("serviceconnect-producer"));
            Assert.Equal(HealthStatus.Healthy, report.Entries["serviceconnect-producer"].Status);
        }
        finally
        {
            await bus.StopConsumingAsync();
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider)
            {
                await asyncProvider.DisposeAsync();
            }
        }
    }

    // Private message type scoped to this test class — avoids competing with any
    // shared exchange that TestMessage binds to (fan-out exchange collision).
    private sealed class HealthCheckProbe(Guid correlationId) : Message(correlationId);

    // Minimal handler — we only need it to exist so the consumer connection opens.
    private sealed class NoOpHandler : IMessageHandler<HealthCheckProbe>
    {
        public IConsumeContext Context { get; set; } = null!;

        public Task HandleAsync(HealthCheckProbe message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
