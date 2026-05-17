using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

[Collection(SerialConcurrencyCollection.Name)]
public class AggregatorProcessorUnresolvedGateTests
{
    [Fact]
    public async Task ProcessAsync_UnresolvedOnlyBatch_DoesNotTriggerFlush()
    {
        // Persistor reports CountAsync = 10 but CountResolvedAsync = 0 (all records have
        // unresolved CLR types). The processor must NOT acquire the _flushLocks semaphore
        // or call GetSnapshotAsync: the batch-size gate must read CountResolvedAsync, so
        // an all-unresolved bucket never trips the flush path.
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(10); // would trigger gate if read
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);  // all unresolved — gate must NOT fire

        var aggregator = new AggUnrTestAggregator(batchSize: 5);
        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggUnrTestMessage), HandlerType = typeof(AggUnrTestAggregator) }
        };
        var services = new ServiceCollection();
        services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggUnrTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AggregatorRegistry>.Instance);
        var accessor = new ConsumeScopeAccessor();
        using var _scope = accessor.Push(provider);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        await using var processor = new AggregatorProcessor(
            registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        var msg = new AggUnrTestMessage(Guid.NewGuid()) { Value = "x" };
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };
        await processor.ProcessAsync(new byte[] { 1 }, typeof(AggUnrTestMessage), msg, headers, envelope);

        // GetSnapshotAsync would only be called if the gate fired. Verify it was NOT.
        persistorMock.Verify(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // CountAsync must not be consulted by the gate — only CountResolvedAsync.
        persistorMock.Verify(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        persistorMock.Verify(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FlushAsync_ReleasesLease_WhenSnapshotOnlyContainsUnresolved()
    {
        // Persistor returns a snapshot with no resolved messages but UnresolvedCount > 0.
        // The early return at the top of the flush body must call ReleaseSnapshotAsync so
        // the persistor's per-snapshot lease (stamped during GetSnapshotAsync) is freed
        // immediately rather than held for the full TTL.
        var snapshotMessage = new AggUnrTestMessage(Guid.NewGuid()) { Value = "unresolved" };
        var snapshot = new AggregatorSnapshot(
            [],                    // ResolvedMessages — empty
            [Guid.NewGuid()],      // CorrelationIds
            UnresolvedCount: 3);   // three unresolvable rows

        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5); // meets batch threshold so flush fires
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        persistorMock.Setup(p => p.ReleaseSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var aggregator = new AggUnrTestAggregator(batchSize: 5);
        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggUnrTestMessage), HandlerType = typeof(AggUnrTestAggregator) }
        };
        var services = new ServiceCollection();
        services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggUnrTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AggregatorRegistry>.Instance);
        var accessor = new ConsumeScopeAccessor();
        using var _scope = accessor.Push(provider);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        await using var processor = new AggregatorProcessor(
            registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        var msg = new AggUnrTestMessage(Guid.NewGuid()) { Value = "x" };
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };
        await processor.ProcessAsync(new byte[] { 1 }, typeof(AggUnrTestMessage), msg, headers, envelope);

        // The empty-resolved early-return must release the lease so unresolved rows are
        // not stranded under the lease for the full TTL.
        persistorMock.Verify(
            p => p.ReleaseSnapshotAsync(
                It.Is<string>(n => n == aggregator.GetType().FullName || true), // any aggregator name
                snapshot,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_ResolvedBatchAtThreshold_TriggersFlush()
    {
        // Sanity check: the resolved-count gate still fires when records are resolvable.
        var snapshotMessage = new AggUnrTestMessage(Guid.NewGuid()) { Value = "y" };
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AggregatorSnapshot(
                [snapshotMessage],
                [Guid.NewGuid()],
                UnresolvedCount: 0));
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var aggregator = new AggUnrTestAggregator(batchSize: 5);
        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggUnrTestMessage), HandlerType = typeof(AggUnrTestAggregator) }
        };
        var services = new ServiceCollection();
        services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggUnrTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AggregatorRegistry>.Instance);
        var accessor = new ConsumeScopeAccessor();
        using var _scope = accessor.Push(provider);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        await using var processor = new AggregatorProcessor(
            registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        var msg = new AggUnrTestMessage(Guid.NewGuid()) { Value = "z" };
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };
        await processor.ProcessAsync(new byte[] { 1 }, typeof(AggUnrTestMessage), msg, headers, envelope);

        persistorMock.Verify(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

file sealed class AggUnrTestMessage(Guid corrId) : Message(corrId)
{
    public string? Value { get; set; }
}

file sealed class AggUnrTestAggregator(int batchSize) : Aggregator<AggUnrTestMessage>
{
    private readonly int _batchSize = batchSize;

    public override int BatchSize() => _batchSize;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(60);
    public override Task ExecuteAsync(IReadOnlyList<AggUnrTestMessage> messages, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
