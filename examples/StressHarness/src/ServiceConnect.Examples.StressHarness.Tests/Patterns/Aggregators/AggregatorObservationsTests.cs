using ServiceConnect.Examples.StressHarness.Patterns.Aggregators;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Patterns.Aggregators;

public class AggregatorObservationsTests
{
    [Fact]
    public async Task TryRemoveCompleted_DropsCompletedAwaiterEntry()
    {
        var obs = new AggregatorObservations();
        var done = Guid.NewGuid();
        var inflight = Guid.NewGuid();

        _ = obs.AwaitBatchAsync(done, CancellationToken.None);
        _ = obs.AwaitBatchAsync(inflight, CancellationToken.None);

        obs.Record(new AggregatorBatchObservation(done, "alpha", 4));

        await Task.Yield();

        obs.TryRemoveCompleted([done]);

        var doneAfter = obs.AwaitBatchAsync(done, CancellationToken.None);
        var inflightAfter = obs.AwaitBatchAsync(inflight, CancellationToken.None);

        Assert.False(doneAfter.IsCompleted, "Re-awaiting a reclaimed flow id should yield a fresh pending TCS.");
        Assert.False(inflightAfter.IsCompleted);
    }
}
