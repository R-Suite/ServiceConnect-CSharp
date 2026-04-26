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
            new List<HandlerReference>(),
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
    public void Descriptor_SetStream_WritesStreamProperty()
    {
        var registry = BuildRegistry();
        Assert.True(registry.TryGet(typeof(ShrFoo), out var descriptor));

        var handler = new ShrFooStreamHandler();
        var stream = new MessageBusReadStream("seq");

        descriptor!.SetStream(handler, stream);

        Assert.Same(stream, handler.Stream);
    }

    [Fact]
    public async Task Descriptor_InvokeExecuteAsync_CallsExecuteAsyncOnHandler()
    {
        var registry = BuildRegistry();
        Assert.True(registry.TryGet(typeof(ShrFoo), out var descriptor));

        var handler = new ShrFooStreamHandler();
        var msg = new ShrFoo(Guid.NewGuid());

        await descriptor!.InvokeExecuteAsync(handler, msg, CancellationToken.None);

        Assert.Same(msg, handler.Executed);
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

file class ShrFoo : Message { public ShrFoo(Guid c) : base(c) { } }

file class ShrFooStreamHandler : IStreamHandler<ShrFoo>
{
    public IMessageBusReadStream Stream { get; set; } = null!;
    public ShrFoo? Executed { get; private set; }
    public Task ExecuteAsync(ShrFoo stream, CancellationToken cancellationToken = default)
    {
        Executed = stream;
        return Task.CompletedTask;
    }
}

file class ShrSecondFooStreamHandler : IStreamHandler<ShrFoo>
{
    public IMessageBusReadStream Stream { get; set; } = null!;
    public Task ExecuteAsync(ShrFoo stream, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

file class ShrFooMessageHandler : IMessageHandler<ShrFoo>
{
    public IConsumeContext Context { get; set; } = null!;
    public Task HandleAsync(ShrFoo message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
