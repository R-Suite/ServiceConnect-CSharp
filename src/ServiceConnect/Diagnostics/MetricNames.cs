namespace ServiceConnect.Diagnostics;

/// <summary>
/// Names of metrics emitted by ServiceConnect. Exposed as <c>public const string</c>
/// so consumers (Grafana templates, custom <see cref="System.Diagnostics.Metrics.MeterListener"/>,
/// alert rules) can reference them without re-typing strings.
/// </summary>
/// <remarks>
/// OTel-standard names (<c>messaging.publish.duration</c>, <c>messaging.process.duration</c>,
/// <c>messaging.client.published.messages</c>, <c>messaging.client.consumed.messages</c>) follow
/// the <see href="https://opentelemetry.io/docs/specs/semconv/messaging/messaging-metrics/">OpenTelemetry
/// messaging-metrics semantic conventions</see>. ServiceConnect-specific extensions live under the
/// <c>messaging.serviceconnect.*</c> sub-namespace.
/// </remarks>
public static class MetricNames
{
    /// <summary>Histogram (seconds) — duration of a publish operation, from start to broker ack.</summary>
    public const string PublishDuration = "messaging.publish.duration";

    /// <summary>Histogram (seconds) — duration of consumer-side message processing (handler dispatch).</summary>
    public const string ProcessDuration = "messaging.process.duration";

    /// <summary>Counter — number of messages successfully published.</summary>
    public const string PublishedMessages = "messaging.client.published.messages";

    /// <summary>Counter — number of messages consumed, tagged by <c>messaging.outcome</c>.</summary>
    public const string ConsumedMessages = "messaging.client.consumed.messages";

    /// <summary>Counter — number of consumer-side retry attempts (header-counter increments).</summary>
    public const string RetryAttempts = "messaging.serviceconnect.retry.attempts";

    /// <summary>Counter — number of messages dropped because retry publishing failed.</summary>
    public const string RetryDrops = "messaging.serviceconnect.retry.drops";

    /// <summary>Counter — number of publishes that exceeded the configured publish timeout waiting for broker ack.</summary>
    public const string PublishConfirmTimeouts = "messaging.serviceconnect.publish.confirm_timeouts";

    /// <summary>Counter — number of audit messages that failed to publish.</summary>
    public const string AuditDrops = "messaging.serviceconnect.audit.drops";

    /// <summary>UpDownCounter — current count of in-flight (dispatched but not acked) consumer messages.</summary>
    public const string InFlightMessages = "messaging.serviceconnect.process.messages.inflight";

    /// <summary>Counter — number of outgoing operations aborted because an outgoing filter
    /// returned <c>FilterAction.Stop</c>. No publish/send span is emitted for blocked operations,
    /// so this counter is the operator-visible signal for filter-suppressed deliveries.</summary>
    public const string OutgoingFiltersBlocked = "messaging.serviceconnect.outgoing_filters.blocked";

    /// <summary>
    /// Counter incremented when an aggregator handler succeeds but the subsequent
    /// <c>RemoveSnapshotAsync</c> call fails. The framework intentionally swallows the
    /// remove failure to avoid re-running the handler via broker NACK; the rows remain
    /// leased until the lease expires and a peer may then re-claim and re-dispatch
    /// (the at-least-once trade-off). A spike on this counter translates directly into
    /// duplicate handler invocations after the lease expires.
    /// </summary>
    public const string SnapshotRemoveFailedAfterDispatch = "messaging.serviceconnect.aggregator.snapshot_remove_failed_after_dispatch";
}
