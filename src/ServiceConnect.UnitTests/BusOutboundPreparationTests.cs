using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

public sealed class BusOutboundPreparationTests
{
    private static readonly byte[] SerializedBytes = [0xAB, 0xCD];

    private static (Bus bus, Mock<IFilterPipeline> filterPipeline, Mock<IMessageSerializer> serializer) BuildBus(bool hasOutgoingFilters)
    {
        var mockSerializer = new Mock<IMessageSerializer>();
        mockSerializer.SetupSerializeAny<FakeMessage1>(SerializedBytes);

        var mockFilterPipeline = new Mock<IFilterPipeline>();
        // Default: filters continue — individual tests override when needed.
        mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);

        var mockSendPipeline = new Mock<ISendMessagePipeline>();
        var mockRequestReplyManager = new Mock<IRequestReplyManager>();
        var mockLogger = new Mock<ILogger<Bus>>();
        var mockQueueConfig = new Mock<IQueueConfiguration>();
        mockQueueConfig.Setup(x => x.QueueName).Returns("test-queue");

        var mockPipelineConfig = new Mock<IPipelineConfiguration>();
        // An empty list means no outgoing filters; a non-empty list activates the filter path.
        mockPipelineConfig.Setup(x => x.OutgoingFilters)
            .Returns(hasOutgoingFilters ? [typeof(object)] : []);

        var mockDispatcher = new Mock<IMessageDispatcher>();
        IReadOnlyList<HandlerReference> handlerReferences = [];
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var scopeAccessor = new ConsumeScopeAccessor();

        var bus = new Bus(
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
            scopeAccessor);

