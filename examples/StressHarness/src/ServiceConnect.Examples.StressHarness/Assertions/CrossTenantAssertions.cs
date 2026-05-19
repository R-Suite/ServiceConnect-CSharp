using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Assertions;

public readonly record struct AssertionOutcome(bool Ok, string Failure)
{
    public static AssertionOutcome Pass() => new(true, string.Empty);
    public static AssertionOutcome Fail(string reason) => new(false, reason);
}

public static class CrossTenantAssertions
{
    public static AssertionOutcome Check(IConsumeContext context, BusIdentity expectedReceiver, string actualBusTag)
    {
        if (!context.Headers.TryGetValue(StressHeaders.OriginBus, out var originRaw) || originRaw is null)
        {
            return AssertionOutcome.Fail($"flow missing '{StressHeaders.OriginBus}' header");
        }

        var expectedTag = expectedReceiver.ToHeaderValue();
        if (!string.Equals(actualBusTag, expectedTag, StringComparison.Ordinal))
        {
            return AssertionOutcome.Fail($"expected receiver '{expectedTag}' but handler ran on '{actualBusTag}'");
        }

        return AssertionOutcome.Pass();
    }
}
