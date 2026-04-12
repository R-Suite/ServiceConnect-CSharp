using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class AutoStartConsumingE2ETests
{
    private readonly MessagingFixture _fixture;

    public AutoStartConsumingE2ETests(MessagingFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task AutoStartConsuming_True_HandlerReceivesWithoutExplicitStart()
    {
        var queueName = _fixture.GetUniqueQueueName("autostart");
        var receivedTcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handlerReferences = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();

                // Register handler references before AddServiceConnect so TryAddSingleton keeps this list
                services.AddSingleton<IList<HandlerReference>>(handlerReferences);

                // Register the handler, backed by our TCS callback
                services.AddTransient<IMessageHandler<TestMessage>>(_ =>
                    new CallbackHandler<TestMessage>(msg => receivedTcs.TrySetResult(msg)));

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
                    builder.ConfigureQueues(q => q.QueueName = queueName);
                    builder.ConfigureBus(b =>
                    {
                        b.ScanForMessageHandlers = false;
                        b.AutoStartConsuming = true;
                    });
                });
            })
            .Build();

        try
        {
            // BusHostedService.StartAsync triggers bus.StartConsumingAsync() — no manual call
            await host.StartAsync();

            // Give the hosted service time to complete StartConsumingAsync
            await Task.Delay(500);

            // Publish via the bus from the host's service provider
            var bus = host.Services.GetRequiredService<IBus>();
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId) { Content = "auto-start consuming" };
            await bus.PublishAsync(sent);

            // Assert: wait up to 30 seconds for the handler to be called
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => receivedTcs.TrySetCanceled());

            var received = await receivedTcs.Task;

            Assert.Equal("auto-start consuming", received.Content);
            Assert.Equal(correlationId, received.CorrelationId);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
