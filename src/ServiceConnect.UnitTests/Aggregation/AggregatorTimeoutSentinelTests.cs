using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.Aggregation;

public class AggregatorTimeoutSentinelTests
{
    public sealed class TestAggregator : Aggregator<Message>
    {
        public override Task ExecuteAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public void Timeout_DefaultBaseImplementation_ReturnsInfiniteTimeSpan()
    {
        var agg = new TestAggregator();
        Assert.Equal(Timeout.InfiniteTimeSpan, agg.Timeout());
    }
}