        return (bus, mockFilterPipeline, mockSerializer);
    }

    [Fact]
    public async Task PrepareOutboundAsync_NoFilters_ReturnsBytesAndDirectHeaders_NotStopped()
    {
        var (bus, filterPipeline, _) = BuildBus(hasOutgoingFilters: false);
        var message = new FakeMessage1(Guid.NewGuid());
        var correlationId = message.CorrelationId;

        var result = await bus.PrepareOutboundAsync(message, callerHeaders: null, CancellationToken.None);

        // Not stopped; the full byte content matches the stub.
        Assert.False(result.Stopped);
        Assert.Equal(SerializedBytes, result.Bytes.ToArray());

        // Headers are stamped with the correlation ID (fast path, BuildHeadersDirect).
        Assert.True(result.Headers.TryGetValue("CorrelationId", out var cid));
        Assert.Equal(correlationId.ToString(), cid);

        // Filter pipeline must never be called on the fast path.
        filterPipeline.Verify(
            x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PrepareOutboundAsync_FiltersAccept_ReturnsHeadersFromEnvelope_NotStopped()
    {
        var (bus, filterPipeline, _) = BuildBus(hasOutgoingFilters: true);
        var message = new FakeMessage1(Guid.NewGuid());
        var correlationId = message.CorrelationId;

        // Filter returns Continue — message is not stopped.
        filterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);

        var result = await bus.PrepareOutboundAsync(message, callerHeaders: null, CancellationToken.None);

        Assert.False(result.Stopped);
        Assert.Equal(SerializedBytes, result.Bytes.ToArray());

        // Headers are extracted from the envelope, so CorrelationId must be present.
        Assert.True(result.Headers.ContainsKey("CorrelationId"));
        Assert.Equal(correlationId.ToString(), result.Headers["CorrelationId"]);

        // Filter must have been invoked exactly once.
        filterPipeline.Verify(
            x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PrepareOutboundAsync_FiltersStop_ReturnsStoppedTrue()
    {
        var (bus, filterPipeline, mockSerializer) = BuildBus(hasOutgoingFilters: true);
        var message = new FakeMessage1(Guid.NewGuid());

        filterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        var result = await bus.PrepareOutboundAsync(message, callerHeaders: null, CancellationToken.None);

        // Callers must check Stopped before reading Bytes or Headers.
        Assert.True(result.Stopped);

        // Serialisation must have run before the filter — the helper always serialises first.
        mockSerializer.VerifySerialize(message, Times.Once());
    }

    [Fact]
    public async Task PrepareOutboundAsync_CallerHeaders_FlowToEnvelopeOnFilterPath()
    {
        var (bus, filterPipeline, _) = BuildBus(hasOutgoingFilters: true);
        var message = new FakeMessage1(Guid.NewGuid());
        var callerHeaders = new Dictionary<string, string> { ["X-Trace-Id"] = "abc123" };

        Envelope? capturedEnvelope = null;
        filterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .Callback<Envelope, CancellationToken>((env, _) => capturedEnvelope = env)
            .ReturnsAsync(FilterAction.Continue);

        await bus.PrepareOutboundAsync(message, callerHeaders, CancellationToken.None);

        Assert.NotNull(capturedEnvelope);
        Assert.True(capturedEnvelope.Headers.TryGetValue("X-Trace-Id", out var traceId));
        Assert.Equal("abc123", traceId?.ToString());
    }

    [Fact]
    public async Task PrepareOutboundAsync_CallerHeaders_FlowToDirectHeadersOnNoFilterPath()
    {
        var (bus, _, _) = BuildBus(hasOutgoingFilters: false);
        var message = new FakeMessage1(Guid.NewGuid());
        var callerHeaders = new Dictionary<string, string> { ["X-Trace-Id"] = "trace-42" };

        var result = await bus.PrepareOutboundAsync(message, callerHeaders, CancellationToken.None);

        Assert.False(result.Stopped);
        Assert.True(result.Headers.TryGetValue("X-Trace-Id", out var v));
        Assert.Equal("trace-42", v);
    }

    [Fact]
    public async Task PrepareOutboundAsync_CancellationToken_FlowsThroughToFilter()
    {
        var (bus, filterPipeline, _) = BuildBus(hasOutgoingFilters: true);
        var message = new FakeMessage1(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        CancellationToken capturedToken = default;
        filterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .Callback<Envelope, CancellationToken>((_, ct) => capturedToken = ct)
            .ReturnsAsync(FilterAction.Continue);

        await bus.PrepareOutboundAsync(message, callerHeaders: null, token);

        Assert.Equal(token, capturedToken);
    }

    [Fact]
    public async Task PrepareOutboundForRequestAsync_NoFilters_SkipsSerializeAndReturnsDirectHeaders()
    {
        var (bus, filterPipeline, mockSerializer) = BuildBus(hasOutgoingFilters: false);
        var message = new FakeMessage1(Guid.NewGuid());
        var correlationId = message.CorrelationId;

        var result = await bus.PrepareOutboundForRequestAsync(message, callerHeaders: null, CancellationToken.None);

        Assert.False(result.Stopped);
        Assert.NotNull(result.Headers);
        Assert.True(result.Headers.TryGetValue("CorrelationId", out var cid));
        Assert.Equal(correlationId.ToString(), cid);

        // The request path skips the local serialise when no filters are registered
        // because RequestReplyManager re-serialises downstream.
        mockSerializer.VerifySerialize(message, Times.Never());
        filterPipeline.Verify(
            x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PrepareOutboundForRequestAsync_FiltersAccept_SerializesAndReturnsHeaders()
    {
        var (bus, filterPipeline, mockSerializer) = BuildBus(hasOutgoingFilters: true);
        var message = new FakeMessage1(Guid.NewGuid());
        var correlationId = message.CorrelationId;

        filterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);

        var result = await bus.PrepareOutboundForRequestAsync(message, callerHeaders: null, CancellationToken.None);

        Assert.False(result.Stopped);
        Assert.NotNull(result.Headers);
        Assert.True(result.Headers.TryGetValue("CorrelationId", out var cid));
        Assert.Equal(correlationId.ToString(), cid);

        // The filter path requires a local serialise so the envelope's wire body can be inspected.
        mockSerializer.VerifySerialize(message, Times.Once());
        filterPipeline.Verify(
            x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PrepareOutboundForRequestAsync_FiltersStop_ReturnsStoppedTrue()
    {
        var (bus, filterPipeline, _) = BuildBus(hasOutgoingFilters: true);
        var message = new FakeMessage1(Guid.NewGuid());

        filterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        var result = await bus.PrepareOutboundForRequestAsync(message, callerHeaders: null, CancellationToken.None);

        Assert.True(result.Stopped);
    }
}
