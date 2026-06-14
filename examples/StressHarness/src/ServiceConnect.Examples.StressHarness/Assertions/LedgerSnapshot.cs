namespace ServiceConnect.Examples.StressHarness.Assertions;

public sealed record LedgerSnapshot(
    IReadOnlyList<PublishRecord> Publishes,
    IReadOnlyList<ConsumeRecord> Consumes);
