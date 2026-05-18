using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

// The legacy fallback inside IsTrustedRequestReplyEnvelope trusts header fields that
// any external producer aware of our queue name can fabricate. Strict mode disables
// that fallback; the tracked-request path remains the strong primary check.
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

    [Fact]
    public void IsKnownQueue_LargeMappingSet_LooksUpCorrectly()
    {
        // Build 1000 mappings × 10 queues each.
        var mappings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        for (int i = 0; i < 1000; i++)
        {
            mappings[$"msg.type.{i}"] = [.. Enumerable.Range(0, 10).Select(j => $"queue.{i}.{j}")];
        }

        var config = new Mock<IQueueConfiguration>();
        config.SetupGet(c => c.QueueName).Returns("self");
        config.SetupGet(c => c.ErrorQueueName).Returns("self.error");
        config.SetupGet(c => c.AuditQueueName).Returns("self.audit");
        config.SetupGet(c => c.QueueMappings).Returns(mappings);

        Assert.True(ConsumeContext.IsKnownQueue("queue.999.9", config.Object));
        Assert.False(ConsumeContext.IsKnownQueue("nope", config.Object));
        Assert.True(ConsumeContext.IsKnownQueue("QUEUE.999.9", config.Object)); // case-insensitive
        Assert.True(ConsumeContext.IsKnownQueue("self", config.Object));
        Assert.True(ConsumeContext.IsKnownQueue("self.error", config.Object));
        Assert.True(ConsumeContext.IsKnownQueue("self.audit", config.Object));
    }

    [Fact]
    public void IsKnownQueue_RepeatedLookups_StayUnderPerfBudget()
    {
        var mappings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        for (int i = 0; i < 1000; i++)
        {
            mappings[$"msg.type.{i}"] = [.. Enumerable.Range(0, 10).Select(j => $"queue.{i}.{j}")];
        }

        var config = new Mock<IQueueConfiguration>();
        config.SetupGet(c => c.QueueName).Returns("self");
        config.SetupGet(c => c.ErrorQueueName).Returns("self.error");
        config.SetupGet(c => c.AuditQueueName).Returns("self.audit");
        config.SetupGet(c => c.QueueMappings).Returns(mappings);

        // Pre-build probe strings outside the timed window so the benchmark isolates
        // hash-lookup speed from string-allocation throughput. Without this the timed
        // loop is dominated by 10k small-string allocations on a slow CI runner.
        var probes = Enumerable.Range(0, 10_000)
            .Select(i => $"queue.{i % 1000}.{i % 10}")
            .ToArray();

        // Warm up the cache (so first-call flattening cost doesn't dominate).
        ConsumeContext.IsKnownQueue("warmup", config.Object);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var probe in probes)
        {
            ConsumeContext.IsKnownQueue(probe, config.Object);
        }

        sw.Stop();
        Assert.True(sw.Elapsed.TotalMilliseconds < 50,
            $"IsKnownQueue is too slow: {sw.Elapsed.TotalMilliseconds:0.##}ms for 10k lookups against 10k mappings");
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
