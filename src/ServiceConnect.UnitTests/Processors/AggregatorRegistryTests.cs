using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class AggregatorRegistryTests
{
    [Fact]
    public void TryGet_ReturnsDescriptor_ForRegisteredType()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));
        Assert.Equal(typeof(ArgFoo), descriptor!.MessageType);
        Assert.Equal(typeof(Aggregator<ArgFoo>), descriptor.AggregatorBaseType);
        Assert.Equal(typeof(Aggregator<ArgFoo>).FullName, descriptor.AggregatorName);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForUnregisteredType()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var registry = new AggregatorRegistry(
            new List<HandlerReference>(),
            sp,
            NullLogger<AggregatorRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(ArgFoo), out var descriptor));
        Assert.Null(descriptor);
    }

    [Fact]
    public void Construction_CapturesBatchSize_FromAggregatorInstance()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));
        Assert.Equal(42, descriptor!.BatchSize);
    }

    [Fact]
    public void Construction_CapturesTimeout_FromAggregatorInstance()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));
        Assert.Equal(TimeSpan.FromSeconds(7), descriptor!.Timeout);
    }

    [Fact]
    public void Construction_ThrowsOnDuplicateMessageType()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) },
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgSecondFooAggregator) }
        };

        var services = new ServiceCollection();
        services.AddTransient<Aggregator<ArgFoo>, ArgFooAggregator>();
        var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance));
        Assert.Contains(nameof(ArgFoo), ex.Message);
    }

    [Fact]
    public void Construction_Deduplicates_SameHandlerRegisteredTwice()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) },
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();

        // Same (MessageType, HandlerType) pair twice must not throw.
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);
        Assert.True(registry.TryGet(typeof(ArgFoo), out _));
    }

    [Fact]
    public void Construction_IgnoresNonAggregators()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooMessageHandler) }
        };
        var sp = new ServiceCollection().BuildServiceProvider();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.False(registry.TryGet(typeof(ArgFoo), out _));
    }

    [Fact]
    public void Descriptor_BuildTypedList_ReturnsPopulatedTypedList()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));

        var raw = new List<object> { new ArgFoo(Guid.NewGuid()) { Val = "a" }, new ArgFoo(Guid.NewGuid()) { Val = "b" } };
        var typed = descriptor!.BuildTypedList(raw);

        Assert.IsType<List<ArgFoo>>(typed);
        Assert.Equal(2, typed.Count);
        Assert.Equal("a", ((ArgFoo)typed[0]!).Val);
    }

    [Fact]
    public async Task Descriptor_InvokeExecuteAsync_CallsExecuteAsyncOnAggregator()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp, NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));

        var agg = new ArgFooAggregator();
        var list = new List<ArgFoo> { new(Guid.NewGuid()) { Val = "x" } };
        await descriptor!.InvokeExecuteAsync(agg, list, CancellationToken.None);

        Assert.NotNull(agg.Executed);
        Assert.Single(agg.Executed!);
        Assert.Equal("x", agg.Executed![0].Val);
    }

    private static IServiceProvider BuildServiceProvider<TMsg, TAgg>()
        where TMsg : Message where TAgg : Aggregator<TMsg>
    {
        var services = new ServiceCollection();
        services.AddTransient<Aggregator<TMsg>, TAgg>();
        return services.BuildServiceProvider();
    }
}

file class ArgFoo : Message { public ArgFoo(Guid c) : base(c) { } public string Val { get; set; } = ""; }

file class ArgFooAggregator : Aggregator<ArgFoo>
{
    public IList<ArgFoo>? Executed { get; private set; }
    public override int BatchSize() => 42;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(7);
    public override Task ExecuteAsync(IList<ArgFoo> messages, CancellationToken cancellationToken = default)
    {
        Executed = messages;
        return Task.CompletedTask;
    }
}

file class ArgSecondFooAggregator : Aggregator<ArgFoo>
{
    public override Task ExecuteAsync(IList<ArgFoo> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

file class ArgFooMessageHandler : IMessageHandler<ArgFoo>
{
    public IConsumeContext Context { get; set; } = null!;
    public Task HandleAsync(ArgFoo message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
