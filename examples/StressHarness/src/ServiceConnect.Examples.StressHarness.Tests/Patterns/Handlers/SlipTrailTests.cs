using ServiceConnect.Examples.StressHarness.Patterns.Handlers;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Patterns.Handlers;

public class SlipTrailTests
{
    [Fact]
    public void TryRemoveCompleted_RemovesNamedFlows_LeavesOthersIntact()
    {
        var trail = new SlipTrail();
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        trail.Record(keep, "alpha");
        trail.Record(drop, "alpha");

        trail.TryRemoveCompleted([drop]);

        Assert.Equal(["alpha"], trail.Snapshot(keep));
        Assert.Empty(trail.Snapshot(drop));
    }
}
