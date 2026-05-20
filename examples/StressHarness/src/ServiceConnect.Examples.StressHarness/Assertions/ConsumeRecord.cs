using ServiceConnect.Examples.StressHarness.Chaos;

namespace ServiceConnect.Examples.StressHarness.Assertions;

public readonly record struct ConsumeRecord(
    Guid MessageId,
    Guid FlowId,
    string Pattern,
    string ConsumingBus,
    DateTimeOffset Consumed,
    ChaosWindow Window);
