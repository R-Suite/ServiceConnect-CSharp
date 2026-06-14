using ServiceConnect.Examples.StressHarness.Chaos;

namespace ServiceConnect.Examples.StressHarness.Assertions;

public readonly record struct PublishRecord(
    Guid MessageId,
    Guid FlowId,
    string Pattern,
    string OriginBus,
    DateTimeOffset PublishStarted,
    DateTimeOffset PublishCompleted,
    PublishOutcome Outcome,
    ChaosWindow Window);
