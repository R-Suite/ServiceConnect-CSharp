using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

// Exercises the post-handler RemoveSnapshotAsync failure path inside
// AggregatorProcessor.DispatchResolvedAsync. The framework swallows the persistor
// failure to avoid NACK-driven duplicate handler dispatch; the new counter is the
// operator-visible signal for that otherwise log-only event.
[Collection(SerialConcurrencyCollection.Name)]
public sealed class AggregatorProcessorSnapshotRemoveCounterTests
{
    private static AggregatorSnapshot SnapshotOf(IEnumerable<IHasCorrelationId> messages, int unresolved = 0)
    {
        var msgList = messages.ToList();
        var ids = msgList.Select(_ => Guid.NewGuid()).ToList();
        return new AggregatorSnapshot(msgList, ids, unresolved);
    }

    private static (ConsumeScopeAccessor accessor, IDisposable scope, IServiceScopeFactory factory) BuildScopeContext(IServiceProvider provider)
    {
        var accessor = new ConsumeScopeAccessor();
        var scope = accessor.Push(provider);
        var factory = provider.GetRequiredService<IServiceScopeFactory>();
        return (accessor, scope, factory);
    }

    [Fact]
    public async Task SnapshotRemoveFailureAfterDispatch_IncrementsCounter()
    {
        using var collector = new MetricCollector<long>(
            (IServiceProvider?)null,
            ServiceConnectMeter.MeterName,
            MetricNames.SnapshotRemoveFailedAfterDispatch);

        var message = new SnapshotRemoveTestMessage(Guid.NewGuid());
        var handlerCompleted = new TaskCompletionSource<IReadOnlyList<SnapshotRemoveTestMessage>>();
        var aggregator = new SnapshotRemoveTestAggregator(handlerCompleted);

        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // BatchSize = 1, so a single inserted message immediately triggers the flush gate.
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SnapshotOf([message]));
        // RemoveSnapshotAsync throws AFTER the handler has succeeded — this is the
        // window the new counter measures.
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated Mongo blip during RemoveSnapshotAsync."));
        persistorMock.Setup(p => p.ReleaseSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(SnapshotRemoveTestMessage), HandlerType = typeof(SnapshotRemoveTestAggregator) }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<SnapshotRemoveTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(
            handlerRefs,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeGuard = scopeHandle;
        await using var processor = new AggregatorProcessor(
            registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(SnapshotRemoveTestMessage), message, headers, envelope);

        // Confirm the handler did run (otherwise we'd be measuring the wrong code path).
        var handlerWait = await Task.WhenAny(handlerCompleted.Task, Task.Delay(2000));
        Assert.Same(handlerCompleted.Task, handlerWait);

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Single(measurements);
        Assert.Equal(1L, measurements[0].Value);
    }
}

file sealed class SnapshotRemoveTestMessage(Guid correlationId) : Message(correlationId);

file sealed class SnapshotRemoveTestAggregator(TaskCompletionSource<IReadOnlyList<SnapshotRemoveTestMessage>> tcs) : Aggregator<SnapshotRemoveTestMessage>
{
    private readonly TaskCompletionSource<IReadOnlyList<SnapshotRemoveTestMessage>> _tcs = tcs;

    public override int BatchSize() => 1;
    public override TimeSpan Timeout() => TimeSpan.FromMinutes(5);

    public override Task ExecuteAsync(IReadOnlyList<SnapshotRemoveTestMessage> messages, CancellationToken cancellationToken = default)
    {
        _tcs.TrySetResult(messages);
        return Task.CompletedTask;
    }
}
