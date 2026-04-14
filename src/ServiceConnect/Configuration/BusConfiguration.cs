using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public sealed class BusConfiguration : IBusConfiguration
{
    public bool ScanForMessageHandlers { get; set; } = true;
    public bool AutoStartConsuming { get; set; } = true;
    public bool EnableProcessManagerTimeouts { get; set; }
    public int ConsumerCount { get; set; } = 1;
    public Action<Exception>? ExceptionHandler { get; set; }
    /// <inheritdoc />
    public bool IncludeMachineNameInHeaders { get; set; }
    public ITransportConfiguration Transport { get; } = new TransportConfiguration();
    public IQueueConfiguration Queues { get; } = new QueueConfiguration();
    public IPersistenceConfiguration Persistence { get; } = new PersistenceConfiguration();
    public PipelineConfiguration Pipeline { get; } = new PipelineConfiguration();
    IPipelineConfiguration IBusConfiguration.Pipeline => Pipeline;
}
