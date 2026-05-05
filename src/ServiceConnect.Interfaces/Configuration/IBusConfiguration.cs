namespace ServiceConnect.Interfaces.Configuration;

/// <summary>
/// Configures ServiceConnect bus runtime behavior.
/// </summary>
public interface IBusConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether handler discovery scans configured assemblies automatically.
    /// </summary>
    /// <remarks>
    /// When <c>false</c>, handler discovery does not scan the AppDomain automatically.
    /// Assemblies explicitly supplied via <see cref="M:ServiceConnect.ServiceConnectBuilder.ScanAssemblies(System.Reflection.Assembly[])"/>
    /// are still scanned — the explicit list takes precedence over this flag.
    /// </remarks>
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
    /// Optional async hook invoked when message dispatch throws. Awaited by the dispatcher
    /// before returning the failure result, so slow handlers no longer block the consumer
    /// thread (v7 used <c>Action&lt;Exception&gt;</c> and was synchronous).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cancellation token is the dispatcher's shutdown CTS; honour it to avoid stretching
    /// shutdown deadlines.
    /// </para>
    /// <para>
    /// <b>Notification hook semantics.</b> The dispatcher invokes this callback after the
    /// message-dispatch failure has already been recorded. The original exception is attached
    /// to the returned <c>ConsumeEventResult</c> regardless of what the callback does — there
    /// is no way for the callback to signal "treat this exception as success." To suppress
    /// retries, throw or swallow inside the handler, or configure <c>DisableErrors</c> at the
    /// queue level.
    /// </para>
    /// <para>
    /// <b>Crash visibility.</b> If the callback itself throws, the dispatcher catches the
    /// exception, logs at <c>Error</c> level with the message-type for correlation, and
    /// continues. The original dispatch failure flows through to the retry/error-queue path
    /// normally; a flaky notification hook cannot block message processing.
    /// </para>
    /// <para>
    /// Migration from v7: wrap your <c>Action&lt;Exception&gt;</c> as
    /// <c>(ex, _) =&gt; { Sync(ex); return ValueTask.CompletedTask; }</c>.
    /// </para>
    /// </remarks>
    Func<Exception, CancellationToken, ValueTask>? ExceptionHandler { get; set; }
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

    /// <summary>
    /// When <c>true</c>, messages that the dispatcher runs to completion on but which no
    /// processor claims (see <see cref="ConsumeEventResult.NotHandled"/>) are published to
    /// the error exchange instead of silently acked. Defaults to <c>false</c> — unhandled
    /// messages are logged and acked, preserving historical behaviour. Enable when a
    /// handler-less message should be treated as a terminal failure for operator visibility.
    /// </summary>
    bool DeadLetterUnhandledMessages { get; set; }
}
