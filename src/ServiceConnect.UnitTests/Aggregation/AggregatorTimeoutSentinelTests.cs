using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Aggregation;

// These tests verify that the registry rejects the two invalid flush configurations
// that previously could arise from the old virtual defaults (BatchSize=0, Timeout=InfiniteTimeSpan).
// They act as defence-in-depth alongside the parametric coverage in AggregatorRegistryTests.
public class AggregatorTimeoutSentinelTests
{
    private sealed class SentinelMessage : Message
    {
        public SentinelMessage() : base(Guid.NewGuid()) { }
    }

    private sealed class InfiniteTimeoutAggregator : Aggregator<SentinelMessage>
    {
        public override int BatchSize() => 5;
        public override TimeSpan Timeout() => System.Threading.Timeout.InfiniteTimeSpan;
        public override Task ExecuteAsync(IReadOnlyList<SentinelMessage> messages, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ZeroBatchSizeAggregator : Aggregator<SentinelMessage>
    {
        public override int BatchSize() => 0;
        public override TimeSpan Timeout() => TimeSpan.FromSeconds(1);
        public override Task ExecuteAsync(IReadOnlyList<SentinelMessage> messages, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public void Registry_ThrowsInvalidOperation_WhenTimeoutIsInfiniteTimeSpan()
    {
        var services = new ServiceCollection();
        services.AddTransient<Aggregator<SentinelMessage>, InfiniteTimeoutAggregator>();
        var sp = services.BuildServiceProvider();

        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(SentinelMessage), HandlerType = typeof(InfiniteTimeoutAggregator) },
        };

        Assert.Throws<InvalidOperationException>(() =>
            new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance));
    }

    [Fact]
    public void Registry_ThrowsInvalidOperation_WhenBatchSizeIsZero()
    {
        var services = new ServiceCollection();
        services.AddTransient<Aggregator<SentinelMessage>, ZeroBatchSizeAggregator>();
        var sp = services.BuildServiceProvider();

        var refs = new List<HandlerReference>
        {
            new() { MessageType = typeof(SentinelMessage), HandlerType = typeof(ZeroBatchSizeAggregator) },
        };

        Assert.Throws<InvalidOperationException>(() =>
            new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance));
    }
}
