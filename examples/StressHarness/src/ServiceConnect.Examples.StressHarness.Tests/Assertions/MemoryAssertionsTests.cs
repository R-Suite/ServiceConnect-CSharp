using ServiceConnect.Examples.StressHarness.Assertions;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Assertions;

public class MemoryAssertionsTests
{
    [Fact]
    public void DeltaUnderBudget_Passes()
    {
        var snapshot = MemoryAssertions.CheckDelta(baselineBytes: 100_000_000, finalBytes: 105_000_000, budgetBytes: 50_000_000);
        Assert.True(snapshot.Ok);
    }

    [Fact]
    public void DeltaOverBudget_Fails()
    {
        var snapshot = MemoryAssertions.CheckDelta(baselineBytes: 100_000_000, finalBytes: 200_000_000, budgetBytes: 50_000_000);
        Assert.False(snapshot.Ok);
        Assert.Contains("exceeded", snapshot.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NegativeDelta_AlwaysPasses()
    {
        var snapshot = MemoryAssertions.CheckDelta(baselineBytes: 200_000_000, finalBytes: 100_000_000, budgetBytes: 1);
        Assert.True(snapshot.Ok);
    }
}
