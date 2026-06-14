using ServiceConnect.Examples.StressHarness.Patterns.Handlers;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Patterns.Handlers;

public class SagaObservationsTests
{
    [Fact]
    public void TryRemoveCompleted_RemovesNamedFlows_LeavesOthersIntact()
    {
        var obs = new SagaObservations();
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        obs.Record(keep, 1);
        obs.Record(drop, 1);

        obs.TryRemoveCompleted([drop]);

        Assert.Equal([1], obs.Snapshot(keep));
        Assert.Empty(obs.Snapshot(drop));
    }
}
