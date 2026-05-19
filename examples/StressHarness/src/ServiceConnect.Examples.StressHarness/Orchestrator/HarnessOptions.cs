namespace ServiceConnect.Examples.StressHarness.Orchestrator;

/// <summary>
/// Orchestrator-level configuration shared by both buses owned by <see cref="HarnessHost"/>.
/// </summary>
/// <param name="BrokerUri">
/// RabbitMQ broker address. Either a bare host (<c>localhost</c>) or an AMQP-form URI
/// (<c>amqp://host:5672</c>). Only the host component is forwarded to
/// <see cref="ServiceConnect.Interfaces.Configuration.ITransportConfiguration.Host"/>.
/// </param>
/// <param name="PersistenceMode">
/// <c>"inmemory"</c> or <c>"mongo"</c>. Selects the persistence registration applied to
/// both buses. Anything else is treated as in-memory.
/// </param>
/// <param name="MongoConnectionString">
/// Connection string used when <paramref name="PersistenceMode"/> is <c>"mongo"</c>;
/// ignored otherwise. Required (non-null) in mongo mode — <see cref="HarnessHost"/>
/// throws at start when missing.
/// </param>
/// <param name="FlowTimeout">
/// Per-flow wall-clock budget the orchestrator (Task 12) applies when awaiting completion
/// of an individual pattern run.
/// </param>
/// <param name="MemoryBudgetBytes">
/// Memory-assertion ceiling consulted by <c>MemoryAssertions</c> at flow boundaries.
/// </param>
/// <param name="ReportDir">
/// Filesystem directory the reporting layer writes JSON/Markdown summaries into.
/// </param>
public sealed record HarnessOptions(
    string BrokerUri,
    string PersistenceMode,
    string? MongoConnectionString,
    TimeSpan FlowTimeout,
    long MemoryBudgetBytes,
    string ReportDir);
