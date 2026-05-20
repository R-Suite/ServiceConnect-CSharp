using ServiceConnect.Examples.StressHarness.Patterns.Handlers;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Patterns.Handlers;

public class StreamObservationsTests
{
    [Fact]
    public async Task TryRemoveCompleted_DropsCompletedAwaiterEntry()
    {
        var obs = new StreamObservations();
        var done = Guid.NewGuid();
        var inflight = Guid.NewGuid();

        _ = obs.AwaitAsync(done, CancellationToken.None);
        _ = obs.AwaitAsync(inflight, CancellationToken.None);

        obs.Record(new StreamObservation(done, "alpha", 4, "deadbeef"));

        await Task.Yield();

        obs.TryRemoveCompleted([done]);

        var doneAfter = obs.AwaitAsync(done, CancellationToken.None);
        var inflightAfter = obs.AwaitAsync(inflight, CancellationToken.None);

        Assert.False(doneAfter.IsCompleted, "Re-awaiting a reclaimed flow id should yield a fresh pending TCS.");
        Assert.False(inflightAfter.IsCompleted);
    }
}
