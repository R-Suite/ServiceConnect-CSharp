using ServiceConnect.Examples.StressHarness.Patterns;

namespace ServiceConnect.Examples.StressHarness.Assertions;

public readonly record struct AssertionOutcome(bool Ok, string Failure)
{
    public static AssertionOutcome Pass() => new(true, string.Empty);
    public static AssertionOutcome Fail(string reason) => new(false, reason);
}

public static class CrossTenantAssertions
{
    /// <summary>
    /// Verifies that the handler captured by <paramref name="headers"/> ran on the bus
    /// identified by <paramref name="actualBusTag"/>, matching the driver's
    /// <paramref name="expectedReceiver"/>. Operates on a header snapshot — taken by
    /// <see cref="PerHandlerSignal.Signal"/> while the consume context was still active —
    /// rather than a live <c>IConsumeContext</c>, because the framework's pooled context
    /// is invalidated the moment the handler returns and the driver's continuation routinely
    /// fires after that point.
    /// </summary>
    public static AssertionOutcome Check(IReadOnlyDictionary<string, object> headers, BusIdentity expectedReceiver, string actualBusTag)
    {
        if (!headers.ContainsKey(StressHeaders.OriginBus))
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
