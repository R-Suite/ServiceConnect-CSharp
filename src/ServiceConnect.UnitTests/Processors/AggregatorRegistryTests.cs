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
        var registry = new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));
        Assert.Equal(typeof(ArgFoo), descriptor!.MessageType);
        Assert.Equal(typeof(Aggregator<ArgFoo>), descriptor.AggregatorBaseType);
        // AggregatorName is the concrete handler type's FullName, not the closed generic base.
        Assert.Equal(typeof(ArgFooAggregator).FullName, descriptor.AggregatorName);
    }

    [Fact]
    public void AggregatorName_DoesNotContainAssemblyQualifiedNoise()
    {
        // Closed generic base types (e.g. Aggregator<ArgFoo>) embed the assembly-qualified name
        // of their type argument in FullName, including Version=, Culture=, PublicKeyToken=.
        // Those components rotate on assembly version bumps and would orphan persisted state.
        // The name must be derived from the concrete handler type instead.
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));

        Assert.DoesNotContain("Version=", descriptor!.AggregatorName);
        Assert.DoesNotContain("PublicKeyToken=", descriptor.AggregatorName);
        Assert.DoesNotContain("Culture=", descriptor.AggregatorName);

        // The name is the concrete handler's FullName, stable across hypothetical version bumps.
        Assert.Equal(typeof(ArgFooAggregator).FullName, descriptor.AggregatorName);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForUnregisteredType()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var registry = new AggregatorRegistry(
            [],
            sp.GetRequiredService<IServiceScopeFactory>(),
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
        var registry = new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);

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
        var registry = new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));
        Assert.Equal(TimeSpan.FromSeconds(7), descriptor!.Timeout);
    }

    [Theory]
    [InlineData(5, 0, 0)]              // BatchSize set, Timeout=Zero — timer never scheduled, tail strands
    [InlineData(0, 1, 0)]              // BatchSize=0, positive Timeout — timer-only, no batch guard
    [InlineData(0, 0, 0)]              // both zero — no flush trigger at all
    [InlineData(-1, 1, 0)]             // negative BatchSize, positive Timeout
    [InlineData(5, -1, 0)]             // positive BatchSize, negative Timeout (Timeout.InfiniteTimeSpan-like)
    public void Construction_ThrowsInvalidOperation_WhenConfigurationCannotFlush(
        int batchSize, int timeoutSeconds, int timeoutMilliseconds)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds) + TimeSpan.FromMilliseconds(timeoutMilliseconds);
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgBar), HandlerType = typeof(ArgBarAggregator) }
        };

        var services = new ServiceCollection();
        services.AddTransient<Aggregator<ArgBar>>(_ => new ArgBarAggregator(batchSize, timeout));
        var sp = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() =>
            new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance));
    }

    [Fact]
    public void Construction_DoesNotThrow_WhenBothBatchSizeAndTimeoutArePositive()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgBar), HandlerType = typeof(ArgBarAggregator) }
        };

        var services = new ServiceCollection();
        services.AddTransient<Aggregator<ArgBar>>(_ => new ArgBarAggregator(5, TimeSpan.FromSeconds(1)));
        var sp = services.BuildServiceProvider();

        // Must not throw.
        var registry = new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
        Assert.True(registry.TryGet(typeof(ArgBar), out _));
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
            new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance));
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
        var registry = new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);
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
        var registry = new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);

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
        var registry = new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);

        Assert.True(registry.TryGet(typeof(ArgFoo), out var descriptor));

        var raw = new List<object> { new ArgFoo(Guid.NewGuid()) { Val = "a" }, new ArgFoo(Guid.NewGuid()) { Val = "b" } };
        var typedObj = descriptor!.BuildTypedList(raw);

        // BuildTypedList returns IReadOnlyList<ArgFoo> boxed as object; the underlying runtime
        // type is List<ArgFoo> (which implements IReadOnlyList<ArgFoo>).
        Assert.IsType<List<ArgFoo>>(typedObj);
        var typed = Assert.IsAssignableFrom<IReadOnlyList<ArgFoo>>(typedObj);
        Assert.Equal(2, typed.Count);
        Assert.Equal("a", typed[0].Val);
    }

    [Fact]
    public async Task Descriptor_InvokeExecuteAsync_CallsExecuteAsyncOnAggregator()
    {
        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(ArgFoo), HandlerType = typeof(ArgFooAggregator) }
        };
        var sp = BuildServiceProvider<ArgFoo, ArgFooAggregator>();
        var registry = new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance);

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

file class ArgFoo(Guid c) : Message(c) { public string Val { get; set; } = ""; }

file class ArgFooAggregator : Aggregator<ArgFoo>
{
    public IReadOnlyList<ArgFoo>? Executed { get; private set; }
    public override int BatchSize() => 42;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(7);
    public override Task ExecuteAsync(IReadOnlyList<ArgFoo> messages, CancellationToken cancellationToken = default)
    {
        Executed = messages;
        return Task.CompletedTask;
    }
}

file class ArgSecondFooAggregator : Aggregator<ArgFoo>
{
    public override Task ExecuteAsync(IReadOnlyList<ArgFoo> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

file class ArgFooMessageHandler : IMessageHandler<ArgFoo>
{
    public Task HandleAsync(ArgFoo message, IConsumeContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

file class ArgBar(Guid c) : Message(c);

file class ArgBarAggregator(int batchSize, TimeSpan timeout) : Aggregator<ArgBar>
{
    public override int BatchSize() => batchSize;
    public override TimeSpan Timeout() => timeout;
    public override Task ExecuteAsync(IReadOnlyList<ArgBar> messages, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
