using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes;
using Xunit;

namespace ServiceConnect.UnitTests.BusTests;

/// <summary>
/// Verifies the polymorphic fan-out of <see cref="Bus.PublishAsync{T}"/>: a derived message is
/// published to its own exchange AND to each ancestor type's exchange (walking BaseType up to,
/// but excluding, <see cref="Message"/>), re-stamped with that ancestor's type per hop. This
/// matches master's recursive <c>Publish&lt;TBase&gt;</c> so a subscriber bound only to a
/// base-type exchange receives derived messages, and so the C#, master, and Node runtimes
/// interoperate for polymorphic subscribers on the same bus.
/// </summary>
public sealed class BusPublishAncestorFanoutTests
{
    // Hierarchy: FakeLeafEvent : FakeMiddleEvent : FakeAncestorEvent : Message.
    public abstract class FakeAncestorEvent(Guid correlationId) : Message(correlationId);
    public class FakeMiddleEvent(Guid correlationId) : FakeAncestorEvent(correlationId);
    public sealed class FakeLeafEvent(Guid correlationId) : FakeMiddleEvent(correlationId);

    // Direct Message descendant — no polymorphic ancestors above Message.
    public sealed class FakeFlatEvent(Guid correlationId) : Message(correlationId);

    private readonly Mock<IMessageSerializer> _mockSerializer = new();
    private readonly Mock<IFilterPipeline> _mockFilterPipeline = new();
    private readonly Mock<ISendMessagePipeline> _mockSendPipeline = new();
    private readonly Mock<IRequestReplyManager> _mockRequestReplyManager = new();
    private readonly Mock<IPipelineConfiguration> _mockPipelineConfig = new();
    private readonly Mock<ILogger<Bus>> _mockLogger = new();
    private readonly Mock<IQueueConfiguration> _mockQueueConfig = new();
    private readonly Mock<IMessageDispatcher> _mockDispatcher = new();
    private readonly Bus _bus;

    public BusPublishAncestorFanoutTests()
    {
        // No outgoing filters — Bus takes the fast header-build path.
        _mockPipelineConfig.Setup(x => x.OutgoingFilters).Returns([]);
        _mockQueueConfig.Setup(x => x.QueueName).Returns("test-queue");
        _mockSerializer.SetupSerializeAny<FakeLeafEvent>([1, 2, 3]);
        _mockSerializer.SetupSerializeAny<FakeFlatEvent>([4, 5, 6]);

        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _bus = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            [],
            _mockPipelineConfig.Object,
            scopeFactory,
            new ConsumeScopeAccessor());
    }

    private List<SendContext> CapturePublishContexts()
    {
        var contexts = new List<SendContext>();
        _mockSendPipeline
            .Setup(x => x.ExecutePublishMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) => contexts.Add(ctx))
            .Returns(Task.CompletedTask);
        return contexts;
    }

    [Fact]
    public async Task PublishAsync_DerivedType_FansOutToEachAncestorExchange_ReStampingTypePerHop()
    {
        var contexts = CapturePublishContexts();

        await _bus.PublishAsync(new FakeLeafEvent(Guid.NewGuid()));

        // One publish per type in the hierarchy up to (but excluding) Message.
        Assert.Equal(3, contexts.Count);
        Assert.Equal(typeof(FakeLeafEvent), contexts[0].MessageType);
        Assert.Equal(typeof(FakeMiddleEvent), contexts[1].MessageType);
        Assert.Equal(typeof(FakeAncestorEvent), contexts[2].MessageType);

        // Never publishes for the Message base type itself.
        Assert.DoesNotContain(contexts, c => c.MessageType == typeof(Message));

        // Every hop is a publish carrying the same body bytes.
        Assert.All(contexts, c => Assert.Equal(SendOperation.Publish, c.Operation));
        Assert.All(contexts, c => Assert.True(c.MessageBytes.Span.SequenceEqual(new byte[] { 1, 2, 3 })));
    }

    [Fact]
    public async Task PublishAsync_DerivedType_AllHopsShareOneMessageId()
    {
        var contexts = CapturePublishContexts();

        await _bus.PublishAsync(new FakeLeafEvent(Guid.NewGuid()));

        // Master carries a single MessageId across the recursive ancestor publishes; the
        // snapshot-and-clone of the prepared headers reproduces that here.
        var messageIds = contexts.Select(c => c.Headers[HeaderKeys.MessageId]).Distinct().ToList();
        Assert.Single(messageIds);
    }

    [Fact]
    public async Task PublishAsync_DirectMessageDescendant_PublishesOnceOnly()
    {
        var contexts = CapturePublishContexts();

        await _bus.PublishAsync(new FakeFlatEvent(Guid.NewGuid()));

        // BaseType is Message, so there are no ancestor exchanges to fan out to.
        Assert.Single(contexts);
        Assert.Equal(typeof(FakeFlatEvent), contexts[0].MessageType);
    }
}
