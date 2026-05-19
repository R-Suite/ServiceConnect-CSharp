using System.Collections.Concurrent;
using System.Diagnostics;
using ServiceConnect.Telemetry;

namespace ServiceConnect.Examples.StressHarness.Patterns.Telemetry;

/// <summary>
/// Captures every <see cref="Activity"/> emitted by the framework's
/// <see cref="ServiceConnectActivitySource"/> for the lifetime of the harness
/// process. A single instance is shared across both buses; the registered
/// <see cref="ActivityListener"/> is process-global so one subscription picks up
/// spans from any number of buses without per-bus wiring.
/// </summary>
/// <remarks>
/// <para>
/// The listener subscribes to <see cref="ServiceConnectActivitySource.ActivitySourceName"/>
/// (currently <c>ServiceConnect.Telemetry.Bus</c>) and records every stopped
/// activity into a <see cref="ConcurrentBag{T}"/>. The bag is read by the
/// driver after the handler signal fires; reading does not drain, so subsequent
/// drivers (in soak / throughput modes) could re-inspect the same observations
/// — for smoke mode the bag grows by exactly four entries per flow direction
/// (publish + consume on each bus), bounded by the total flow count.
/// </para>
/// <para>
/// <see cref="Dispose"/> tears down the listener subscription. The harness
/// holds the singleton for its full lifetime, so disposal happens only when the
/// process exits; the listener-based ActivitySource model documents that
/// undisposed listeners leak via the source's listener list, hence the explicit
/// IDisposable to keep the harness's shutdown story clean.
/// </para>
/// </remarks>
public sealed class TelemetryObservations : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentBag<ActivitySnapshot> _activities = [];
    private bool _disposed;

    public TelemetryObservations()
    {
        _listener = new ActivityListener
        {
            // Filter by the framework's own ActivitySource name so the listener
            // does not capture host-worker or ASP.NET spans that share the
            // ambient AsyncLocal context — the driver's assertion is scoped
            // strictly to ServiceConnect-emitted activities.
            ShouldListenTo = source => source.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                // Snapshot the tags and conversation-id here under the
                // activity's own Stop() callback — the framework recycles
                // some internal Activity state after the listener returns,
                // so deferring the read until the driver wakes can observe
                // stale tag values on instrumented builds. The snapshot is a
                // shallow record and the activity itself is not retained.
                var conversationId = activity.GetTagItem(MessagingAttributes.MessageConversationId) as string;
                _activities.Add(new ActivitySnapshot(
                    Name: activity.OperationName,
                    Kind: activity.Kind,
                    DisplayName: activity.DisplayName,
                    ConversationId: conversationId));
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>
    /// Returns every span captured for the activity source so far. The snapshot
    /// is taken at call time; subsequent emissions are observed on the next call.
    /// </summary>
    public IReadOnlyList<ActivitySnapshot> Activities => [.. _activities];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _listener.Dispose();
    }
}

/// <summary>
/// Immutable record of a single activity captured by the harness's
/// <see cref="ActivityListener"/>. Carries the minimum the driver needs for
/// per-flow filtering — operation name, kind, display name, and the
/// conversation id stamped from the message's <c>CorrelationId</c>.
/// </summary>
public sealed record ActivitySnapshot(string Name, ActivityKind Kind, string DisplayName, string? ConversationId);
