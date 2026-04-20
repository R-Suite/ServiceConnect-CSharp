using ServiceConnect.Examples.Aggregator.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Aggregator.Consumer;

public sealed class TelemetrySliceAggregator : Aggregator<TelemetrySlice>
{
    public override int BatchSize() => 2;

    public override TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    public override void Execute(IList<TelemetrySlice> messages)
    {
        var total = messages.Sum(message => message.Value);
        ConsoleStatus.Success("aggregator-consumer", $"combined total {total} from {messages.Count} slices");
    }
}
