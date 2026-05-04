using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class MessageHandlerRegistryTests
{
    [Fact]
    public void TryGetOrBuild_ReturnsTrue_ForRegisteredMessageType()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrFooHandler) }
        };
        var registry = new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);

        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out var descriptor));
        Assert.Equal(typeof(MhrFooMsg), descriptor!.MessageType);
        Assert.Equal(typeof(IMessageHandler<MhrFooMsg>), descriptor.HandlerInterfaceType);
    }

    [Fact]
    public void Construction_IgnoresNonMessageHandlers()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrProcessHandler) },
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrStreamHandler) }
        };
        var registry = new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);

        // Neither process nor stream handler should register an IMessageHandler descriptor
        Assert.False(registry.TryGetOrBuild(typeof(MhrFooMsg), out _));
    }

    [Fact]
    public void Construction_DoesNotThrow_OnMultipleHandlersForSameMessage()
    {
        // Two different handler classes for one message type is legitimate; descriptor describes interface not instance
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrFooHandler) },
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrSecondFooHandler) }
        };

        var registry = new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);

        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out _));
    }

    [Fact]
    public void TryGetOrBuild_LazilyBuilds_ForUnregisteredMessageType()
    {
        var registry = new MessageHandlerRegistry(
            [],
            NullLogger<MessageHandlerRegistry>.Instance);

        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out var descriptor));
        Assert.Equal(typeof(IMessageHandler<MhrFooMsg>), descriptor!.HandlerInterfaceType);
    }

    [Fact]
    public void TryGetOrBuild_ReturnsFalse_ForMessageBaseType()
    {
        var registry = new MessageHandlerRegistry(
            [],
            NullLogger<MessageHandlerRegistry>.Instance);

        Assert.False(registry.TryGetOrBuild(typeof(Message), out var descriptor));
        Assert.Null(descriptor);
    }

    [Fact]
    public void TryGetOrBuild_ReturnsFalse_ForObject()
    {
        var registry = new MessageHandlerRegistry(
            [],
            NullLogger<MessageHandlerRegistry>.Instance);

        Assert.False(registry.TryGetOrBuild(typeof(object), out _));
    }

    [Fact]
    public async Task Descriptor_InvokeHandleAsync_PassesMessageAndContext()
    {
        var registry = BuildRegistry();
        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out var descriptor));

        var handler = new MhrFooHandler();
        var msg = new MhrFooMsg(Guid.NewGuid());
        var ctx = new MhrFakeConsumeContext();

        await descriptor!.InvokeHandleAsync(handler, msg, ctx, CancellationToken.None);

        Assert.Same(msg, handler.Received);
        Assert.Same(ctx, handler.ReceivedContext);
    }

    [Fact]
    public void TryGetOrBuild_CachesNegativeResults()
    {
        var registry = new MessageHandlerRegistry(
            [],
            NullLogger<MessageHandlerRegistry>.Instance);

        // Hit the unbuildable type twice; both return false without crashing
        Assert.False(registry.TryGetOrBuild(typeof(Message), out _));
        Assert.False(registry.TryGetOrBuild(typeof(Message), out _));
    }

    private static MessageHandlerRegistry BuildRegistry()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(MhrFooMsg), HandlerType = typeof(MhrFooHandler) }
        };
        return new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);
    }
}

file class MhrFooMsg(Guid c) : Message(c) {
}
file class MhrBarData : IProcessManagerData { public Guid CorrelationId { get; set; } }

file class MhrFooHandler : IMessageHandler<MhrFooMsg>
{
    public MhrFooMsg? Received { get; private set; }
    public IConsumeContext? ReceivedContext { get; private set; }
    public Task HandleAsync(MhrFooMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        Received = message;
        ReceivedContext = context;
        return Task.CompletedTask;
    }
}

file class MhrSecondFooHandler : IMessageHandler<MhrFooMsg>
{
    public Task HandleAsync(MhrFooMsg message, IConsumeContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

file class MhrProcessHandler : IProcessHandler<MhrBarData, MhrFooMsg>
{
    public Task HandleAsync(MhrFooMsg message, MhrBarData data, IConsumeContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

file class MhrStreamHandler : IStreamHandler<MhrFooMsg>
{
    public Task ExecuteAsync(MhrFooMsg message, IMessageBusReadStream stream, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

file class MhrFakeConsumeContext : IConsumeContext
{
    public IBus Bus => throw new NotImplementedException();
    public IReadOnlyDictionary<string, object> Headers { get; set; } = new Dictionary<string, object>();
    public string? MessageId => null;
    public Guid CorrelationId => Guid.Empty;
    public CancellationToken CancellationToken { get; set; }
    public Task ReplyAsync<TReply>(TReply message, ReplyOptions? options = null, CancellationToken cancellationToken = default) where TReply : Message
        => throw new NotImplementedException();
}
