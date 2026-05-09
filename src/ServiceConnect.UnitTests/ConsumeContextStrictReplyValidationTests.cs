using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

// Covers H11. The legacy fallback inside IsTrustedRequestReplyEnvelope trusts header
// fields that any external producer aware of our queue name can fabricate. Strict mode
// disables that fallback; the tracked-request path remains the strong primary check.
public class ConsumeContextStrictReplyValidationTests
{
    private readonly Mock<IBus> _mockBus = new();
    private readonly TestReplyStatusRequestReplyManager _replyStatusRequestReplyManager = new();
    private readonly IQueueConfiguration _queueConfig;

    public ConsumeContextStrictReplyValidationTests()
    {
        _mockBus
            .Setup(b => b.SendAsync(It.IsAny<ConsumeContextStrictReply>(), It.IsAny<SendOptions?>()))
            .Returns(Task.CompletedTask);

        _queueConfig = new QueueConfiguration
        {
            QueueName = "my-queue",
            ErrorQueueName = "errors",
            AuditQueueName = "audit"
        };
    }

    [Fact]
    public void IsTrustedRequestReplyEnvelope_FallbackEnvelope_TrustedInLaxMode_PreservesLegacyBehaviour()
    {
        var headers = BuildFallbackEnvelopeHeaders();
        var busConfig = new BusConfiguration { StrictReplyValidation = false };

        Assert.True(ConsumeContext.IsTrustedRequestReplyEnvelope(
            headers, _queueConfig, _replyStatusRequestReplyManager, busConfig));
    }

    [Fact]
    public void IsTrustedRequestReplyEnvelope_FallbackEnvelope_RejectedInStrictMode()
    {
        var headers = BuildFallbackEnvelopeHeaders();
        var busConfig = new BusConfiguration { StrictReplyValidation = true };

        Assert.False(ConsumeContext.IsTrustedRequestReplyEnvelope(
            headers, _queueConfig, _replyStatusRequestReplyManager, busConfig));
    }

    [Fact]
    public void IsTrustedRequestReplyEnvelope_TrackedRequest_AlwaysTrusted_RegardlessOfStrictMode()
    {
        var requestMessageId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.RequestMessageId] = requestMessageId,
            [HeaderKeys.SourceAddress] = "another-bus-queue",
            [HeaderKeys.MessageId] = Guid.NewGuid().ToString(),
            [HeaderKeys.DestinationAddress] = "my-queue"
        };
        _replyStatusRequestReplyManager.TrackedRequests.Add(requestMessageId);

        Assert.True(ConsumeContext.IsTrustedRequestReplyEnvelope(
            headers, _queueConfig, _replyStatusRequestReplyManager,
            new BusConfiguration { StrictReplyValidation = false }));
        Assert.True(ConsumeContext.IsTrustedRequestReplyEnvelope(
            headers, _queueConfig, _replyStatusRequestReplyManager,
            new BusConfiguration { StrictReplyValidation = true }));
    }

    [Fact]
    public void IsTrustedRequestReplyEnvelope_NoRequestMessageId_RejectedInBothModes()
    {
        var headers = new Dictionary<string, object>();

        Assert.False(ConsumeContext.IsTrustedRequestReplyEnvelope(
            headers, _queueConfig, _replyStatusRequestReplyManager,
            new BusConfiguration { StrictReplyValidation = false }));
        Assert.False(ConsumeContext.IsTrustedRequestReplyEnvelope(
            headers, _queueConfig, _replyStatusRequestReplyManager,
            new BusConfiguration { StrictReplyValidation = true }));
    }

    [Fact]
    public async Task ReplyAsync_FallbackEnvelope_RejectedInStrictMode_ThrowsForUnknownQueue()
    {
        var headers = BuildFallbackEnvelopeHeaders();
        // Source is not in any queue mapping, so without the fallback we fall through to
        // the IsKnownQueue check and reject.
        var busConfig = new BusConfiguration { StrictReplyValidation = true };
        var context = new ConsumeContext(
            _mockBus.Object,
            headers,
            _queueConfig,
            busConfig,
            _replyStatusRequestReplyManager,
            default);

        var reply = new ConsumeContextStrictReply(Guid.NewGuid()) { Value = "hello" };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => context.ReplyAsync(reply));
        Assert.Contains("not a recognized queue", ex.Message);
    }

    [Fact]
    public async Task ReplyAsync_TrackedRequest_StillSucceedsInStrictMode()
    {
        var requestMessageId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.SourceAddress] = "another-bus-queue",
            [HeaderKeys.RequestMessageId] = requestMessageId
        };
        _replyStatusRequestReplyManager.TrackedRequests.Add(requestMessageId);

        var busConfig = new BusConfiguration { StrictReplyValidation = true };
        var context = new ConsumeContext(
            _mockBus.Object,
            headers,
            _queueConfig,
            busConfig,
            _replyStatusRequestReplyManager,
            default);

        var reply = new ConsumeContextStrictReply(Guid.NewGuid()) { Value = "hello" };

        await context.ReplyAsync(reply);

        _mockBus.Verify(b => b.SendAsync(
            reply,
            It.Is<SendOptions?>(o =>
                o.HasValue &&
                o.Value.EndPoint == "another-bus-queue" &&
                o.Value.Headers != null &&
                o.Value.Headers["ResponseMessageId"] == requestMessageId)),
            Times.Once);
    }

    private static Dictionary<string, object> BuildFallbackEnvelopeHeaders() =>
        new()
        {
            [HeaderKeys.RequestMessageId] = Guid.NewGuid().ToString(),
            [HeaderKeys.SourceAddress] = "external-queue",
            [HeaderKeys.MessageId] = Guid.NewGuid().ToString(),
            [HeaderKeys.DestinationAddress] = "my-queue"
        };
}

public class ConsumeContextStrictReply(Guid correlationId) : Message(correlationId)
{
    public string? Value { get; set; }
}
