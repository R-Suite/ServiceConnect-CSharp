namespace ServiceConnect.Examples.StressHarness.Assertions;

public static class MemoryAssertions
{
    public static long SnapshotTotalMemory() => GC.GetTotalMemory(forceFullCollection: true);

    public static AssertionOutcome CheckDelta(long baselineBytes, long finalBytes, long budgetBytes)
    {
        var delta = finalBytes - baselineBytes;
        if (delta > budgetBytes)
        {
            return AssertionOutcome.Fail(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"memory delta {delta:N0} bytes exceeded budget {budgetBytes:N0} bytes (baseline {baselineBytes:N0}, final {finalBytes:N0})"));
        }
        return AssertionOutcome.Pass();
    }
}
