namespace ServiceConnect.Interfaces.Configuration;

public interface IBusConfiguration
{
    bool ScanForMessageHandlers { get; set; }
    bool AutoStartConsuming { get; set; }
    bool EnableProcessManagerTimeouts { get; set; }
    TimeSpan ProcessManagerTimeoutPollInterval { get; set; }
    int ConsumerCount { get; set; }
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
