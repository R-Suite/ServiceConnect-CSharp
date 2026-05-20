using ServiceConnect.Examples.StressHarness.Patterns.Middleware;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Patterns.Middleware;

public class MiddlewareTrailTests
{
    [Fact]
    public void TryRemoveCompleted_RemovesNamedFlows_LeavesOthersIntact()
    {
        var trail = new MiddlewareTrail();
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        trail.Record(keep, "mid-enter");
        trail.Record(drop, "mid-enter");

        trail.TryRemoveCompleted([drop]);

        Assert.Equal(["mid-enter"], trail.Snapshot(keep));
        Assert.Empty(trail.Snapshot(drop));
    }
}
