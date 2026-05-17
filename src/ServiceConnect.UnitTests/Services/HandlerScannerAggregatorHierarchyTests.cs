using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public sealed class HandlerScannerAggregatorHierarchyTests
{
    public sealed class TwoLevelMessage : Message
    {
        public TwoLevelMessage() : base(Guid.NewGuid()) { }
    }

    public abstract class AggregatorBase<T> : Aggregator<T> where T : Message
    {
        public override int BatchSize() => 10;
        public override TimeSpan Timeout() => TimeSpan.FromSeconds(1);
    }

    public sealed class TwoLevelAggregator : AggregatorBase<TwoLevelMessage>
    {
        public override Task ExecuteAsync(IReadOnlyList<TwoLevelMessage> messages, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public void ScanForHandlers_DiscoversTwoLevelAggregatorSubclass()
    {
        var refs = HandlerScanner.ScanForHandlers([typeof(TwoLevelAggregator).Assembly]);

        Assert.Contains(refs, r =>
            r.HandlerType == typeof(TwoLevelAggregator) &&
            r.MessageType == typeof(TwoLevelMessage) &&
            r.InterfaceKind == HandlerInterfaceKind.Aggregator);
    }
}
