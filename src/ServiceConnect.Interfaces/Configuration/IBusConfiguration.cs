namespace ServiceConnect.Interfaces.Configuration;

public interface IBusConfiguration
{
    bool ScanForMessageHandlers { get; set; }
    bool AutoStartConsuming { get; set; }
    bool EnableProcessManagerTimeouts { get; set; }
    int Clients { get; set; }
    Action<Exception>? ExceptionHandler { get; set; }
    ITransportConfiguration Transport { get; }
    IQueueConfiguration Queues { get; }
    IPersistenceConfiguration Persistence { get; }
    IPipelineConfiguration Pipeline { get; }
}
