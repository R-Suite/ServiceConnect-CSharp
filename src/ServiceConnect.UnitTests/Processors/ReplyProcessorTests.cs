using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class ReplyProcessorTests
{
    [Fact]
    public async Task ProcessAsync_WithResponseMessageId_RoutesToReplyManager()
    {
        var replyManager = new TestReplyStatusRequestReplyManager(true);

        var processor = new ReplyProcessor(replyManager);
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.ResponseMessageId] = "reply-123",
            [HeaderKeys.FullTypeName] = typeof(TestReplyMsg).AssemblyQualifiedName!
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestReplyMsg), null, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        Assert.Equal("reply-123", replyManager.LastMessageId);
        Assert.Equal(typeof(TestReplyMsg), replyManager.LastMessageType);
    }

    [Fact]
    public async Task ProcessAsync_WhenReplyManagerRejectsReply_ReturnsNotHandled()
    {
        var replyManager = new TestReplyStatusRequestReplyManager(false);

        var processor = new ReplyProcessor(replyManager);
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.ResponseMessageId] = "reply-123",
            [HeaderKeys.FullTypeName] = typeof(TestReplyMsg).AssemblyQualifiedName!
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestReplyMsg), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_WhenReplyStatusContractIsUnavailable_ReturnsNotHandled()
    {
        var processor = new ReplyProcessor(null);
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.ResponseMessageId] = "reply-123",
            [HeaderKeys.FullTypeName] = typeof(TestReplyMsg).AssemblyQualifiedName!
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestReplyMsg), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_WithoutResponseMessageId_ReturnsNotHandled()
    {
        var processor = new ReplyProcessor(new TestReplyStatusRequestReplyManager(true));
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestReplyMsg), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_WithEmptyResponseMessageId_ReturnsNotHandled()
    {
        var processor = new ReplyProcessor(new TestReplyStatusRequestReplyManager(true));
        var headers = new Dictionary<string, object> { [HeaderKeys.ResponseMessageId] = "" };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestReplyMsg), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }
}

file class TestReplyMsg : Message
{
    public TestReplyMsg(Guid correlationId) : base(correlationId) { }
}

file sealed class TestReplyStatusRequestReplyManager(bool shouldHandle) : IReplyStatusRequestReplyManager
{
    public string? LastMessageId { get; private set; }
    public Type? LastMessageType { get; private set; }

    public bool TryProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type)
    {
        LastMessageId = messageId;
        LastMessageType = type;
        return shouldHandle;
    }

    public bool IsTrackedRequest(string messageId) => false;
}
