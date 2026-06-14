using Moq;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Handlers;

public class HandlerSignatureTests
{
    public sealed class TestMessage : Message
    {
        public TestMessage() : base(Guid.NewGuid()) { }
    }

    public sealed class TestHandler : IMessageHandler<TestMessage>
    {
        public IConsumeContext? CapturedContext { get; private set; }
        public TestMessage? CapturedMessage { get; private set; }

        public Task HandleAsync(TestMessage message, IConsumeContext context, CancellationToken cancellationToken = default)
        {
            CapturedMessage = message;
            CapturedContext = context;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task HandleAsync_ReceivesContextAsParameter()
    {
        var handler = new TestHandler();
        var message = new TestMessage();
        var context = Mock.Of<IConsumeContext>();

        await handler.HandleAsync(message, context, CancellationToken.None);

        Assert.Same(message, handler.CapturedMessage);
        Assert.Same(context, handler.CapturedContext);
    }

}
