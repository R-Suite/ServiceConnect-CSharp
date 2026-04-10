using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public class BusConfiguration : IBusConfiguration
{
    public bool ScanForMessageHandlers { get; set; } = true;
    public bool AutoStartConsuming { get; set; } = true;
    public bool EnableProcessManagerTimeouts { get; set; }
    public int Clients { get; set; } = 1;
    public Action<Exception>? ExceptionHandler { get; set; }
    public ITransportConfiguration Transport { get; } = new TransportConfiguration();
    public IQueueConfiguration Queues { get; } = new QueueConfiguration();
    public IPersistenceConfiguration Persistence { get; } = new PersistenceConfiguration();
    public IPipelineConfiguration Pipeline { get; } = new PipelineConfiguration();
}
