using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// End-to-end guard that two concurrent messages for the same saga correlation id
/// both converge via the optimistic-concurrency retry path. Neither increment may be
/// silently lost to a version conflict — the loser must reload, re-apply, and commit.
/// </summary>
[Collection(nameof(PersistenceCollection))]
public class ProcessManagerConcurrencyE2ETests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ConcurrentMessages_SameSagaId_BothIncrementsApplied()
    {
        var bothHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("pm-concurrency");
        var correlationId = Guid.NewGuid();
        // Gate: the first handler holds here so the second can race past it
        var gate = new ManualResetEventSlim(false);

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(ConcurrentIncrementHandler),
                MessageType = typeof(TestMessage)
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton(bothHandled);
        services.AddSingleton(gate);
        services.AddTransient<IProcessHandler<ConcurrentCounterData, TestMessage>, ConcurrentIncrementHandler>();

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
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.UseMongoDbPersistence(opts =>
            {
                opts.ConnectionString = _fixture.MongoDbConnectionString;
                opts.DatabaseName = _fixture.GetUniqueDatabaseName("pm-concurrency");
            });
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();

        try
        {
            // Send both messages in quick succession so they race inside the process manager
            var msg1 = new TestMessage(correlationId) { Content = "increment-1" };
            var msg2 = new TestMessage(correlationId) { Content = "increment-2" };
            await bus.SendAsync(msg1, new SendOptions { EndPoint = queueName });
            await bus.SendAsync(msg2, new SendOptions { EndPoint = queueName });

            // Release the gate after a short delay so both handlers can race
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                gate.Set();
            });

            // Wait for both increments to complete
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => bothHandled.TrySetCanceled());
            await bothHandled.Task;

            // Allow time for persistence after handler
            await Task.Delay(500);

            // Assert: both increments converged; counter == 2
            var finder = provider.GetRequiredService<IProcessManagerFinder>();
            var mapper = new TestProcessManagerPropertyMapper();
            mapper.ConfigureMapping<ConcurrentCounterData, TestMessage>(d => d.CorrelationId, m => m.CorrelationId);
            var result = await finder.FindDataAsync<ConcurrentCounterData>(mapper, new TestMessage(correlationId));

            Assert.NotNull(result);
            Assert.Equal(2, result!.Data.Counter);
        }
        finally
        {
            gate.Set(); // safety
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable ap)
            {
                await ap.DisposeAsync();
            }
        }
    }
}

file class ConcurrentCounterData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public int Counter { get; set; }
}

file class ConcurrentIncrementHandler(
    TaskCompletionSource<bool> bothHandled,
    ManualResetEventSlim gate) : IProcessHandler<ConcurrentCounterData, TestMessage>
{
    private readonly TaskCompletionSource<bool> _bothHandled = bothHandled;
    private readonly ManualResetEventSlim _gate = gate;
    private static int _invokeCount;

    public Task HandleAsync(TestMessage message, ConcurrentCounterData data, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        data.CorrelationId = message.CorrelationId;
        data.Counter++;

        var count = Interlocked.Increment(ref _invokeCount);

        // The first handler blocks to create a window for the second to race
        if (count == 1)
        {
            _gate.Wait(TimeSpan.FromSeconds(5));
        }

        if (count >= 2)
        {
            _bothHandled.TrySetResult(true);
        }

        return Task.CompletedTask;
    }
}
