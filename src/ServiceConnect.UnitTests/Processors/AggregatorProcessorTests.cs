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
        using var processor = new AggregatorProcessor(registry, provider, NullLogger<AggregatorProcessor>.Instance);
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
