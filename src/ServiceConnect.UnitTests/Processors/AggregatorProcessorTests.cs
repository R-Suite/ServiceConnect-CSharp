using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using ServiceConnect.UnitTests;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

[Collection(SerialConcurrencyCollection.Name)]
public class AggregatorProcessorTests
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
    public async Task ProcessAsync_NoAggregator_ReturnsNotHandled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>([]);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(
            [],
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance);

        var msg = new AggTestMessage(Guid.NewGuid()) { Value = "test" };
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_BatchComplete_ExecutesAggregator()
    {
        var messages = new List<AggTestMessage>
        {
            new(Guid.NewGuid()) { Value = "A" },
            new(Guid.NewGuid()) { Value = "B" },
            new(Guid.NewGuid()) { Value = "C" },
        };

        var tcs = new TaskCompletionSource<IList<AggTestMessage>>();
        var aggregator = new AggTestAggregator(tcs);

        var insertCount = 0;
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++insertCount);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SnapshotOf(messages));
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                MessageType = typeof(AggTestMessage),
                HandlerType = typeof(AggTestAggregator),
            }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        await using var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        ProcessResult result = ProcessResult.NotHandled;
        foreach (var msg in messages)
        {
            result = await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg, headers, envelope);
        }

        Assert.Equal(ProcessResult.Handled, result);

        var executedMessages = await Task.WhenAny(tcs.Task, Task.Delay(2000)) == tcs.Task
            ? await tcs.Task
            : null;

        Assert.NotNull(executedMessages);
        Assert.Equal(3, executedMessages!.Count);
        Assert.Equal("A", executedMessages[0].Value);
        Assert.Equal("B", executedMessages[1].Value);
        Assert.Equal("C", executedMessages[2].Value);
    }

    [Fact]
    public async Task FlushAggregator_CallsRemoveSnapshotAsync_NotPerMessageRemove()
    {
        var messages = new List<AggTestMessage>
        {
            new(Guid.NewGuid()) { Value = "A" },
            new(Guid.NewGuid()) { Value = "B" },
            new(Guid.NewGuid()) { Value = "C" },
        };

        var tcs = new TaskCompletionSource<IList<AggTestMessage>>();
        var aggregator = new AggTestAggregator(tcs);

        var insertCount = 0;
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++insertCount);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SnapshotOf(messages));
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggTestMessage), HandlerType = typeof(AggTestAggregator) }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        await using var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        foreach (var msg in messages)
        {
            await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg, headers, envelope);
        }

        persistorMock.Verify(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()), Times.Once);
        persistorMock.Verify(p => p.RemoveAllAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        persistorMock.Verify(p => p.RemoveDataAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FlushAggregator_InvokesExecuteBeforeRemovingSnapshot()
    {
        // Execute must run before RemoveSnapshotAsync. Removing first means a handler
        // exception drops the batch permanently; keeping the snapshot until after a
        // successful execute preserves it for redelivery on failure.
        var messages = new List<AggTestMessage>
        {
            new(Guid.NewGuid()) { Value = "A" },
            new(Guid.NewGuid()) { Value = "B" },
            new(Guid.NewGuid()) { Value = "C" },
        };

        var callOrder = new List<string>();
        var executed = new TaskCompletionSource<IList<AggTestMessage>>();
        var aggregator = new OrderRecordingAggregator(callOrder, executed);

        var insertCount = 0;
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++insertCount);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SnapshotOf(messages));
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("remove"))
            .Returns(Task.CompletedTask);

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggTestMessage), HandlerType = typeof(OrderRecordingAggregator) }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        await using var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        foreach (var msg in messages)
        {
            await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg, headers, envelope);
        }

        var waited = await Task.WhenAny(executed.Task, Task.Delay(2000));
        Assert.Same(executed.Task, waited);
        Assert.Equal(new[] { "execute", "remove" }, callOrder);
    }

    [Fact]
    public async Task FlushAggregator_WithUnresolvedRecords_DoesNotDispatchOrDeleteWhenNoResolved()
    {
        // When every buffered record has an unresolvable type, nothing is
        // dispatched AND nothing is deleted — the unresolved records must
        // survive for a later attempt once the types become resolvable.
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Under the resolved-aware gate, an unresolved-only buffer reports
        // CountResolvedAsync = 0; the gate does not fire, GetSnapshotAsync is never
        // consulted, and (correctly) no dispatch or delete occurs. Pre-fix this scenario
        // entered FlushAggregatorAsync and short-circuited on an empty ResolvedMessages
        // list — same outward behaviour, but each message paid the lock + snapshot cost.
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var tcs = new TaskCompletionSource<IList<AggTestMessage>>();
        var aggregator = new AggTestAggregator(tcs);
        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggTestMessage), HandlerType = typeof(AggTestAggregator) }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        await using var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage),
            new AggTestMessage(Guid.NewGuid()) { Value = "X" }, headers, envelope);

        Assert.False(tcs.Task.IsCompleted); // aggregator never ran
        persistorMock.Verify(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
        persistorMock.Verify(p => p.RemoveAllAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FlushAggregator_WithResolvedAndUnresolved_DispatchesResolvedAndPreservesUnresolved()
    {
        // A flush containing both resolved and unresolved items must dispatch
        // the resolved ones and call RemoveSnapshotAsync, which only deletes the
        // ids captured in the snapshot so the unresolved records remain buffered.
        var messages = new List<AggTestMessage>
        {
            new(Guid.NewGuid()) { Value = "A" },
            new(Guid.NewGuid()) { Value = "B" },
            new(Guid.NewGuid()) { Value = "C" },
        };

        var tcs = new TaskCompletionSource<IList<AggTestMessage>>();
        var aggregator = new AggTestAggregator(tcs);

        var insertCount = 0;
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++insertCount);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SnapshotOf(messages, unresolved: 1));
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggTestMessage), HandlerType = typeof(AggTestAggregator) }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        await using var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        foreach (var msg in messages)
        {
            await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg, headers, envelope);
        }

        var executed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, executed.Count);
        // Only RemoveSnapshotAsync — never RemoveAllAsync — so the unresolved record is preserved.
        persistorMock.Verify(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        persistorMock.Verify(p => p.RemoveAllAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_CancelsInFlightFlush()
    {
        // Dispose should cancel in-flight flushes via CancellationTokenSource.
        var flushStarted = new TaskCompletionSource();
        var flushCanProceed = new TaskCompletionSource();

        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                flushStarted.TrySetResult();
                await flushCanProceed.Task.WaitAsync(ct);
                return SnapshotOf(
                [
                    new AggTestMessage(Guid.NewGuid()) { Value = "A" },
                    new AggTestMessage(Guid.NewGuid()) { Value = "B" },
                    new AggTestMessage(Guid.NewGuid()) { Value = "C" },
                ]);
            });
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tcs = new TaskCompletionSource<IList<AggTestMessage>>();
        var aggregator = new AggTestAggregator(tcs);
        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggTestMessage), HandlerType = typeof(AggTestAggregator) }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var processTask = processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage),
            new AggTestMessage(Guid.NewGuid()) { Value = "X" }, headers, envelope);

        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await processor.DisposeAsync();

        var ex = await Record.ExceptionAsync(async () => await processTask);
        Assert.True(ex is null or OperationCanceledException or ObjectDisposedException,
            $"Expected null, OperationCanceledException, or ObjectDisposedException but got: {ex?.GetType().Name}: {ex?.Message}");
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>([]);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry([], provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance);

        await processor.DisposeAsync();
        await processor.DisposeAsync();
    }

    [Fact]
    public async Task InsertDuringFlush_LateMessageNotWiped()
    {
        // A message inserted after the snapshot is captured but before
        // RemoveSnapshotAsync runs must survive the flush: RemoveSnapshotAsync
        // only deletes the specific ids captured in the snapshot, not the
        // whole aggregator buffer.
        var persistor = new ServiceConnect.Persistence.InMemory.InMemoryAggregatorPersistor();
        const string name = "agg-race";

        var initial = new[]
        {
            new AggTestMessage(Guid.NewGuid()) { Value = "a1" },
            new AggTestMessage(Guid.NewGuid()) { Value = "a2" },
            new AggTestMessage(Guid.NewGuid()) { Value = "a3" },
        };
        foreach (var m in initial)
        {
            await persistor.InsertDataAsync(m, name);
        }

        var snapshot = await persistor.GetSnapshotAsync(name);

        // Simulate a concurrent insert that arrives *after* the snapshot but *before*
        // the remove — this is the race the plan's snapshot API is designed to close.
        var late = new AggTestMessage(Guid.NewGuid()) { Value = "late" };
        await persistor.InsertDataAsync(late, name);

        await persistor.RemoveSnapshotAsync(name, snapshot);

        var remaining = await persistor.GetDataAsync(name);
        Assert.Single(remaining);
        // In-memory persistor deep-clones on insert/retrieve, so identity differs; compare
        // by correlation id and payload instead.
        var survivor = Assert.IsType<AggTestMessage>(remaining[0]);
        Assert.Equal(late.CorrelationId, survivor.CorrelationId);
        Assert.Equal(late.Value, survivor.Value);
    }

    [Fact]
    public async Task InsertDuringFlush_MoqCallback_LateMessageSurvivesRemoveSnapshot()
    {
        // Verifies the snapshot-remove pattern: a message inserted between
        // GetSnapshotAsync and RemoveSnapshotAsync must not be wiped, because
        // RemoveSnapshotAsync only removes the ids captured in the snapshot.
        // Uses Moq callbacks to simulate the concurrent insert deterministically.

        var initial = new List<AggTestMessage>
        {
            new(Guid.NewGuid()) { Value = "a1" },
            new(Guid.NewGuid()) { Value = "a2" },
            new(Guid.NewGuid()) { Value = "a3" },
        };

        var lateMessage = new AggTestMessage(Guid.NewGuid()) { Value = "late" };
        var lateId = Guid.NewGuid();

        // Track what RemoveSnapshotAsync receives so we can verify the late message id is NOT in it.
        IAggregatorSnapshot? capturedSnapshot = null;

        var insertCount = 0;
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++insertCount);

        // GetSnapshotAsync callback simulates a concurrent insert arriving between snapshot and remove.
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // The snapshot captures only the initial 3 messages.
                var ids = initial.Select(_ => Guid.NewGuid()).ToList();
                return (IAggregatorSnapshot)new AggregatorSnapshot([.. initial.Cast<IHasCorrelationId>()], ids, 0);
                // NOTE: the late message is NOT in this snapshot — it would be inserted
                // by a concurrent producer between snapshot and remove.
            });

        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<string, IAggregatorSnapshot, CancellationToken>((_, snap, _) => capturedSnapshot = snap)
            .Returns(Task.CompletedTask);

        var tcs = new TaskCompletionSource<IList<AggTestMessage>>();
        var aggregator = new AggTestAggregator(tcs);
        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggTestMessage), HandlerType = typeof(AggTestAggregator) }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        await using var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // Send exactly BatchSize (3) messages to trigger a flush.
        foreach (var msg in initial)
        {
            await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg, headers, envelope);
        }

        // Aggregator must have run with the 3 initial messages.
        var executed = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, executed.Count);

        // RemoveSnapshotAsync must have been called exactly once.
        persistorMock.Verify(p => p.RemoveSnapshotAsync(
            It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()), Times.Once);

        // The snapshot passed to RemoveSnapshotAsync must NOT contain the late message's id.
        // (The late message id was never inserted into the snapshot, confirming the contract
        //  that RemoveSnapshotAsync only removes what was snapshotted — not any later arrivals.)
        Assert.NotNull(capturedSnapshot);
        Assert.DoesNotContain(lateId, capturedSnapshot!.ResolvedIds);

        // RemoveAllAsync must never be called (that would wipe late inserts).
        persistorMock.Verify(p => p.RemoveAllAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_WithLateTimerCallback_DoesNotRecreateFlushLock()
    {
        // OnTimerFired is gated on Volatile.Read(ref _disposed); a callback that fires after
        // DisposeAsync has cleared _flushLocks must return before touching any state. Without
        // the gate, FlushAggregatorAsync's _flushLocks.GetOrAdd(...) would re-create a
        // SemaphoreSlim in a dictionary that is never read again — a bounded leak.
        //
        // White-box approach: manually set _disposed=1 via reflection (WITHOUT calling
        // DisposeAsync so _disposeCts remains live and FlushAggregatorAsync can reach
        // _flushLocks.GetOrAdd), then call OnTimerFired directly and wait briefly for any
        // spawned background task to complete. The expectation is _flushLocks stays empty —
        // OnTimerFired returns immediately on the disposed guard.

        var getSnapshotCalled = new TaskCompletionSource();
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        // GetSnapshotAsync signals when the flush lock has been acquired (i.e. GetOrAdd ran).
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, _) =>
            {
                getSnapshotCalled.TrySetResult();
                return Task.FromResult<IAggregatorSnapshot>(AggregatorSnapshot.Empty);
            });
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tcs = new TaskCompletionSource<IList<AggTestMessage>>();
        var aggregator = new AggTestTimedAggregator(tcs);
        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggTestMessage), HandlerType = typeof(AggTestTimedAggregator) }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        await using var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        // Retrieve private fields / methods via reflection.
        var processorType = typeof(AggregatorProcessor);
        var disposedField = processorType.GetField("_disposed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var flushLocksField = processorType.GetField("_flushLocks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var onTimerFiredMethod = processorType.GetMethod("OnTimerFired", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var flushLocks = (System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>)flushLocksField.GetValue(processor)!;

        // Retrieve the descriptor for AggTestMessage (Timeout-based, not BatchSize-based).
        var registryType = typeof(AggregatorRegistry);
        var descriptorsField = registryType.GetField("_descriptors", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var descriptors = descriptorsField.GetValue(registry)!;
        var descriptor = (AggregatorDescriptor)((System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<Type, AggregatorDescriptor>>)descriptors)
            .First(kvp => kvp.Key == typeof(AggTestMessage)).Value;

        // Manually mark disposed without calling DisposeAsync — this preserves _disposeCts
        // so that _disposeCts.Token is accessible (not thrown) when RunFlushAsync runs.
        // This replicates the exact window: DisposeAsync has set _disposed but hasn't yet
        // called _disposeCts.Cancel() / _flushLocks.Clear().
        disposedField.SetValue(processor, 1);

        // _flushLocks should still be populated (we haven't cleared it).
        // The processor is fully live at this point except _disposed=1.

        // Simulate a late timer callback: call OnTimerFired with _disposed already set.
        onTimerFiredMethod.Invoke(processor, [descriptor]);

        // Give the background RunFlushAsync task time to run if the guard is missing.
        // Expected: OnTimerFired returns immediately on the disposed guard and _flushLocks
        // stays empty; without the guard, RunFlushAsync would call FlushAggregatorAsync →
        // GetOrAdd and a stray entry would land in the dictionary.
        await Task.Delay(200);

        // Assert: _flushLocks must be empty — the late callback must not have created any entry.
        Assert.Empty(flushLocks);
    }

    [Fact]
    public async Task TimerReuse_DoesNotAllocateNewTimerPerMessage()
    {
        var flushCount = 0;
        var flushTcs = new TaskCompletionSource();

        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref flushCount);
                flushTcs.TrySetResult();
                return AggregatorSnapshot.Empty;
            });
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tcs = new TaskCompletionSource<IList<AggTestMessage>>();
        var aggregator = new AggTestTimedAggregator(tcs);
        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggTestMessage), HandlerType = typeof(AggTestTimedAggregator) }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggTestMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        await using var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        for (int i = 0; i < 5; i++)
        {
            await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage),
                new AggTestMessage(Guid.NewGuid()) { Value = $"msg-{i}" }, headers, envelope);
            await Task.Delay(50);
        }

        await flushTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, flushCount);
    }

    [Fact]
    public async Task RunFlushAsync_AfterDispose_DoesNotLogSpuriousObjectDisposedException()
    {
        // Race: OnTimerFired passes the `_disposed` fast-path guard (because the timer callback
        // was scheduled BEFORE DisposeAsync set the flag), then loses the race with DisposeAsync
        // to dispose `_disposeCts`. The first act of RunFlushAsync is
        // `FlushAggregatorAsync(descriptor, _disposeCts.Token)` — evaluating the Token on a
        // disposed CancellationTokenSource throws ObjectDisposedException. RunFlushAsync must
        // recognise this race and stay quiet rather than escalating it through `logger.LogError`,
        // which would emit a spurious ERROR entry during otherwise-clean shutdown.
        //
        // Deterministic reproduction: fully dispose the processor (so `_disposed=1` AND
        // `_disposeCts` is disposed — exactly the state a losing timer callback sees at the
        // Token-access point), then invoke RunFlushAsync directly via reflection. This
        // bypasses OnTimerFired's guard (already "passed" in the real race) and exercises
        // RunFlushAsync's failure mode in isolation.

        var capturingLogger = new AptCapturingLogger();
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AggregatorSnapshot.Empty);
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggTestMessage), HandlerType = typeof(AggTestTimedAggregator) }
        };
        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistorMock.Object);
        services.AddSingleton<Aggregator<AggTestMessage>>(new AggTestTimedAggregator(new TaskCompletionSource<IList<AggTestMessage>>()));
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        var processor = new AggregatorProcessor(registry, accessor, scopeFactory, capturingLogger, persistorMock.Object);

        // Fully dispose — _disposed=1, _disposeCts disposed. Realistic post-race state.
        await processor.DisposeAsync();

        // Fetch the descriptor for AggTestMessage.
        var registryType = typeof(AggregatorRegistry);
        var descriptorsField = registryType.GetField("_descriptors", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var descriptors = descriptorsField.GetValue(registry)!;
        var descriptor = (AggregatorDescriptor)((IEnumerable<KeyValuePair<Type, AggregatorDescriptor>>)descriptors)
            .First(kvp => kvp.Key == typeof(AggTestMessage)).Value;

        // Invoke RunFlushAsync directly. Bypasses OnTimerFired's guard (same effect as the
        // real race where that guard had already passed). RunFlushAsync is private → reflection.
        var processorType = typeof(AggregatorProcessor);
        var runFlushMethod = processorType.GetMethod("RunFlushAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runTask = (Task)runFlushMethod.Invoke(processor, [42, tcs, descriptor])!;
        await runTask;

        // RunFlushAsync must catch the race quietly, no ERROR logged. If the ODE from
        // `_disposeCts.Token` were allowed to fall through into catch (Exception), an
        // ERROR entry would land here.
        var odeErrors = capturingLogger.Entries
            .Where(e => e.Level == LogLevel.Error && e.Exception is ObjectDisposedException)
            .ToList();
        Assert.Empty(odeErrors);
    }

    [Fact]
    public async Task FlushAggregatorAsync_HandlerThrows_SnapshotRemainsForRetry()
    {
        // If the handler throws a non-cancellation exception, the snapshot must not have
        // been removed yet — the messages stay in the persistor so the broker can redeliver
        // and the batch is re-flushable on the next admission.
        var persistor = new ServiceConnect.Persistence.InMemory.InMemoryAggregatorPersistor();

        var throwingAggregator = new ThrowingAggregator();
        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(AggTestMessage), HandlerType = typeof(ThrowingAggregator) }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IAggregatorPersistor>(persistor);
        services.AddSingleton<Aggregator<AggTestMessage>>(throwingAggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        await using var processor = new AggregatorProcessor(registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistor);

        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // BatchSize=3: the first two messages are buffered; the third triggers a synchronous
        // flush. The handler throws, so the test expects an exception from ProcessAsync.
        var messages = new[]
        {
            new AggTestMessage(Guid.NewGuid()) { Value = "x1" },
            new AggTestMessage(Guid.NewGuid()) { Value = "x2" },
            new AggTestMessage(Guid.NewGuid()) { Value = "x3" },
        };
        await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), messages[0], headers, envelope);
        await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), messages[1], headers, envelope);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), messages[2], headers, envelope));

        // Derive the stream name exactly as the processor does so CountAsync targets the right key.
        var aggregatorBaseType = typeof(ThrowingAggregator).BaseType!;
        var streamName = aggregatorBaseType.FullName!;

        // The messages must still be in the persistor so they can be retried.
        var remaining = await persistor.CountAsync(streamName);
        Assert.Equal(messages.Length, remaining);
    }

    [Fact]
    public async Task ProcessAsync_AfterDispose_ThrowsObjectDisposedExceptionWithoutTouchingDisposedCts()
    {
        // Verify that calling ProcessAsync on a disposed processor fails fast with
        // ObjectDisposedException rather than reaching _disposeCts.Token (which would
        // be disposed and throw an unrelated ODE from the linked-CTS construction).
        // BatchSize=1 ensures the batch path is taken on the first message, so the
        // dispose guard inside the batch block is also exercised.
        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(PostDisposeProbeMessage), HandlerType = typeof(PostDisposeProbeAggregator) }
        };

        var aggregator = new PostDisposeProbeAggregator();
        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<Aggregator<PostDisposeProbeMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);

        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock
            .Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock
            .Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        var processor = new AggregatorProcessor(
            registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        await processor.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            processor.ProcessAsync(
                ReadOnlyMemory<byte>.Empty,
                typeof(PostDisposeProbeMessage),
                new PostDisposeProbeMessage(Guid.NewGuid()),
                new Dictionary<string, object>(),
                new Envelope { Headers = new Dictionary<string, object>(), Body = ReadOnlyMemory<byte>.Empty },
                CancellationToken.None));
    }

    /// <summary>
    /// After concurrent ResetTimer calls for the same aggregator, the dictionary must
    /// hold exactly one live Timer. ConcurrentDictionary.AddOrUpdate's factory may run
    /// multiple times under contention; without single-flight serialisation, losing
    /// factory attempts would produce live Timer instances that were never installed
    /// in _timers and never disposed. The lock around the create+install pair makes
    /// "exactly one Timer per ResetTimer call, previous disposed atomically" the only
    /// reachable observable state.
    /// </summary>
    [Fact]
    public async Task ResetTimer_ConcurrentCalls_NoOrphanedTimers()
    {
        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ResetTimerProbeMessage), HandlerType = typeof(ResetTimerProbeAggregator) }
        };

        var aggregator = new ResetTimerProbeAggregator();
        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<Aggregator<ResetTimerProbeMessage>>(aggregator);
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);

        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock
            .Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock
            .Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var (accessor, scopeHandle, scopeFactory) = BuildScopeContext(provider);
        using var _scopeAgg = scopeHandle;
        await using var processor = new AggregatorProcessor(
            registry, accessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        var tasks = Enumerable.Range(0, 64).Select(_ =>
            processor.ProcessAsync(
                ReadOnlyMemory<byte>.Empty,
                typeof(ResetTimerProbeMessage),
                new ResetTimerProbeMessage(Guid.NewGuid()),
                new Dictionary<string, object>(),
                new Envelope { Headers = new Dictionary<string, object>(), Body = ReadOnlyMemory<byte>.Empty },
                CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks);

        var timersField = typeof(AggregatorProcessor).GetField("_timers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var timers = (System.Collections.Concurrent.ConcurrentDictionary<string, Timer>)timersField!.GetValue(processor)!;

        Assert.Single(timers);
    }

    // The batch-path flush must resolve the Aggregator<T> instance from the consume scope
    // that is currently active when ProcessAsync runs. The dispatcher pushes a per-message
    // scope; without scope-aware resolution, scoped aggregator dependencies leak across
    // messages. BatchSize=1 makes ProcessAsync take the synchronous batch-flush path so
    // the scope active at call time is the one observed by FlushAggregatorAsync. (The
    // timer-fired path runs outside any consume scope and falls back to a fresh scope from
    // IServiceScopeFactory; that is a different code path with its own behaviour contract.)
    [Fact]
    public async Task ProcessAsync_BatchPath_ResolvesAggregatorFromCurrentConsumeScope()
    {
        var rootAggregator = new ScopeProbeAggregator();
        var scopedAggregator = new ScopeProbeAggregator();

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ScopeProbeAggMessage), HandlerType = typeof(ScopeProbeAggregator) }
        };

        var rootServices = new ServiceCollection();
        rootServices.AddSingleton<IList<HandlerReference>>(handlerRefs);
        rootServices.AddSingleton<Aggregator<ScopeProbeAggMessage>>(rootAggregator);
        var rootProvider = rootServices.BuildServiceProvider();

        var scopedServices = new ServiceCollection();
        scopedServices.AddSingleton<IList<HandlerReference>>(handlerRefs);
        scopedServices.AddSingleton<Aggregator<ScopeProbeAggMessage>>(scopedAggregator);
        var scopedProvider = scopedServices.BuildServiceProvider();

        // The registry materializes the aggregator once at startup against rootProvider to
        // read BatchSize/Timeout — this is fine; only the per-flush resolution needs to be
        // scope-aware.
        var registry = new AggregatorRegistry(handlerRefs, rootProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);

        var probeMessage = new ScopeProbeAggMessage(Guid.NewGuid());
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountResolvedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SnapshotOf([probeMessage]));
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var scopeAccessor = new ConsumeScopeAccessor();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();
        await using var processor = new AggregatorProcessor(
            registry, scopeAccessor, scopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        using (scopeAccessor.Push(scopedProvider))
        {
            await processor.ProcessAsync(
                ReadOnlyMemory<byte>.Empty,
                typeof(ScopeProbeAggMessage),
                probeMessage,
                new Dictionary<string, object>(),
                new Envelope { Headers = new Dictionary<string, object>(), Body = ReadOnlyMemory<byte>.Empty },
                CancellationToken.None);
        }

        Assert.Equal(0, rootAggregator.Hits);
        Assert.Equal(1, scopedAggregator.Hits);
    }

    // The Timer captures the dispatcher's ExecutionContext at construction. AsyncLocal
    // flows through EC, so when the timer fires the callback observes the dispatcher's
    // scope on ConsumeScopeAccessor — but that scope was disposed when ProcessAsync
    // returned. The flush must always create a fresh DI scope on the timer path.
    [Fact]
    public async Task TimerFiredFlush_AlwaysCreatesFreshScope_IgnoresEcCapturedAmbient()
    {
        var staleAggregator = new EcCaptureProbeAggregator();
        var freshAggregator = new EcCaptureProbeAggregator();

        var handlerRefs = new List<HandlerReference>
        {
            new() { MessageType = typeof(EcCaptureProbeMessage), HandlerType = typeof(EcCaptureProbeAggregator) }
        };

        var staleServices = new ServiceCollection();
        staleServices.AddSingleton<IList<HandlerReference>>(handlerRefs);
        staleServices.AddSingleton<Aggregator<EcCaptureProbeMessage>>(staleAggregator);
        var staleProvider = staleServices.BuildServiceProvider();

        var freshServices = new ServiceCollection();
        freshServices.AddSingleton<IList<HandlerReference>>(handlerRefs);
        freshServices.AddSingleton<Aggregator<EcCaptureProbeMessage>>(freshAggregator);
        var freshProvider = freshServices.BuildServiceProvider();

        var registry = new AggregatorRegistry(handlerRefs, freshProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);

        var probeMessage = new EcCaptureProbeMessage(Guid.NewGuid());
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<IHasCorrelationId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Below batch size so ProcessAsync goes to the timer path, not the immediate flush.
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SnapshotOf([probeMessage]));
        persistorMock.Setup(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var scopeAccessor = new ConsumeScopeAccessor();
        var freshScopeFactory = freshProvider.GetRequiredService<IServiceScopeFactory>();
        await using var processor = new AggregatorProcessor(
            registry, scopeAccessor, freshScopeFactory, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        using (scopeAccessor.Push(staleProvider))
        {
            await processor.ProcessAsync(
                ReadOnlyMemory<byte>.Empty,
                typeof(EcCaptureProbeMessage),
                probeMessage,
                new Dictionary<string, object>(),
                new Envelope { Headers = new Dictionary<string, object>(), Body = ReadOnlyMemory<byte>.Empty },
                CancellationToken.None);
        }

        // Wait up to 2s for the 50ms timer to fire and complete the flush.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && freshAggregator.Hits == 0 && staleAggregator.Hits == 0)
        {
            await Task.Delay(20);
        }

        Assert.Equal(0, staleAggregator.Hits);
        Assert.Equal(1, freshAggregator.Hits);
    }
}

