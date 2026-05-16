using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ServiceConnect.Diagnostics;

/// <summary>
/// Hosts the <see cref="System.Diagnostics.Metrics.Meter"/> and instruments emitted by
/// ServiceConnect. Always-on: instruments are zero-cost when no listener has subscribed,
/// matching the pattern used by .NET BCL libraries (<c>HttpClient</c>, <c>EFCore</c>).
/// </summary>
/// <remarks>
/// Subscribers wire the meter via <c>MeterProvider.AddMeter("ServiceConnect.Bus")</c> or, on
/// OpenTelemetry, via <c>builder.AddServiceConnectInstrumentation()</c> from
/// <c>ServiceConnect.Telemetry</c>.
/// </remarks>
public static class ServiceConnectMeter
{
    /// <summary>The meter name used by every ServiceConnect instrument.</summary>
    public const string MeterName = "ServiceConnect.Bus";

    private static readonly string _version =
        typeof(ServiceConnectMeter).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static readonly Meter _meter = new(MeterName, _version);

    private static readonly Histogram<double> _publishDuration = _meter.CreateHistogram<double>(
        name: MetricNames.PublishDuration,
        unit: "s",
        description: "Duration of a publish operation, from start to broker ack.");

    private static readonly Histogram<double> _processDuration = _meter.CreateHistogram<double>(
        name: MetricNames.ProcessDuration,
        unit: "s",
        description: "Duration of consumer-side message processing (handler dispatch).");

    private static readonly Counter<long> _publishedMessages = _meter.CreateCounter<long>(
        name: MetricNames.PublishedMessages,
        unit: "{message}",
        description: "Number of messages successfully published.");

    private static readonly Counter<long> _consumedMessages = _meter.CreateCounter<long>(
        name: MetricNames.ConsumedMessages,
        unit: "{message}",
        description: "Number of messages consumed, tagged by outcome.");

    private static readonly Counter<long> _retryAttempts = _meter.CreateCounter<long>(
        name: MetricNames.RetryAttempts,
        unit: "{attempt}",
        description: "Consumer-side retry attempts (header-counter increments).");

    private static readonly Counter<long> _retryDrops = _meter.CreateCounter<long>(
        name: MetricNames.RetryDrops,
        unit: "{drop}",
        description: "Messages dropped because retry publishing failed.");

    private static readonly Counter<long> _publishConfirmTimeouts = _meter.CreateCounter<long>(
        name: MetricNames.PublishConfirmTimeouts,
        unit: "{timeout}",
        description: "Publishes that exceeded the configured publish timeout waiting for broker ack.");

    private static readonly Counter<long> _auditDrops = _meter.CreateCounter<long>(
        name: MetricNames.AuditDrops,
        unit: "{drop}",
        description: "Audit messages that failed to publish.");

    private static readonly Counter<long> _outgoingFiltersBlocked = _meter.CreateCounter<long>(
        name: MetricNames.OutgoingFiltersBlocked,
        unit: "{message}",
        description: "Outgoing operations aborted because an outgoing filter returned FilterAction.Stop.");

    private static readonly UpDownCounter<long> _inFlightMessages = _meter.CreateUpDownCounter<long>(
        name: MetricNames.InFlightMessages,
        unit: "{message}",
        description: "Current count of in-flight (dispatched but not acked) consumer messages.");

    /// <summary>Records a publish duration in seconds with the given tags.</summary>
    public static void RecordPublishDuration(double seconds, in TagList tags)
        => _publishDuration.Record(seconds, tags);

    /// <summary>Records a consumer-side process duration in seconds with the given tags.</summary>
    public static void RecordProcessDuration(double seconds, in TagList tags)
        => _processDuration.Record(seconds, tags);

    /// <summary>Increments the published-messages counter by 1 with the given tags.</summary>
    public static void AddPublishedMessage(in TagList tags) => _publishedMessages.Add(1, tags);

    /// <summary>Increments the consumed-messages counter by 1 with the given tags.</summary>
    public static void AddConsumedMessage(in TagList tags) => _consumedMessages.Add(1, tags);

    /// <summary>Increments the retry-attempts counter by 1 with the given tags.</summary>
    public static void AddRetryAttempt(in TagList tags) => _retryAttempts.Add(1, tags);

    /// <summary>Increments the retry-drops counter by 1 with the given tags.</summary>
    public static void AddRetryDrop(in TagList tags) => _retryDrops.Add(1, tags);

    /// <summary>Increments the publish-confirm-timeouts counter by 1 with the given tags.</summary>
    public static void AddPublishConfirmTimeout(in TagList tags) => _publishConfirmTimeouts.Add(1, tags);

    /// <summary>Increments the audit-drops counter by 1 with the given tags.</summary>
    public static void AddAuditDrop(in TagList tags) => _auditDrops.Add(1, tags);

    /// <summary>Increments the outgoing-filters-blocked counter by 1 with the given tags.</summary>
    public static void AddOutgoingFiltersBlocked(in TagList tags) => _outgoingFiltersBlocked.Add(1, tags);

    /// <summary>Adjusts the in-flight UpDownCounter by <paramref name="delta"/> with the given tags.</summary>
    public static void AddInFlight(long delta, in TagList tags) => _inFlightMessages.Add(delta, tags);

    /// <summary>
    /// Disposes the underlying <see cref="Meter"/>. Call only when unloading the assembly in a
    /// collectible <c>AssemblyLoadContext</c>; for normal long-running processes the meter lives
    /// for process lifetime and disposal is unnecessary. Mirrors
    /// <c>ServiceConnectActivitySource.Shutdown()</c>.
    /// </summary>
    public static void Shutdown() => _meter.Dispose();
}
