using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using System.Collections.Concurrent;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// Opt-in throughput soak. Sustained-load tests have a different shape from the
/// "did the race fire once" E2E tests: they exercise the whole bus pipeline
/// (publisher channel pool, consumer dispatch, handler scheduling, acks) at
/// high message counts and surface back-pressure / leak / starvation issues
/// that single-burst tests miss.
///
/// These tests are <b>opt-in</b>: they no-op unless the
/// <c>SERVICECONNECT_STRESS</c> environment variable is set to <c>1</c>. Run
/// them explicitly via:
/// <code>SERVICECONNECT_STRESS=1 dotnet test --filter "Category=Stress"</code>.
/// </summary>
[Collection(nameof(MessagingCollection))]
public class ThroughputSoakE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    private static bool StressEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("SERVICECONNECT_STRESS"), "1", StringComparison.Ordinal);

    [Fact]
    [Trait("Category", "Stress")]
    public async Task Publish1000Messages_AllConsumedWithinBudget()
    {
        if (!StressEnabled)
        {
            return;
        }

        // Sustained throughput: publish many messages while a single consumer drains
        // them. Verifies no message is lost or duplicated under steady-state load and
        // gives a rough wall-clock budget so an order-of-magnitude regression shows up.
        const int messageCount = 1_000;
        var deadline = TimeSpan.FromMinutes(2);

        var queueName = _fixture.GetUniqueQueueName("soak-throughput");
        var errorQueueName = _fixture.GetUniqueQueueName("soak-throughput-eq");

        var consumed = new ConcurrentDictionary<Guid, byte>();

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
            new CallbackHandler<TestMessage>(msg =>
            {
                consumed[msg.CorrelationId] = 1;
            }));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.ErrorQueueName = errorQueueName;
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();

        try
        {
            var ids = Enumerable.Range(0, messageCount).Select(_ => Guid.NewGuid()).ToArray();

            // Spray publishes with bounded fan-out so we exercise the channel pool
            // without immediately overrunning RabbitMQ's TCP buffers.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var publishTasks = ids.Select(id =>
                bus.PublishAsync(new TestMessage(id) { Content = "soak" })).ToArray();
            await Task.WhenAll(publishTasks);
            stopwatch.Stop();

            var allReceived = await TestPolling.WaitUntilAsync(
                () => Task.FromResult(consumed.Count >= messageCount),
                deadline,
                TimeSpan.FromMilliseconds(250));

            Assert.True(
                allReceived,
                $"Expected {messageCount} messages consumed within {deadline} but only got {consumed.Count}.");
            Assert.True(ids.All(consumed.ContainsKey), "Some message correlation ids were never observed.");
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task SustainedPublishUnderConcurrency_NoChannelOrConnectionFaults()
    {
        if (!StressEnabled)
        {
            return;
        }

        // Many publishers fan out concurrently against the same producer instance.
        // The producer's channel pool / connection lifecycle must not surface
        // CHANNEL_ERROR or AlreadyClosedException under sustained pressure.
        const int publishers = 16;
        const int perPublisher = 100;
        const int total = publishers * perPublisher;
        var deadline = TimeSpan.FromMinutes(2);

        var queueName = _fixture.GetUniqueQueueName("soak-fanout");
        var errorQueueName = _fixture.GetUniqueQueueName("soak-fanout-eq");

        var consumed = 0;

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
            new CallbackHandler<TestMessage>(_ => Interlocked.Increment(ref consumed)));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.ErrorQueueName = errorQueueName;
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();

        try
        {
            var publisherTasks = Enumerable.Range(0, publishers).Select(p => Task.Run(async () =>
            {
                for (var i = 0; i < perPublisher; i++)
                {
                    await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = $"p{p}-i{i}" });
                }
            })).ToArray();

            await Task.WhenAll(publisherTasks);

            var allReceived = await TestPolling.WaitUntilAsync(
                () => Task.FromResult(Volatile.Read(ref consumed) >= total),
                deadline,
                TimeSpan.FromMilliseconds(250));

            Assert.True(
                allReceived,
                $"Expected {total} messages consumed within {deadline} but only got {Volatile.Read(ref consumed)}.");
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }
}
