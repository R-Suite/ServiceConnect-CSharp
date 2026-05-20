using System.Collections.Concurrent;
using System.Diagnostics;
using ServiceConnect.Examples.StressHarness.Assertions;
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
/// activity into a per-flow bag indexed by the
/// <c>messaging.message.conversation_id</c> tag the framework stamps from the
/// message's CorrelationId. Spans whose tag is missing or unparseable are
/// dropped — the harness only emits activities under a fully populated flow id,
/// so a missing tag indicates a span the driver does not own and would not
/// inspect anyway.
/// </para>
/// <para>
/// Per-flow rows are reclaimed by the dispatcher via
/// <see cref="TryRemoveCompleted"/> at the end of each flow so a long-running
/// soak does not retain one snapshot per delivered span; the driver looks up
/// its flow via <see cref="GetActivitiesFor(Guid)"/> before the reclamation
/// runs.
/// </para>
/// <para>
/// <see cref="Dispose"/> tears down the listener subscription. The harness
/// holds the singleton for its full lifetime, so disposal happens only when the
/// process exits; the listener-based ActivitySource model documents that
/// undisposed listeners leak via the source's listener list, hence the explicit
/// IDisposable to keep the harness's shutdown story clean.
/// </para>
/// </remarks>
public sealed class TelemetryObservations : IDisposable, IFlowKeyedSingleton
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentDictionary<Guid, ConcurrentBag<ActivitySnapshot>> _byFlow = new();
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
                if (string.IsNullOrEmpty(conversationId))
                {
                    return;
                }

                // Accept both the framework's default ("D" with dashes, written
                // by ServiceConnectActivitySource via Guid.ToString()) and the
                // dash-stripped "N" form that the harness uses in some
                // adjacent headers. Anything else is a span this listener does
                // not own and the driver would not inspect.
                if (!Guid.TryParseExact(conversationId, "D", out var flowId)
                    && !Guid.TryParseExact(conversationId, "N", out flowId))
                {
                    return;
                }

                var snapshot = new ActivitySnapshot(
                    Name: activity.OperationName,
                    Kind: activity.Kind,
                    DisplayName: activity.DisplayName,
                    ConversationId: conversationId);

                var bag = _byFlow.GetOrAdd(flowId, _ => []);
                bag.Add(snapshot);
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>
    /// Returns every span captured for <paramref name="flowId"/> so far. The
    /// returned collection reflects the bag at call time; subsequent emissions
    /// are observed on the next call. Empty if the flow has no captured spans
    /// (e.g. the driver polled before the framework stopped the Consumer
    /// activity).
    /// </summary>
    public IReadOnlyCollection<ActivitySnapshot> GetActivitiesFor(Guid flowId) =>
        _byFlow.TryGetValue(flowId, out var bag)
            ? bag
            : [];

    /// <summary>
    /// Drops the per-flow snapshot bag for every id in
    /// <paramref name="completedFlowIds"/>. Ids the listener never observed are
    /// ignored.
    /// </summary>
    public void TryRemoveCompleted(IEnumerable<Guid> completedFlowIds)
    {
        foreach (var id in completedFlowIds)
        {
            _byFlow.TryRemove(id, out _);
        }
    }

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
