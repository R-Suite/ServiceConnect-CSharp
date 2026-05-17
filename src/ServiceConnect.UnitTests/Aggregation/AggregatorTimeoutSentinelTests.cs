using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.Interfaces;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Aggregation;

// These tests pin the diagnostic message the registry emits when both flush paths are
// misconfigured: they verify not only that registration fails but that the exception
// identifies the offending handler type and the bad configuration value by name.
// AggregatorRegistryTests covers the same failure modes parametrically; these sentinel
// tests add message-content assertions so the diagnostic contract is explicitly tested.
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

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance));
        Assert.Contains(typeof(InfiniteTimeoutAggregator).FullName!, ex.Message);
        Assert.Contains("Timeout=", ex.Message);
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

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new AggregatorRegistry(refs, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AggregatorRegistry>.Instance));
        Assert.Contains(typeof(ZeroBatchSizeAggregator).FullName!, ex.Message);
        Assert.Contains("BatchSize=0", ex.Message);
    }
}
