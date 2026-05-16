using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class AggregatorRegistryCaptiveDepsTests
{
    public sealed class TestMessage : Message
    {
        public TestMessage() : base(Guid.NewGuid()) { }
    }

    public sealed class DisposableScopedDep : IDisposable
    {
        public static int DisposeCount;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    public sealed class AggregatorWithScopedDep(DisposableScopedDep dep) : Aggregator<TestMessage>
    {
        // Holding the dep keeps the DI graph honest (registered, captured, disposed via scope).
        // The class only needs to compile and instantiate; the test exercises the scope's disposal.
        public DisposableScopedDep Dep { get; } = dep;

        public override int BatchSize() => 5;
        public override TimeSpan Timeout() => TimeSpan.FromSeconds(1);
        public override Task ExecuteAsync(IReadOnlyList<TestMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void Constructor_DisposesScopedDependencyResolvedAtConstructionTime()
    {
        DisposableScopedDep.DisposeCount = 0;

        var services = new ServiceCollection();
        services.AddScoped<DisposableScopedDep>();
        services.AddTransient<Aggregator<TestMessage>, AggregatorWithScopedDep>();
        var rootProvider = services.BuildServiceProvider(validateScopes: true);

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(AggregatorWithScopedDep), MessageType = typeof(TestMessage) },
        };

        // Construct the registry — it must NOT capture the root provider in a way that
        // keeps the scoped dependency alive past the constructor.
        var registry = new AggregatorRegistry(
            handlerRefs,
            rootProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AggregatorRegistry>.Instance);

        Assert.Equal(1, DisposableScopedDep.DisposeCount);
        Assert.True(registry.TryGet(typeof(TestMessage), out var descriptor));
        Assert.Equal(5, descriptor!.BatchSize);
    }

    public sealed class AsyncOnlyDisposableAggregator : Aggregator<TestMessage>, IAsyncDisposable
    {
        public AsyncOnlyDisposableAggregator() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public override int BatchSize() => 1;
        public override TimeSpan Timeout() => TimeSpan.FromSeconds(1);
        public override Task ExecuteAsync(IReadOnlyList<TestMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void Constructor_RejectsIAsyncDisposableOnlyAggregator_WithClearMessage()
    {
        var services = new ServiceCollection();
        services.AddTransient<Aggregator<TestMessage>, AsyncOnlyDisposableAggregator>();
        var rootProvider = services.BuildServiceProvider(validateScopes: true);

        var handlerRefs = new List<HandlerReference>
        {
            new() { HandlerType = typeof(AsyncOnlyDisposableAggregator), MessageType = typeof(TestMessage) },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new AggregatorRegistry(
            handlerRefs,
            rootProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AggregatorRegistry>.Instance));
        Assert.Contains("IAsyncDisposable", ex.Message);
        Assert.Contains("IDisposable", ex.Message);
    }
}
