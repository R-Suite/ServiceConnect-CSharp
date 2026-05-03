using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class StreamHandlerRegistryTests
{
    [Fact]
    public void TryGet_ReturnsDescriptor_ForRegisteredType()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooStreamHandler) }
        };
        var registry = new StreamHandlerRegistry(refs, NullLogger<StreamHandlerRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ShrFoo), out var descriptor));
        Assert.Equal(typeof(ShrFoo), descriptor!.MessageType);
        Assert.Equal(typeof(IStreamHandler<ShrFoo>), descriptor.HandlerInterfaceType);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForUnregisteredType()
    {
        var registry = new StreamHandlerRegistry(
            [],
            NullLogger<StreamHandlerRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(ShrFoo), out var descriptor));
        Assert.Null(descriptor);
    }

    [Fact]
    public void Construction_IgnoresNonStreamHandlers()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooMessageHandler) }
        };
        var registry = new StreamHandlerRegistry(refs, NullLogger<StreamHandlerRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(ShrFoo), out _));
    }

    [Fact]
    public void Construction_ThrowsOnDuplicateMessageType_WithDistinctHandlers()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooStreamHandler) },
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrSecondFooStreamHandler) }
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new StreamHandlerRegistry(refs, NullLogger<StreamHandlerRegistry>.Instance));

        Assert.Contains(nameof(ShrFoo), ex.Message);
    }

    [Fact]
    public void Construction_Deduplicates_SameHandlerRegisteredTwice()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooStreamHandler) },
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooStreamHandler) }
        };

        // Same (MessageType, HandlerType) pair twice must not throw.
        var registry = new StreamHandlerRegistry(refs, NullLogger<StreamHandlerRegistry>.Instance);
        Assert.True(registry.TryGet(typeof(ShrFoo), out _));
    }

    [Fact]
    public async Task Descriptor_InvokeExecuteAsync_PassesMessageAndStream()
    {
        var registry = BuildRegistry();
        Assert.True(registry.TryGet(typeof(ShrFoo), out var descriptor));

        var handler = new ShrFooStreamHandler();
        var msg = new ShrFoo(Guid.NewGuid());
        var stream = new MessageBusReadStream("seq");

        await descriptor!.InvokeExecuteAsync(handler, msg, stream, CancellationToken.None);

        Assert.Same(msg, handler.Executed);
        Assert.Same(stream, handler.ReceivedStream);
    }

    private static StreamHandlerRegistry BuildRegistry()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ShrFoo), HandlerType = typeof(ShrFooStreamHandler) }
        };
        return new StreamHandlerRegistry(refs, NullLogger<StreamHandlerRegistry>.Instance);
    }
}

file class ShrFoo(Guid c) : Message(c) {
}

file class ShrFooStreamHandler : IStreamHandler<ShrFoo>
{
    public ShrFoo? Executed { get; private set; }
    public IMessageBusReadStream? ReceivedStream { get; private set; }
    public Task ExecuteAsync(ShrFoo message, IMessageBusReadStream stream, CancellationToken cancellationToken = default)
    {
        Executed = message;
        ReceivedStream = stream;
        return Task.CompletedTask;
    }
}

file class ShrSecondFooStreamHandler : IStreamHandler<ShrFoo>
{
    public Task ExecuteAsync(ShrFoo message, IMessageBusReadStream stream, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

file class ShrFooMessageHandler : IMessageHandler<ShrFoo>
{
    public Task HandleAsync(ShrFoo message, IConsumeContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
