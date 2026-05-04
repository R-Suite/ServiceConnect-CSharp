using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// End-to-end smoke that a freshly-built Consumer over a queue previously used
/// (and torn down) by another Consumer still consumes cleanly. Same-instance
/// Dispose→Start field-state invariants (owned-connection nulling, client-bag
/// clearing) are guarded directly by the unit suite; this exercises the
/// outward-visible bus shape across lifecycles on shared broker state.
/// </summary>
[Collection(nameof(MessagingCollection))]
public class ConsumerRestartE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task FreshConsumerOnPreviouslyUsedQueue_DeliversAfterFirstDisposed()
    {
        var consumerQueue = _fixture.GetUniqueQueueName("consumer-restart");
        var producerQueue = _fixture.GetUniqueQueueName("consumer-restart-producer");

        var firstReceived = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var phase = 0; // 0 = first lifecycle, 1 = second lifecycle

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(RestartCheckHandler), MessageType = typeof(TestMessage) }
        };

        IServiceProvider BuildConsumerProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IList<HandlerReference>>(handlerRefs);
            services.AddSingleton(new PhaseHolder(() => phase));
            // Capture TCS references via factory lambda to avoid DI type-ambiguity
            // (two singletons of the same type cannot be injected into distinct constructor
            // parameters — DI resolves one type to one instance for positional injection).
            services.AddTransient<IMessageHandler<TestMessage>>(sp =>
                new RestartCheckHandler(
                    firstReceived,
                    secondReceived,
                    sp.GetRequiredService<PhaseHolder>()));

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
                builder.ConfigureQueues(q => q.QueueName = consumerQueue);
                builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            });

            return services.BuildServiceProvider();
        }

        var producerServices = new ServiceCollection();
        producerServices.AddLogging();
        producerServices.AddSingleton<IList<HandlerReference>>([]);
        producerServices.AddServiceConnect(builder =>
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
            builder.ConfigureQueues(q => q.QueueName = producerQueue);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var producerProvider = producerServices.BuildServiceProvider();
        var producerBus = producerProvider.GetRequiredService<IBus>();

        // First lifecycle: start consumer, send message, receive it, dispose.
        var firstProvider = BuildConsumerProvider();
        var firstBus = firstProvider.GetRequiredService<IBus>();
        try
        {
            await firstBus.StartConsumingAsync();
            await producerBus.SendAsync(new TestMessage(Guid.NewGuid()) { Content = "first" }, new SendOptions { EndPoint = consumerQueue });
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            cts1.Token.Register(() => firstReceived.TrySetCanceled());
            var first = await firstReceived.Task;
            Assert.Equal("first", first.Content);
        }
        finally
        {
            await firstBus.DisposeAsync();
            if (firstProvider is IAsyncDisposable a)
            {
                await a.DisposeAsync();
            }
        }

        // Second lifecycle: rebuild the bus and start a fresh consumer on the same queue,
        // verify message delivery. The first lifecycle's DisposeAsync must release broker
        // resources cleanly so a new bus on the same queue can stand up and consume without
        // colliding with leftover topology, leases, or pending unacked deliveries.
        phase = 1;
        var secondProvider = BuildConsumerProvider();
        var secondBus = secondProvider.GetRequiredService<IBus>();
        try
        {
            await secondBus.StartConsumingAsync();
            await producerBus.SendAsync(new TestMessage(Guid.NewGuid()) { Content = "second" }, new SendOptions { EndPoint = consumerQueue });
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            cts2.Token.Register(() => secondReceived.TrySetCanceled());
            var second = await secondReceived.Task;
            Assert.Equal("second", second.Content);
        }
        finally
        {
            await secondBus.DisposeAsync();
            if (secondProvider is IAsyncDisposable b)
            {
                await b.DisposeAsync();
            }

            await producerBus.DisposeAsync();
            if (producerProvider is IAsyncDisposable c)
            {
                await c.DisposeAsync();
            }
        }
    }
}

file sealed class PhaseHolder(Func<int> phase)
{
    private readonly Func<int> _phase = phase;

    public int Current => _phase();
}

file sealed class RestartCheckHandler(
    TaskCompletionSource<TestMessage> first,
    TaskCompletionSource<TestMessage> second,
    PhaseHolder phase) : IMessageHandler<TestMessage>
{
    private readonly TaskCompletionSource<TestMessage> _first = first;
    private readonly TaskCompletionSource<TestMessage> _second = second;
    private readonly PhaseHolder _phase = phase;

    public Task HandleAsync(TestMessage message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (_phase.Current == 0)
        {
            _first.TrySetResult(message);
        }
        else
        {
            _second.TrySetResult(message);
        }

        return Task.CompletedTask;
    }
}
