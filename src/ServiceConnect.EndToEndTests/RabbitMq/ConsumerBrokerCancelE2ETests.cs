using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests.RabbitMq;

/// <summary>
/// Verifies the broker-cancel contract: when the broker sends basic.cancel (triggered here
/// by deleting the consumer queue out-of-band), the consumer's IsCancelledByBroker flag
/// flips, Bus.IsConsuming becomes false, and BusConsumingHealthCheck reports Unhealthy —
/// all within 5 seconds, and without any explicit StopConsumingAsync call from the application.
/// </summary>
[Collection(nameof(IsolatedCollection))]
public sealed class ConsumerBrokerCancelE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task DeleteQueueWhileConsuming_BusConsumingHealthCheckReportsUnhealthyWithin5s()
    {
        var queueName = _fixture.GetUniqueQueueName("broker-cancel");

        // Wire up a bus with a consumer but no message handlers — we only need the
        // consume channel open so the broker can deliver a basic.cancel against it.
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
                t.SetClientSetting("RetryCount", 1);
                t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        await using var sp = services.BuildServiceProvider();
        var bus = sp.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();

        try
        {
            // The health check must report Healthy before we trigger the broker cancel.
            var healthCheck = new BusConsumingHealthCheck(bus);
            var registration = new HealthCheckRegistration("test", healthCheck, HealthStatus.Unhealthy, null);
            var context = new HealthCheckContext { Registration = registration };

            var initial = await healthCheck.CheckHealthAsync(context);
            Assert.Equal(HealthStatus.Healthy, initial.Status);

            // Delete the queue out-of-band on a separate connection. The broker will send
            // basic.cancel to the consumer connection, setting IsCancelledByBroker = true.
            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword,
            };
            await using var sideConn = await factory.CreateConnectionAsync();
            await using var sideChannel = await sideConn.CreateChannelAsync();
            await sideChannel.QueueDeleteAsync(queueName, ifUnused: false, ifEmpty: false);

            // Poll the health check until it flips to Unhealthy, or until the 5-second deadline.
            // The basic.cancel is async from the broker's side — give it time to propagate.
            HealthStatus status = HealthStatus.Healthy;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var probe = await healthCheck.CheckHealthAsync(context);
                status = probe.Status;
                if (status != HealthStatus.Healthy)
                {
                    break;
                }

                await Task.Delay(100);
            }

            Assert.Equal(HealthStatus.Unhealthy, status);
        }
        finally
        {
            // Bus.StopConsumingAsync throws InvalidOperationException after a broker cancel
            // because _stopped is set on the DisposedAsync path when the consumer is gone.
            // Dispose directly so cleanup is best-effort.
            await bus.DisposeAsync();
        }
    }
}
