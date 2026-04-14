using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class AggregatorProcessorTests
{
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
        persistorMock.Setup(p => p.GetDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<object>(messages));
        persistorMock.Setup(p => p.RemoveDataAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.RemoveAllAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        await using var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance);
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
    public async Task FlushAggregator_CallsRemoveAllAsync_NotPerMessageRemove()
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
        persistorMock.Setup(p => p.GetDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<object>(messages));
        persistorMock.Setup(p => p.RemoveAllAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        await using var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        foreach (var msg in messages)
            await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage), msg, headers, envelope);

        // R-001: Should call RemoveAllAsync once instead of RemoveDataAsync per message
        persistorMock.Verify(p => p.RemoveAllAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        persistorMock.Verify(p => p.RemoveDataAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_CancelsInFlightFlush()
    {
        // R-002: Dispose should cancel in-flight flushes via CancellationTokenSource
        var flushStarted = new TaskCompletionSource();
        var flushCanProceed = new TaskCompletionSource();

        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        persistorMock.Setup(p => p.GetDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                flushStarted.TrySetResult();
                await flushCanProceed.Task.WaitAsync(ct);
                return new List<object>
                {
                    new AggTestMessage(Guid.NewGuid()) { Value = "A" },
                    new AggTestMessage(Guid.NewGuid()) { Value = "B" },
                    new AggTestMessage(Guid.NewGuid()) { Value = "C" },
                };
            });
        persistorMock.Setup(p => p.RemoveAllAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance);

        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // Start processing which will trigger a flush that blocks in GetDataAsync
        var processTask = processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage),
            new AggTestMessage(Guid.NewGuid()) { Value = "X" }, headers, envelope);

        // Wait for flush to start
        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Dispose while flush is in-flight - should not throw
        await processor.DisposeAsync();

        // The process task should either complete, throw OperationCanceledException (from CTS cancel),
        // or throw ObjectDisposedException (from semaphore disposal during cleanup).
        var ex = await Record.ExceptionAsync(async () => await processTask);
        Assert.True(ex == null || ex is OperationCanceledException || ex is ObjectDisposedException,
            $"Expected null, OperationCanceledException, or ObjectDisposedException but got: {ex?.GetType().Name}: {ex?.Message}");
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        // R-002: Interlocked.Exchange guard prevents double-dispose
        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        var provider = services.BuildServiceProvider();

        var registry = new AggregatorRegistry(new List<HandlerReference>(), provider, NullLogger<AggregatorRegistry>.Instance);
        var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance);

        // Should not throw on multiple disposes
        await processor.DisposeAsync();
        await processor.DisposeAsync();
    }

    [Fact]
    public async Task TimerReuse_DoesNotAllocateNewTimerPerMessage()
    {
        // R-028 / P-013: Timer should be reused via Change() instead of creating new ones.
        // We verify this by checking that the timer fires correctly after multiple resets
        // (if timers were leaking, the old ones could fire prematurely).
        var flushCount = 0;
        var flushTcs = new TaskCompletionSource();

        var persistorMock = new Mock<IAggregatorPersistor>();
        persistorMock.Setup(p => p.InsertDataAsync(It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        persistorMock.Setup(p => p.CountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1); // Always below batch size to trigger timer
        persistorMock.Setup(p => p.GetDataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref flushCount);
                flushTcs.TrySetResult();
                return new List<object>();
            });
        persistorMock.Setup(p => p.RemoveAllAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        await using var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // Send multiple messages quickly — each should reset the same timer, not create a new one.
        // With a 200ms timeout, if old timers were still firing, we'd see multiple flushes.
        for (int i = 0; i < 5; i++)
        {
            await processor.ProcessAsync(new byte[] { 1 }, typeof(AggTestMessage),
                new AggTestMessage(Guid.NewGuid()) { Value = $"msg-{i}" }, headers, envelope);
            await Task.Delay(50); // 50ms between messages, well under the 200ms timeout
        }

        // Wait for the timer to fire (200ms after last message + some buffer)
        await flushTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Should have only one flush from the timer, not 5
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

    public override int BatchSize() => 0; // No batch size — flush by timeout only
    public override TimeSpan Timeout() => TimeSpan.FromMilliseconds(200);

    public override void Execute(IList<AggTestMessage> messages)
    {
        _tcs.TrySetResult(messages);
    }
}
