using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class BusCreateStreamValidationTests
{
    // Constructs a Bus with a live producer mock so CreateStream reaches the validation
    // and stream-creation logic rather than the "no producer" early exit.
    private static Bus BuildBusWithProducer()
    {
        var mockSerializer = new Mock<IMessageSerializer>();
        var mockFilterPipeline = new Mock<IFilterPipeline>();
        var mockSendPipeline = new Mock<ISendMessagePipeline>();
        var mockRequestReplyManager = new Mock<IRequestReplyManager>();
        var mockLogger = new Mock<ILogger<Bus>>();
        var mockQueueConfig = new Mock<IQueueConfiguration>();
        mockQueueConfig.Setup(x => x.QueueName).Returns("test-queue");
        var mockPipelineConfig = new Mock<IPipelineConfiguration>();
        mockPipelineConfig.Setup(x => x.OutgoingFilters).Returns([]);
        var mockDispatcher = new Mock<IMessageDispatcher>();
        var mockProducer = new Mock<IProducer>();
        IList<HandlerReference> handlerReferences = [];
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var scopeAccessor = new ConsumeScopeAccessor();

        return new Bus(
            mockSerializer.Object,
            mockFilterPipeline.Object,
            mockSendPipeline.Object,
            mockRequestReplyManager.Object,
            mockLogger.Object,
            mockQueueConfig.Object,
            mockDispatcher.Object,
            handlerReferences,
            mockPipelineConfig.Object,
            scopeFactory,
            scopeAccessor,
            producer: mockProducer.Object);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateStream_NullOrWhitespaceEndpoint_ThrowsArgumentException(string? endpoint)
    {
        var bus = BuildBusWithProducer();
        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        // (a subclass of ArgumentException) and ArgumentException for empty/whitespace.
        // IsAssignableFrom accepts both without requiring an exact type match.
        var ex = Record.Exception(() => bus.CreateStream<FakeMessage1>(endpoint!));
        Assert.IsAssignableFrom<ArgumentException>(ex);
    }

    [Fact]
    public void CreateStream_ValidEndpoint_DoesNotThrow()
    {
        var bus = BuildBusWithProducer();
        var stream = bus.CreateStream<FakeMessage1>("valid.endpoint");
        Assert.NotNull(stream);
    }
}
