using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// End-to-end guard that inserts arriving while an aggregator flush is in progress
/// survive the flush. Messages that land between snapshot capture and flush completion
/// must remain in the buffer for the next flush rather than being wiped with the
/// ones the first Execute callback actually saw.
/// </summary>
[Collection(nameof(MessagingCollection))]
public class AggregatorFlushRaceE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Aggregator_ConcurrentInsertsWhileFlushingBlock_BothBatchesDelivered()
    {
        // Arrange: first flush completion signal
        // Use wrapper types so DI can distinguish the two TCS instances (DI resolves a
        // bare TaskCompletionSource<IList<TestMessage>> to whichever registration won last).
        var firstFlushSignal = new FirstFlushSignal();
        var secondFlushSignal = new SecondFlushSignal();
        // Gate that blocks the first Execute call while we push the second batch
        var gate = new FlushGate();

        var queueName = _fixture.GetUniqueQueueName("agg-flush-race");

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(FlushRaceAggregator),
                MessageType = typeof(TestMessage)
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton(firstFlushSignal);
        services.AddSingleton(secondFlushSignal);
        services.AddSingleton(gate);
        services.AddTransient<Aggregator<TestMessage>, FlushRaceAggregator>();

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
            builder.UseInMemoryPersistence();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();

        try
        {
            // Act: publish 3 messages to trigger a flush (BatchSize = 3)
            for (var i = 0; i < 3; i++)
            {
                await bus.SendAsync(new TestMessage(Guid.NewGuid()) { Content = $"first-{i}" },
                    new SendOptions { EndPoint = queueName });
            }

            // Wait until the handler is blocking inside Execute
            using var cts30 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts30.Token.Register(() => firstFlushSignal.Tcs.TrySetCanceled());
            var firstBatch = await firstFlushSignal.Tcs.Task;
            Assert.Equal(3, firstBatch.Count);

            // While handler is blocked, publish 3 more messages. RabbitMQ delivers them
            // to the consumer only after the first handler completes (channel prefetch
            // serializes delivery). Using 3 guarantees a batch-size flush will fire
            // once the gate releases and the queued messages are processed.
            for (var i = 0; i < 3; i++)
            {
                await bus.SendAsync(new TestMessage(Guid.NewGuid()) { Content = $"second-{i}" },
                    new SendOptions { EndPoint = queueName });
            }

            // Release the handler. The first flush completes and deletes only the
            // three snapshot ids it saw (not every document in the buffer), so the
            // three late messages remain and then trigger the second batch flush.
            gate.Mre.Set();

            // Assert: second Execute fires and contains the late messages (not silently wiped).
            cts30.Token.Register(() => secondFlushSignal.Tcs.TrySetCanceled());
            var secondBatch = await secondFlushSignal.Tcs.Task;
            var contents = secondBatch.Select(m => m.Content).ToHashSet();
            Assert.Equal(3, secondBatch.Count);
            Assert.Contains("second-0", contents);
            Assert.Contains("second-1", contents);
            Assert.Contains("second-2", contents);
        }
        finally
        {
            gate.Mre.Set(); // safety in case test aborts before release
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable ap)
            {
                await ap.DisposeAsync();
            }
        }
    }
}

file sealed class FirstFlushSignal
{
    public TaskCompletionSource<IList<TestMessage>> Tcs { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

file sealed class SecondFlushSignal
{
    public TaskCompletionSource<IList<TestMessage>> Tcs { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

file sealed class FlushGate
{
    public ManualResetEventSlim Mre { get; } = new(false);
    // The counter has to live on a singleton because ServiceConnect instantiates the
    // aggregator per-dispatch — a field on FlushRaceAggregator would reset on the second
    // invocation and we would route both flushes into the "first" branch.
    private int _invokeCount;
    public int Increment() => Interlocked.Increment(ref _invokeCount);
}

file class FlushRaceAggregator(FirstFlushSignal first, SecondFlushSignal second, FlushGate gate) : Aggregator<TestMessage>
{
    private readonly FirstFlushSignal _first = first;
    private readonly SecondFlushSignal _second = second;
    private readonly FlushGate _gate = gate;

    public override int BatchSize() => 3;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(60);

    public override Task ExecuteAsync(IList<TestMessage> messages, CancellationToken cancellationToken = default)
    {
        var count = _gate.Increment();
        if (count == 1)
        {
            _first.Tcs.TrySetResult(messages);
            _gate.Mre.Wait(TimeSpan.FromSeconds(10));
        }
        else
        {
            _second.Tcs.TrySetResult(messages);
        }
        return Task.CompletedTask;
    }
}
