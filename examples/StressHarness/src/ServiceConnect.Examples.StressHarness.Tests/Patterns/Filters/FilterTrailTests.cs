using ServiceConnect.Examples.StressHarness.Patterns.Filters;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Patterns.Filters;

public class FilterTrailTests
{
    [Fact]
    public void TryRemoveCompleted_RemovesNamedFlows_LeavesOthersIntact()
    {
        var trail = new FilterTrail();
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        trail.Record(keep, "filter");
        trail.Record(drop, "filter");

        trail.TryRemoveCompleted([drop]);

        Assert.Equal(["filter"], trail.Snapshot(keep));
        Assert.Empty(trail.Snapshot(drop));
    }
}
