using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;
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
            new List<HandlerReference>(),
            NullLogger<MessageHandlerRegistry>.Instance);

        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out var descriptor));
        Assert.Equal(typeof(IMessageHandler<MhrFooMsg>), descriptor!.HandlerInterfaceType);
    }

    [Fact]
    public void TryGetOrBuild_ReturnsFalse_ForMessageBaseType()
    {
        var registry = new MessageHandlerRegistry(
            new List<HandlerReference>(),
            NullLogger<MessageHandlerRegistry>.Instance);

        Assert.False(registry.TryGetOrBuild(typeof(Message), out var descriptor));
        Assert.Null(descriptor);
    }

    [Fact]
    public void TryGetOrBuild_ReturnsFalse_ForObject()
    {
        var registry = new MessageHandlerRegistry(
            new List<HandlerReference>(),
            NullLogger<MessageHandlerRegistry>.Instance);

        Assert.False(registry.TryGetOrBuild(typeof(object), out _));
    }

    [Fact]
    public void Descriptor_SetContext_WritesContextProperty()
    {
        var registry = BuildRegistry();
        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out var descriptor));

        var handler = new MhrFooHandler();
        var ctx = new MhrFakeConsumeContext();
        descriptor!.SetContext(handler, ctx);

        Assert.Same(ctx, handler.Context);
    }

    [Fact]
    public async Task Descriptor_InvokeHandleAsync_CallsHandleAsyncOnHandler()
    {
        var registry = BuildRegistry();
        Assert.True(registry.TryGetOrBuild(typeof(MhrFooMsg), out var descriptor));

        var handler = new MhrFooHandler();
        var msg = new MhrFooMsg(Guid.NewGuid());

        await descriptor!.InvokeHandleAsync(handler, msg);

        Assert.Same(msg, handler.Received);
    }

    [Fact]
    public void TryGetOrBuild_CachesNegativeResults()
    {
        var registry = new MessageHandlerRegistry(
            new List<HandlerReference>(),
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

file class MhrFooMsg : Message { public MhrFooMsg(Guid c) : base(c) { } }
file class MhrBarData : IProcessManagerData { public Guid CorrelationId { get; set; } }

file class MhrFooHandler : IMessageHandler<MhrFooMsg>
{
    public IConsumeContext? Context { get; set; }
    public MhrFooMsg? Received { get; private set; }
    public Task HandleAsync(MhrFooMsg message) { Received = message; return Task.CompletedTask; }
}

file class MhrSecondFooHandler : IMessageHandler<MhrFooMsg>
{
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(MhrFooMsg message) => Task.CompletedTask;
}

file class MhrProcessHandler : IProcessHandler<MhrBarData, MhrFooMsg>
{
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(MhrFooMsg message, MhrBarData data) => Task.CompletedTask;
}

file class MhrStreamHandler : IStreamHandler<MhrFooMsg>
{
    public IMessageBusReadStream Stream { get; set; } = null!;
    public void Execute(MhrFooMsg stream) { }
}

file class MhrFakeConsumeContext : IConsumeContext
{
    public IBus Bus => throw new NotImplementedException();
    public IReadOnlyDictionary<string, object> Headers { get; set; } = new Dictionary<string, object>();
    public string? MessageId => null;
    public Guid CorrelationId => Guid.Empty;
    public CancellationToken CancellationToken { get; set; }
    public Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where TReply : Message
        => throw new NotImplementedException();
}
