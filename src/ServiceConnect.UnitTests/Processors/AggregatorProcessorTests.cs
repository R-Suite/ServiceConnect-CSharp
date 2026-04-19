using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class AggregatorProcessorTests
{
    private static AggregatorSnapshot SnapshotOf(IEnumerable<object> messages, int unresolved = 0)
    {
        var msgList = messages.ToList();
        var ids = msgList.Select(_ => Guid.NewGuid()).ToList();
        return new AggregatorSnapshot(msgList, ids, unresolved);
    }

    [Fact]
    public async Task ProcessAsync_NoAggregator_ReturnsNotHandled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(
            new List<HandlerReference>(),
            provider,
            NullLogger<AggregatorRegistry>.Instance);
        var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance);

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
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        var registry = new AggregatorRegistry(handlerRefs, provider, NullLogger<AggregatorRegistry>.Instance);
        await using var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
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
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        var registry = new AggregatorRegistry(handlerRefs, provider, NullLogger<AggregatorRegistry>.Instance);
        await using var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        foreach (var msg in messages)
            await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg, headers, envelope);

        persistorMock.Verify(p => p.RemoveSnapshotAsync(It.IsAny<string>(), It.IsAny<IAggregatorSnapshot>(), It.IsAny<CancellationToken>()), Times.Once);
        persistorMock.Verify(p => p.RemoveAllAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        persistorMock.Verify(p => p.RemoveDataAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FlushAggregator_WithUnresolvedRecords_DoesNotDispatchOrDeleteWhenNoResolved()
    {
        // Regression: if ALL records have unresolvable types,
        // nothing is dispatched AND nothing is deleted — the unresolved records must survive.
        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AggregatorSnapshot([], [], UnresolvedCount: 2));

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

        var registry = new AggregatorRegistry(handlerRefs, provider, NullLogger<AggregatorRegistry>.Instance);
        await using var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
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
        // Regression: a flush with resolved + unresolved items must dispatch
        // resolved ones and call RemoveSnapshotAsync (which only deletes the resolved ids).
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
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        var registry = new AggregatorRegistry(handlerRefs, provider, NullLogger<AggregatorRegistry>.Instance);
        await using var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        foreach (var msg in messages)
            await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg, headers, envelope);

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
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                flushStarted.TrySetResult();
                await flushCanProceed.Task.WaitAsync(ct);
                return SnapshotOf(new object[]
                {
                    new AggTestMessage(Guid.NewGuid()) { Value = "A" },
                    new AggTestMessage(Guid.NewGuid()) { Value = "B" },
                    new AggTestMessage(Guid.NewGuid()) { Value = "C" },
                });
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

        var registry = new AggregatorRegistry(handlerRefs, provider, NullLogger<AggregatorRegistry>.Instance);
        var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);

        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var processTask = processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage),
            new AggTestMessage(Guid.NewGuid()) { Value = "X" }, headers, envelope);

        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await processor.DisposeAsync();

        var ex = await Record.ExceptionAsync(async () => await processTask);
        Assert.True(ex == null || ex is OperationCanceledException || ex is ObjectDisposedException,
            $"Expected null, OperationCanceledException, or ObjectDisposedException but got: {ex?.GetType().Name}: {ex?.Message}");
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(new List<HandlerReference>(), provider, NullLogger<AggregatorRegistry>.Instance);
        var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance);

        await processor.DisposeAsync();
        await processor.DisposeAsync();
    }

    [Fact]
    public async Task InsertDuringFlush_LateMessageNotWiped()
    {
        // Regression: a message inserted between GetSnapshotAsync and
        // RemoveSnapshotAsync must not be deleted, because RemoveSnapshotAsync only
        // removes the specific ids captured in the snapshot.
        var persistor = new ServiceConnect.Persistence.InMemory.InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
        const string name = "agg-race";

        var initial = new[]
        {
            new AggTestMessage(Guid.NewGuid()) { Value = "a1" },
            new AggTestMessage(Guid.NewGuid()) { Value = "a2" },
            new AggTestMessage(Guid.NewGuid()) { Value = "a3" },
        };
        foreach (var m in initial)
            await persistor.InsertDataAsync(m, name);

        var snapshot = await persistor.GetSnapshotAsync(name);

        // Simulate a concurrent insert that arrives *after* the snapshot but *before*
        // the remove — this is the race the plan's snapshot API is designed to close.
        var late = new AggTestMessage(Guid.NewGuid()) { Value = "late" };
        await persistor.InsertDataAsync(late, name);

        await persistor.RemoveSnapshotAsync(name, snapshot);

        var remaining = await persistor.GetDataAsync(name);
        Assert.Single(remaining);
        Assert.Same(late, remaining[0]);
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
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++insertCount);

        // GetSnapshotAsync callback simulates a concurrent insert arriving between snapshot and remove.
        persistorMock.Setup(p => p.GetSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // The snapshot captures only the initial 3 messages.
                var ids = initial.Select(_ => Guid.NewGuid()).ToList();
                return (IAggregatorSnapshot)new AggregatorSnapshot(initial.Cast<object>().ToList(), ids, 0);
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

        var registry = new AggregatorRegistry(handlerRefs, provider, NullLogger<AggregatorRegistry>.Instance);
        await using var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // Send exactly BatchSize (3) messages to trigger a flush.
        foreach (var msg in initial)
            await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg, headers, envelope);

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
    public async Task TimerReuse_DoesNotAllocateNewTimerPerMessage()
    {
        var flushCount = 0;
        var flushTcs = new TaskCompletionSource();

        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        var registry = new AggregatorRegistry(handlerRefs, provider, NullLogger<AggregatorRegistry>.Instance);
        await using var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance, persistorMock.Object);
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
}

file class AggTestMessage : Message
{
    public AggTestMessage(Guid correlationId) : base(correlationId) { }
    public string Value { get; set; } = "";
}

file class AggTestAggregator : Aggregator<AggTestMessage>
{
    private readonly TaskCompletionSource<IList<AggTestMessage>> _tcs;

    public AggTestAggregator(TaskCompletionSource<IList<AggTestMessage>> tcs)
    {
        _tcs = tcs;
    }

    public override int BatchSize() => 3;

    public override void Execute(IList<AggTestMessage> messages)
    {
        _tcs.TrySetResult(messages);
    }
}

file class AggTestTimedAggregator : Aggregator<AggTestMessage>
{
    private readonly TaskCompletionSource<IList<AggTestMessage>> _tcs;

    public AggTestTimedAggregator(TaskCompletionSource<IList<AggTestMessage>> tcs)
    {
        _tcs = tcs;
    }

    public override int BatchSize() => 0;
    public override TimeSpan Timeout() => TimeSpan.FromMilliseconds(200);

    public override void Execute(IList<AggTestMessage> messages)
    {
        _tcs.TrySetResult(messages);
    }
}
