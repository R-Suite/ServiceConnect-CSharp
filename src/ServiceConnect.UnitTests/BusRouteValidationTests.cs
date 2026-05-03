using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class BusRouteValidationTests
{
    public static IEnumerable<object?[]> InvalidDestinations() =>
    [
        [(string[]?)null,                "destinations"],
        [Array.Empty<string>(),          "at least one destination"],
        [new[] { "" },                   "null or whitespace"],
        [new[] { "  " },                 "null or whitespace"],
        [new string?[] { "ok", null },   "null or whitespace"],
        [new[] { "ok", "with,comma" },   "comma"],
    ];

    [Theory]
    [MemberData(nameof(InvalidDestinations))]
    public async Task RouteAsync_InvalidDestinations_ThrowsArgumentException(
        string[]? destinations, string expectedMessageFragment)
    {
        var bus = BuildBus();
        // ArgumentNullException (thrown for null input by ThrowIfNull) is a subclass of
        // ArgumentException; ThrowsAnyAsync accepts both without requiring an exact type match.
        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            bus.RouteAsync(new TestMessage(), destinations!));
        Assert.Contains(expectedMessageFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RouteAsync_ValidDestinations_Succeeds()
    {
        var bus = BuildBus();
        // No exception thrown → valid inputs accepted.
        await bus.RouteAsync(new TestMessage(), ["q1", "q2", "q3"]);
    }

    private static Bus BuildBus()
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer
            .Setup(s => s.Serialize(It.IsAny<TestMessage>()))
            .Returns([]);

        var filterPipeline = new Mock<IFilterPipeline>();

        var sendPipeline = new Mock<ISendMessagePipeline>();
        sendPipeline
            .Setup(p => p.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        sendPipeline
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        var requestReplyManager = new Mock<IRequestReplyManager>();
        var logger = new Mock<ILogger<Bus>>();

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.Setup(q => q.QueueName).Returns("test-queue");

        var pipelineConfig = new Mock<IPipelineConfiguration>();
        pipelineConfig.Setup(p => p.OutgoingFilters).Returns([]);

        var dispatcher = new Mock<IMessageDispatcher>();
        IList<HandlerReference> handlerReferences = [];
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var scopeAccessor = new ConsumeScopeAccessor();

        return new Bus(
            serializer.Object,
            filterPipeline.Object,
            sendPipeline.Object,
            requestReplyManager.Object,
            logger.Object,
            queueConfig.Object,
            dispatcher.Object,
            handlerReferences,
            pipelineConfig.Object,
            scopeFactory,
            scopeAccessor,
            consumer: null,
            producer: null);
    }

    private sealed class TestMessage() : Message(Guid.NewGuid()) { }
}
