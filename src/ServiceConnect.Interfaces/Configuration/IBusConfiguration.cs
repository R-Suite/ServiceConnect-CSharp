namespace ServiceConnect.Interfaces.Configuration;

/// <summary>
/// Configures ServiceConnect bus runtime behavior.
/// </summary>
public interface IBusConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether handler discovery scans configured assemblies automatically.
    /// </summary>
    bool ScanForMessageHandlers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether message consumption starts automatically with the hosted service.
    /// </summary>
    bool AutoStartConsuming { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether process-manager timeouts are polled and dispatched.
    /// </summary>
    bool EnableProcessManagerTimeouts { get; set; }

    /// <summary>
    /// Gets or sets the interval between process-manager timeout polls.
    /// </summary>
    TimeSpan ProcessManagerTimeoutPollInterval { get; set; }

    /// <summary>
    /// Gets or sets the number of consumer loops to run in parallel.
    /// </summary>
    int ConsumerCount { get; set; }

    /// <summary>
    /// Gets or sets an exception callback invoked for handler-processing failures.
    /// </summary>
    Action<Exception>? ExceptionHandler { get; set; }
    /// <summary>
    /// When <c>true</c> (default <c>false</c>), <see cref="Environment.MachineName"/> is
    /// stamped into outgoing <c>SourceMachine</c> and incoming <c>DestinationMachine</c>
    /// headers. Leaking an internal hostname to broker audit consumers is information
    /// disclosure in shared-broker deployments, so this defaults off.
    /// </summary>
    /// <remarks>WARNING: When enabled, Environment.MachineName is stamped into every message header, exposing internal host names to any consumer. Do not enable where messages cross trust boundaries.</remarks>
    bool IncludeMachineNameInHeaders { get; set; }
    /// <summary>
    /// When <c>true</c> (default), <see cref="IConsumeContext.ReplyAsync{TReply}"/> validates
    /// that the <c>SourceAddress</c> header points to a queue known from
    /// <see cref="IQueueConfiguration.QueueMappings"/>, <see cref="IQueueConfiguration.QueueName"/>,
    /// <see cref="IQueueConfiguration.ErrorQueueName"/>, or <see cref="IQueueConfiguration.AuditQueueName"/>.
    /// Set to <c>false</c> to allow replies to arbitrary queue names.
    /// </summary>
    bool ValidateReplyDestinations { get; set; }
    /// <summary>
    /// When <c>true</c> (default), the handler processor will forward messages along
    /// routing-slip destinations found in the <c>RoutingSlip</c> header. When <c>false</c>,
    /// routing-slip headers are silently ignored. Destinations are also validated against
    /// known queues when enabled.
    /// </summary>
    bool EnableRoutingSlipProcessing { get; set; }
}
