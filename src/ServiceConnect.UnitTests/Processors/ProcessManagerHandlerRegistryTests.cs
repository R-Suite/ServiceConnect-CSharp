using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class ProcessManagerHandlerRegistryTests
{
    [Fact]
    public void TryGet_ReturnsDescriptor_ForRegisteredMessageType()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(FooMessage), HandlerType = typeof(FooHandler) }
        };

        var registry = new ProcessManagerHandlerRegistry(refs, NullLogger<ProcessManagerHandlerRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));
        Assert.Equal(typeof(FooMessage), descriptor!.MessageType);
        Assert.Equal(typeof(FooData), descriptor.DataType);
        Assert.Equal(typeof(IProcessHandler<FooData, FooMessage>), descriptor.ProcessHandlerInterfaceType);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForUnknownMessageType()
    {
        var registry = new ProcessManagerHandlerRegistry(
            [],
            NullLogger<ProcessManagerHandlerRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(FooMessage), out var descriptor));
        Assert.Null(descriptor);
    }

    [Fact]
    public void Construction_IgnoresNonProcessHandlers()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(FooMessage), HandlerType = typeof(PlainFooHandler) }
        };

        var registry = new ProcessManagerHandlerRegistry(refs, NullLogger<ProcessManagerHandlerRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(FooMessage), out _));
    }

    [Fact]
    public void Construction_ThrowsOnDuplicateMessageMapping()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(FooMessage), HandlerType = typeof(FooHandler) },
            new() { MessageType = typeof(FooMessage), HandlerType = typeof(SecondFooHandler) }
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ProcessManagerHandlerRegistry(refs, NullLogger<ProcessManagerHandlerRegistry>.Instance));

        Assert.Contains(nameof(FooMessage), ex.Message);
    }

    [Fact]
    public void Descriptor_CreateData_CreatesFreshInstance()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var a = descriptor!.CreateData();
        var b = descriptor.CreateData();

        Assert.IsType<FooData>(a);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void Descriptor_SetCorrelationId_WritesProperty()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var data = descriptor!.CreateData();
        var correlationId = Guid.NewGuid();
        descriptor.SetCorrelationId(data, correlationId);

        Assert.Equal(correlationId, data.CorrelationId);
    }

    [Fact]
    public void Descriptor_SetHandlerContext_WritesHandlerContext()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var handler = new FooHandler();
        var ctx = new FakeConsumeContext();
        descriptor!.SetHandlerContext(handler, ctx);

        Assert.Same(ctx, handler.Context);
    }

    [Fact]
    public void Descriptor_ConfigureMapper_InvokesHandlerConfigureMapper()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var handler = new FooHandler();
        var mapper = new DefaultProcessManagerPropertyMapperStub();
        descriptor!.ConfigureMapper(handler, mapper);

        // FooHandler uses the default interface method which adds one mapping (CorrelationId → CorrelationId).
        Assert.Single(mapper.Mappings);
    }

    [Fact]
    public async Task Descriptor_InvokeHandleAsync_PassesMessageAndData()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var handler = new FooHandler();
        var msg = new FooMessage(Guid.NewGuid());
        var data = new FooData { CorrelationId = Guid.NewGuid() };

        await descriptor!.InvokeHandleAsync(handler, msg, data, CancellationToken.None);

        Assert.Same(msg, handler.ReceivedMessage);
        Assert.Same(data, handler.ReceivedData);
    }

    [Fact]
    public void Descriptor_ExtractData_ReadsDataProperty()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var data = new FooData();
        var persistence = new FooPersistenceData { Data = data };

        var result = descriptor.ExtractData(persistence);

        Assert.Same(data, result);
    }

    [Fact]
    public async Task Descriptor_FindData_ReturnsNullFromFinder_WhenNoPersistence()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var finder = new Mock<IProcessManagerFinder>();
        finder.Setup(f => f.FindDataAsync<FooData>(
                It.IsAny<IProcessManagerPropertyMapper>(),
                It.IsAny<Message>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<FooData>?)null);

        var mapper = new DefaultProcessManagerPropertyMapperStub();
        var result = await descriptor!.FindData(finder.Object, mapper, new FooMessage(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result);
        finder.Verify(f => f.FindDataAsync<FooData>(
            It.IsAny<IProcessManagerPropertyMapper>(),
            It.IsAny<Message>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Descriptor_FindData_ReturnsPersistence_WhenFinderReturnsData()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var persistence = new FooPersistenceData { Data = new FooData { CorrelationId = Guid.NewGuid() } };
        var finder = new Mock<IProcessManagerFinder>();
        finder.Setup(f => f.FindDataAsync<FooData>(
                It.IsAny<IProcessManagerPropertyMapper>(),
                It.IsAny<Message>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(persistence);

        var mapper = new DefaultProcessManagerPropertyMapperStub();
        var result = await descriptor!.FindData(finder.Object, mapper, new FooMessage(Guid.NewGuid()), CancellationToken.None);

        Assert.Same(persistence, result);
    }

    [Fact]
    public async Task Descriptor_UpdateData_CallsFinderWithPersistenceObject()
    {
        var registry = BuildFooRegistry();
        Assert.True(registry.TryGet(typeof(FooMessage), out var descriptor));

        var persistence = new FooPersistenceData { Data = new FooData() };
        var finder = new Mock<IProcessManagerFinder>();
        finder.Setup(f => f.UpdateDataAsync<FooData>(
                It.IsAny<IPersistenceData<FooData>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await descriptor!.UpdateData(finder.Object, persistence, CancellationToken.None);

        finder.Verify(f => f.UpdateDataAsync<FooData>(persistence, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static ProcessManagerHandlerRegistry BuildFooRegistry()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(FooMessage), HandlerType = typeof(FooHandler) }
        };
        return new ProcessManagerHandlerRegistry(refs, NullLogger<ProcessManagerHandlerRegistry>.Instance);
    }
}

file class FooMessage : Message
{
    public FooMessage() : base(Guid.Empty) { }
    public FooMessage(Guid correlationId) : base(correlationId) { }
}

file class FooData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
}

file class FooPersistenceData : IPersistenceData<FooData>
{
    public FooData Data { get; set; } = new();
}

file class FooHandler : IProcessHandler<FooData, FooMessage>
{
    public IConsumeContext Context { get; set; } = null!;
    public FooMessage? ReceivedMessage { get; private set; }
    public FooData? ReceivedData { get; private set; }

    public Task HandleAsync(FooMessage message, FooData data, CancellationToken cancellationToken = default)
    {
        ReceivedMessage = message;
        ReceivedData = data;
        return Task.CompletedTask;
    }
}

file class SecondFooHandler : IProcessHandler<FooData, FooMessage>
{
    public IConsumeContext Context { get; set; } = null!;
    public Task HandleAsync(FooMessage message, FooData data, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

file class PlainFooHandler : IMessageHandler<FooMessage>
{
    public IConsumeContext Context { get; set; } = null!;
    public Task HandleAsync(FooMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

file class FakeConsumeContext : IConsumeContext
{
    public IBus Bus => throw new NotImplementedException();
    public IReadOnlyDictionary<string, object> Headers { get; set; } = new Dictionary<string, object>();
    public string? MessageId => null;
    public Guid CorrelationId => Guid.Empty;
    public CancellationToken CancellationToken { get; set; }
    public Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where TReply : Message
        => throw new NotImplementedException();
}

file class DefaultProcessManagerPropertyMapperStub : IProcessManagerPropertyMapper
{
    private readonly List<ProcessManagerToMessageMap> _mappings = [];
    public IReadOnlyList<ProcessManagerToMessageMap> Mappings => _mappings;

    public void ConfigureMapping<TProcessManagerData, TMessage>(
        System.Linq.Expressions.Expression<Func<TProcessManagerData, object>> processManagerProperty,
        System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
        where TProcessManagerData : IProcessManagerData
    {
        _mappings.Add(new ProcessManagerToMessageMap { MessageType = typeof(TMessage), MessageProp = _ => null! });
    }
}
