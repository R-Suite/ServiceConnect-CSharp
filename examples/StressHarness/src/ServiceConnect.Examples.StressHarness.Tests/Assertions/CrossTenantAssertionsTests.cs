using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Patterns;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Assertions;

public class CrossTenantAssertionsTests
{
    [Fact]
    public void HandlerInvokedOnExpectedBus_NoFailure()
    {
        var headers = new Dictionary<string, object>
        {
            [StressHeaders.OriginBus] = "alpha",
            [StressHeaders.FlowId] = Guid.NewGuid().ToString("N"),
            [StressHeaders.Pattern] = "p2p",
        };

        var result = CrossTenantAssertions.Check(
            headers,
            expectedReceiver: BusIdentity.Beta,
            actualBusTag: "beta");

        Assert.True(result.Ok);
    }

    [Fact]
    public void HandlerInvokedOnWrongBus_RecordsFailure()
    {
        var headers = new Dictionary<string, object>
        {
            [StressHeaders.OriginBus] = "alpha",
            [StressHeaders.FlowId] = Guid.NewGuid().ToString("N"),
            [StressHeaders.Pattern] = "p2p",
        };

        var result = CrossTenantAssertions.Check(
            headers,
            expectedReceiver: BusIdentity.Beta,
            actualBusTag: "alpha");

        Assert.False(result.Ok);
        Assert.Contains("expected receiver 'beta' but handler ran on 'alpha'", result.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingOriginBusHeader_RecordsFailure()
    {
        var headers = new Dictionary<string, object>();

        var result = CrossTenantAssertions.Check(
            headers,
            expectedReceiver: BusIdentity.Beta,
            actualBusTag: "beta");

        Assert.False(result.Ok);
        Assert.Contains("missing", result.Failure, StringComparison.OrdinalIgnoreCase);
    }
}