file sealed class ResetTimerProbeMessage(Guid correlationId) : Message(correlationId);

file sealed class ResetTimerProbeAggregator : Aggregator<ResetTimerProbeMessage>
{
    public override int BatchSize() => 1000;
    public override TimeSpan Timeout() => TimeSpan.FromMilliseconds(50);
    public override Task ExecuteAsync(IList<ResetTimerProbeMessage> messages, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

file sealed class AptCapturingLogger : ILogger<AggregatorProcessor>
{
    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
    public List<LogEntry> Entries { get; } = [];

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    bool ILogger.IsEnabled(LogLevel logLevel) => true;

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

file class AggTestMessage(Guid correlationId) : Message(correlationId)
{
    public string Value { get; set; } = "";
}

file class AggTestAggregator(TaskCompletionSource<IList<AggTestMessage>> tcs) : Aggregator<AggTestMessage>
{
    private readonly TaskCompletionSource<IList<AggTestMessage>> _tcs = tcs;

    public override int BatchSize() => 3;
    public override TimeSpan Timeout() => TimeSpan.FromMinutes(5);

    public override Task ExecuteAsync(IList<AggTestMessage> messages, CancellationToken cancellationToken = default)
    {
        _tcs.TrySetResult(messages);
        return Task.CompletedTask;
    }
}

file class OrderRecordingAggregator(List<string> order, TaskCompletionSource<IList<AggTestMessage>> tcs) : Aggregator<AggTestMessage>
{
    private readonly List<string> _order = order;
    private readonly TaskCompletionSource<IList<AggTestMessage>> _tcs = tcs;

    public override int BatchSize() => 3;
    public override TimeSpan Timeout() => TimeSpan.FromMinutes(5);

    public override Task ExecuteAsync(IList<AggTestMessage> messages, CancellationToken cancellationToken = default)
    {
        _order.Add("execute");
        _tcs.TrySetResult(messages);
        return Task.CompletedTask;
    }
}

file class AggTestTimedAggregator(TaskCompletionSource<IList<AggTestMessage>> tcs) : Aggregator<AggTestMessage>
{
    private readonly TaskCompletionSource<IList<AggTestMessage>> _tcs = tcs;

    public override int BatchSize() => 10000;
    public override TimeSpan Timeout() => TimeSpan.FromMilliseconds(200);

    public override Task ExecuteAsync(IList<AggTestMessage> messages, CancellationToken cancellationToken = default)
    {
        _tcs.TrySetResult(messages);
        return Task.CompletedTask;
    }
}

file sealed class PostDisposeProbeMessage(Guid correlationId) : Message(correlationId);

file sealed class PostDisposeProbeAggregator : Aggregator<PostDisposeProbeMessage>
{
    public override int BatchSize() => 1;
    public override TimeSpan Timeout() => TimeSpan.FromMinutes(5);
    public override Task ExecuteAsync(IList<PostDisposeProbeMessage> messages, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

file sealed class ScopeProbeAggMessage(Guid correlationId) : Message(correlationId);

file sealed class ScopeProbeAggregator : Aggregator<ScopeProbeAggMessage>
{
    private int _hits;
    public int Hits => Volatile.Read(ref _hits);
    public override int BatchSize() => 1;
    public override TimeSpan Timeout() => TimeSpan.FromMinutes(5);
    public override Task ExecuteAsync(IList<ScopeProbeAggMessage> messages, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _hits);
        return Task.CompletedTask;
    }
}

file sealed class EcCaptureProbeMessage(Guid correlationId) : Message(correlationId);

file sealed class EcCaptureProbeAggregator : Aggregator<EcCaptureProbeMessage>
{
    private int _hits;
    public int Hits => Volatile.Read(ref _hits);
    public override int BatchSize() => 1000;
    public override TimeSpan Timeout() => TimeSpan.FromMilliseconds(50);
    public override Task ExecuteAsync(IList<EcCaptureProbeMessage> messages, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _hits);
        return Task.CompletedTask;
    }
}

file sealed class ThrowingAggregator : Aggregator<AggTestMessage>
{
    public override int BatchSize() => 3;
    public override TimeSpan Timeout() => TimeSpan.FromMinutes(5);
    public override Task ExecuteAsync(IList<AggTestMessage> messages, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("handler failure — batch must remain for retry");
}
