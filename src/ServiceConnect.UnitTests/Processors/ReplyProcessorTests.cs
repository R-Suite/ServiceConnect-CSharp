using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class ReplyProcessorTests
{
    private readonly Mock<IRequestReplyManager> _mockReplyManager = new();
    private readonly Mock<IMessageSerializer> _mockSerializer = new();

    [Fact]
    public async Task ProcessAsync_WithResponseMessageId_RoutesToReplyManager()
    {
        var processor = new ReplyProcessor(_mockReplyManager.Object, _mockSerializer.Object);
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.ResponseMessageId] = "reply-123",
            [HeaderKeys.FullTypeName] = typeof(TestReplyMsg).AssemblyQualifiedName!
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestReplyMsg), null, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        _mockReplyManager.Verify(r => r.ProcessReply("reply-123", It.IsAny<byte[]>(), typeof(TestReplyMsg)), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithoutResponseMessageId_ReturnsNotHandled()
    {
        var processor = new ReplyProcessor(_mockReplyManager.Object, _mockSerializer.Object);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestReplyMsg), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_WithEmptyResponseMessageId_ReturnsNotHandled()
    {
        var processor = new ReplyProcessor(_mockReplyManager.Object, _mockSerializer.Object);
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
