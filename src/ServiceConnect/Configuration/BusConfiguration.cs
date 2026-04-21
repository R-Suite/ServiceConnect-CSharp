using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

/// <summary>
/// Mutable implementation of <see cref="IBusConfiguration"/> used during application startup.
/// </summary>
public sealed class BusConfiguration : IBusConfiguration
{
    /// <inheritdoc />
    public bool ScanForMessageHandlers { get; set; } = true;
    /// <inheritdoc />
    public bool AutoStartConsuming { get; set; } = true;
    /// <inheritdoc />
    public bool EnableProcessManagerTimeouts { get; set; }
    /// <inheritdoc />
    public TimeSpan ProcessManagerTimeoutPollInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <inheritdoc />
    public int ConsumerCount { get; set; } = 1;
    /// <inheritdoc />
    public Action<Exception>? ExceptionHandler { get; set; }
    /// <inheritdoc />
    public bool IncludeMachineNameInHeaders { get; set; }
    /// <inheritdoc />
    public bool ValidateReplyDestinations { get; set; } = true;
    /// <inheritdoc />
    public bool EnableRoutingSlipProcessing { get; set; } = true;
    /// <inheritdoc />
    public bool DeadLetterUnhandledMessages { get; set; }
    /// <summary>
    /// Gets the transport configuration used to connect to the broker.
    /// </summary>
    public ITransportConfiguration Transport { get; } = new TransportConfiguration();
    /// <summary>
    /// Gets the queue configuration used for local queue names and explicit routing mappings.
    /// </summary>
    public IQueueConfiguration Queues { get; } = new QueueConfiguration();
    /// <summary>
    /// Gets the persistence configuration used for stateful ServiceConnect features.
    /// </summary>
    public IPersistenceConfiguration Persistence { get; } = new PersistenceConfiguration();
    /// <summary>
    /// Gets the configured pipeline filters and middleware.
    /// </summary>
    public PipelineConfiguration Pipeline { get; } = new PipelineConfiguration();
}
