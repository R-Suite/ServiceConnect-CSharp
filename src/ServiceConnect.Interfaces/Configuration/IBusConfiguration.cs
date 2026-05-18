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
    /// before returning the failure result, so a slow handler does not block the consumer thread.
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
    /// Maximum number of routing-slip destinations honoured when forwarding an inbound
    /// <c>RoutingSlip</c> header. Defaults to <c>32</c>. A header containing more entries
    /// than this is rejected (logged and dropped) without forwarding to any destination.
    /// </summary>
    /// <remarks>
    /// Caps the per-message amplification factor when a hostile inbound message carries
    /// a hand-crafted slip header (e.g. <c>victim-q,victim-q,…</c> repeated within the
    /// per-value header byte budget). Without a cap, ~900 entries fit within the default
    /// 8 KiB header-value cap, so one delivered message can drive ~900 handler invocations.
    /// Lowering the cap below 32 trades hops-per-business-workflow against DoS protection.
    /// </remarks>
    int MaxRoutingSlipHops { get; set; }

    /// <summary>
    /// When <c>true</c>, messages that the dispatcher runs to completion on but which no
    /// processor claims (see <see cref="ConsumeEventResult.NotHandled"/>) are published to
    /// the error exchange instead of silently acked. Defaults to <c>false</c> — unhandled
    /// messages are logged and acked, preserving historical behaviour. Enable when a
    /// handler-less message should be treated as a terminal failure for operator visibility.
    /// </summary>
    bool DeadLetterUnhandledMessages { get; set; }

    /// <summary>
    /// When <c>true</c>, the heuristic fallback inside
    /// <c>ConsumeContext.IsTrustedRequestReplyEnvelope</c> is disabled — only requests
    /// tracked by the local request-reply manager are trusted to bypass
    /// <see cref="ValidateReplyDestinations"/>. Defaults to <c>false</c>, which
    /// preserves the legacy behaviour: any inbound message with a non-empty
    /// <c>RequestMessageId</c>, a <c>SourceAddress</c>, a <c>MessageId</c>, no
    /// <c>ResponseMessageId</c>, and <c>DestinationAddress == this queue</c> is also
    /// trusted as a request envelope. That fallback enables cross-bus request-reply (the
    /// request originated on a different bus instance and the local
    /// <c>IReplyStatusRequestReplyManager</c> doesn't know it), but the headers it relies
    /// on can be crafted by any external producer that knows our queue name — so a hostile
    /// peer could redirect our reply by spoofing them.
    /// </summary>
    /// <remarks>
    /// Set to <c>true</c> when (a) the service does not participate in cross-bus
    /// request-reply, or (b) the operator explicitly verifies that all upstream callers
    /// route through a tracked <c>RequestReplyManager</c>. Otherwise leave at the default
    /// to preserve backward compatibility — strict mode will reject legitimate cross-bus
    /// request-reply traffic that legacy callers rely on. The flag is non-breaking because
    /// the default value preserves existing trust decisions exactly.
    /// </remarks>
    bool StrictReplyValidation { get; set; }

    /// <summary>
    /// Maximum time <c>Bus.DisposeAsync</c> waits for the lifecycle semaphore before
    /// proceeding with teardown anyway. A wedged <c>StartConsumingAsync</c> (e.g., broker
    /// partition during handshake) would otherwise block the semaphore indefinitely and
    /// hang container shutdown. Default: 30 seconds. Must be positive and at most
    /// <c>uint.MaxValue - 1</c> milliseconds (the .NET timer-API ceiling), or
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for no bound.
    /// </summary>
    TimeSpan DisposeTimeout { get; set; }

    /// <summary>
    /// Gets or sets whether the host is allowed to start without an <see cref="IProducer"/>
    /// registered. Defaults to <see langword="false"/>: <c>BusHostedService.StartAsync</c>
    /// throws <see cref="InvalidOperationException"/> at host start if no <see cref="IProducer"/>
    /// has been registered, surfacing the missing transport at host build time rather than
    /// at the first publish/send/<c>CreateStream</c> call. Set to <see langword="true"/> only
    /// in tests or specialised in-memory scenarios that legitimately operate without a
    /// producer (consume-only buses).
    /// </summary>
    bool AllowMissingProducer { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of in-flight request-reply exchanges before
    /// <c>SendRequestAsync</c> / <c>SendRequestMultiAsync</c> throws
    /// <see cref="InvalidOperationException"/> ("cap reached"). Defaults to 10,000.
    /// Each in-flight request pins a Timer, CancellationTokenSource, and TaskCompletionSource;
    /// the cap defends against unbounded memory growth from <see cref="System.Threading.Timeout.Infinite"/>
    /// callers that never wake or hot loops of unawaited requests. Increase for genuine
    /// high-concurrency request-fan workloads; decrease to harden against caller bugs.
    /// Must be positive.
    /// </summary>
    int MaxInflightRequests { get; set; }

    /// <summary>
    /// Gets or sets the maximum total bytes a single inbound stream may reassemble
    /// before <c>MessageBusReadStream.Write</c> throws <see cref="InvalidOperationException"/>.
    /// Defaults to 100 MB (104,857,600 bytes). Defends against unbounded memory growth
    /// from hostile or buggy producers that never close their stream. Raise for
    /// deployments that stream legitimately large artefacts (file uploads, ML models);
    /// lower to harden memory-constrained hosts. Must be positive.
    /// </summary>
    long MaxStreamSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrently-tracked partial inbound streams.
    /// Defaults to 1,000. <c>StreamProcessor</c> rejects new streams (warning log + drop)
    /// when this cap is reached; defends against DoS via stream-slot exhaustion. Raise
    /// for high-concurrency file-transfer workloads; lower to harden memory-constrained
    /// hosts. Must be positive.
    /// </summary>
    int MaxActiveStreams { get; set; }
}
