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
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies H21: Bus no longer stamps MessageType on the outgoing Envelope.
/// MessageType is the operation name ("Publish"|"Send"|"ByteStream") and is
/// stamped exclusively by OutboundHeaderBuilder. Type identity is carried by
/// TypeName (FullName) and FullTypeName (AQN).
/// </summary>
public sealed class BusEnvelopeMessageTypeTests
{
    // Shared mocks; each test creates its own busWithFilters so the filter-path
    // (CreateEnvelope → ExecuteOutgoingFiltersAsync) is taken.
    private readonly Mock<IMessageSerializer> _mockSerializer = new();
    private readonly Mock<IFilterPipeline> _mockFilterPipeline = new();
    private readonly Mock<ISendMessagePipeline> _mockSendPipeline = new();
    private readonly Mock<IRequestReplyManager> _mockRequestReplyManager = new();
    private readonly Mock<IBusConfiguration> _mockConfig = new();
    private readonly Mock<IQueueConfiguration> _mockQueueConfig = new();
    private readonly Mock<ILogger<Bus>> _mockLogger = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConsumeScopeAccessor _scopeAccessor;
    private readonly Mock<IPipelineConfiguration> _pipelineConfigWithFilter;

    public BusEnvelopeMessageTypeTests()
    {
        _mockQueueConfig.Setup(x => x.QueueName).Returns("test-queue");

        // Default: filter pipeline returns Continue.
        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);

        _mockSerializer.Setup(x => x.Serialize(It.IsAny<FakeMessage1>())).Returns([1, 2, 3]);

        _scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _scopeAccessor = new ConsumeScopeAccessor();

        // At least one registered filter forces the slow path (CreateEnvelope).
        _pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        _pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);
    }

    private Bus CreateBusWithFilters()
        => new(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            new Mock<IMessageDispatcher>().Object,
            [],
            _pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);

    /// <summary>
    /// Post-H21: the envelope handed to outgoing filters must NOT contain MessageType.
    /// (Bus used to stamp envelope.Headers["MessageType"] = type.FullName; that stamp is dead.)
    /// </summary>
    [Fact]
    public async Task PublishAsync_EnvelopeDoesNotContainMessageTypeKey()
    {
        Envelope? captured = null;
        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Envelope env, CancellationToken _) =>
            {
                captured = env;
                return FilterAction.Continue;
            });

        var bus = CreateBusWithFilters();
        var message = new FakeMessage1(Guid.NewGuid());

        await bus.PublishAsync(message);

        Assert.NotNull(captured);
        Assert.False(captured!.Headers.ContainsKey(HeaderKeys.MessageType));
    }

    /// <summary>
    /// Post-H21: same guarantee holds on the SendAsync path.
    /// </summary>
    [Fact]
    public async Task SendAsync_EnvelopeDoesNotContainMessageTypeKey()
    {
        Envelope? captured = null;
        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Envelope env, CancellationToken _) =>
            {
                captured = env;
                return FilterAction.Continue;
            });

        var bus = CreateBusWithFilters();
        var message = new FakeMessage1(Guid.NewGuid());

        await bus.SendAsync(message);

        Assert.NotNull(captured);
        Assert.False(captured!.Headers.ContainsKey(HeaderKeys.MessageType));
    }

    /// <summary>
    /// Belt-and-braces: removing MessageType from Bus stamps must not accidentally
    /// drop the other Bus-authoritative headers that outgoing filters depend on.
    /// </summary>
    [Fact]
    public async Task PublishAsync_EnvelopePreservesCorrelationIdAndMessageId_StillStampedByBus()
    {
        Envelope? captured = null;
        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Envelope env, CancellationToken _) =>
            {
                captured = env;
                return FilterAction.Continue;
            });

        var correlationId = Guid.NewGuid();
        var bus = CreateBusWithFilters();
        var message = new FakeMessage1(correlationId);

        await bus.PublishAsync(message);

        Assert.NotNull(captured);
        Assert.True(captured!.Headers.ContainsKey(HeaderKeys.CorrelationId));
        Assert.True(captured.Headers.ContainsKey(HeaderKeys.MessageId));
        Assert.Equal(correlationId.ToString(), captured.Headers[HeaderKeys.CorrelationId]?.ToString());
    }
}
