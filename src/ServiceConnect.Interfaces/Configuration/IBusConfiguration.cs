namespace ServiceConnect.Interfaces.Configuration;

public interface IBusConfiguration
{
    bool ScanForMessageHandlers { get; set; }
    bool AutoStartConsuming { get; set; }
    bool EnableProcessManagerTimeouts { get; set; }
    int ConsumerCount { get; set; }
    Action<Exception>? ExceptionHandler { get; set; }
    /// <summary>
    /// When <c>true</c> (default <c>false</c>), <see cref="Environment.MachineName"/> is
    /// stamped into outgoing <c>SourceMachine</c> and incoming <c>DestinationMachine</c>
    /// headers. Leaking an internal hostname to broker audit consumers is information
    /// disclosure in shared-broker deployments, so this defaults off (S-04).
    /// </summary>
    bool IncludeMachineNameInHeaders { get; set; }
    ITransportConfiguration Transport { get; }
    IQueueConfiguration Queues { get; }
    IPersistenceConfiguration Persistence { get; }
    IPipelineConfiguration Pipeline { get; }
}
