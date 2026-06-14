using ServiceConnect.Examples.StressHarness.Chaos;

namespace ServiceConnect.Examples.StressHarness.Assertions;

public sealed record MessageLedgerAnalysis(
    int TotalPublishes,
    int AckedPublishes,
    int FailedPublishes,
    int TotalConsumes,
    int AckedAndConsumed,
    int AckedButLost,
    int FailedThenConsumed,
    int FailedAndLost,
    int PerMessageRedeliveries,
    IReadOnlyDictionary<ChaosWindow, int> AckedButLostByWindow,
    IReadOnlyDictionary<string, int> AckedButLostByPattern,
    IReadOnlyList<PublishRecord> AckedButLostSample,
    int ConsumesWithoutPublish);
