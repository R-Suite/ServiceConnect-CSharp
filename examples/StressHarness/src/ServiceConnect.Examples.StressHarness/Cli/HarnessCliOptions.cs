namespace ServiceConnect.Examples.StressHarness.Cli;

public sealed record HarnessCliOptions(
    string Mode,                                                       // smoke | soak | throughput
    TimeSpan Duration,
    int Rate,
    IReadOnlyList<string>? Patterns,                                   // null = all
    string Persistence,                                                // inmemory | mongo
    string Chaos,                                                      // none | docker
    string BrokerUri,
    TimeSpan FlowTimeout,
    long MemoryBudgetBytes,
    string ReportDir,
    TimeSpan ChaosInterval,
    TimeSpan ChaosDowntime,
    TimeSpan ChaosRecoveryBudget)
{
    public static HarnessCliOptions Defaults() => new(
        Mode: "smoke",
        Duration: TimeSpan.FromMinutes(5),
        Rate: 100,
        Patterns: null,
        Persistence: "inmemory",
        Chaos: "none",
        BrokerUri: "amqp://localhost",
        FlowTimeout: TimeSpan.FromSeconds(10),
        MemoryBudgetBytes: 256L * 1024 * 1024,
        ReportDir: "out",
        ChaosInterval: TimeSpan.FromSeconds(30),
        ChaosDowntime: TimeSpan.FromSeconds(20),
        ChaosRecoveryBudget: TimeSpan.FromSeconds(60));
}
