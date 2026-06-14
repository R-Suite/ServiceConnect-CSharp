using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Patterns.Middleware;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives a single <see cref="DedupedMessage"/> through the receiver bus's full
/// inbound pipeline — BeforeConsuming filter, MessageProcessing middleware enter,
/// the matching handler, middleware exit, OnConsumedSuccessfully filter — and
/// asserts the trail observed on the receiver is
/// <c>[before, mid-enter, handler, mid-exit, on-success]</c> in that order.
/// </summary>
/// <remarks>
/// <para>
/// Routing is endpoint-explicit, matching <see cref="PointToPointDriver"/>:
/// the receiver's queue name is stamped on <see cref="SendOptions.EndPoint"/>
/// so the driver does not depend on per-type queue mapping state. The receiver
/// bus is unused — the assertion lives in the shared trail, not on the receiver
/// <see cref="IBus"/> handle — but the contract requires it.
/// </para>
/// <para>
/// The driver waits for the handler signal first (one-shot rendezvous on the
/// flow id). The signal fires from inside the handler before the dispatcher
/// invokes the OnConsumedSuccessfully filter, so the trail is still missing the
/// final <c>"on-success"</c> marker at wake-up time. A bounded poll loop walks
/// the trail until the fifth entry lands or the per-flow timeout expires; the
/// loop interval is short enough to keep the test responsive without spinning
/// the CPU. Without this gap, the assertion would race the dispatcher and fail
/// roughly half the time on the fast in-process path.
/// </para>
/// </remarks>
public sealed class CustomFilterAndMiddlewareDriver(FlowAccounting accounting, PerHandlerSignal signals, MiddlewareTrail trail) : IPatternDriver
{
    private static readonly string[] ExpectedTrail = ["before", "mid-enter", "handler", "mid-exit", "on-success"];

    // Poll interval for the OnConsumedSuccessfully marker. The handler signal
    // fires before the dispatcher runs the on-success filter, so the trail's
    // fifth entry lands a short time after the driver wakes. 10ms keeps the
    // loop responsive without spinning; the upper bound is the per-flow timeout.
    private static readonly TimeSpan TrailPollInterval = TimeSpan.FromMilliseconds(10);

    public string Name => "custom-filter-middleware";
    public bool RequiresPersistence => false;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; pipeline traffic flows one way.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        var receiverEndpoint = context.ExpectedReceiver == BusIdentity.Alpha ? "stress-a.work" : "stress-b.work";
        var message = new DedupedMessage(context.FlowId) { Token = context.FlowId.ToString("N") };
        var sendOptions = new SendOptions
        {
            EndPoint = receiverEndpoint,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = context.FlowId.ToString("N"),
                [StressHeaders.OriginBus] = context.Origin.ToHeaderValue(),
                [StressHeaders.Pattern] = Name,
            },
        };

        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: 1);
        await sender.SendAsync(message, sendOptions, cancellationToken).ConfigureAwait(false);

        try
        {
            var invocation = await signals.AwaitAsync(context.FlowId, cancellationToken).ConfigureAwait(false);
            var crossCheck = CrossTenantAssertions.Check(invocation.Headers, context.ExpectedReceiver, invocation.BusTag);
            if (!crossCheck.Ok)
            {
                failures.Add(crossCheck.Failure);
            }

            // Wait for the on-success filter to append its marker. The handler
            // signal precedes the OnConsumedSuccessfully stage in the dispatcher,
            // so polling here closes the unavoidable wake-time gap without
            // adding a second rendezvous on the filter itself.
            var snapshot = await WaitForCompleteTrailAsync(context.FlowId, cancellationToken).ConfigureAwait(false);

            if (!TrailMatches(snapshot))
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"custom-filter-middleware {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: expected trail [{string.Join(", ", ExpectedTrail)}] but observed [{string.Join(", ", snapshot)}]"));
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"custom-filter-middleware {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: pipeline did not complete within {context.FlowTimeout}"));
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 1, handled: 1)
            : FlowResult.Fail(sw.Elapsed, sent: 1, handled: 0, [.. failures]);
    }

    private async Task<IReadOnlyList<string>> WaitForCompleteTrailAsync(Guid flowId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = trail.Snapshot(flowId);
            if (snapshot.Count >= ExpectedTrail.Length)
            {
                return snapshot;
            }
            await Task.Delay(TrailPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TrailMatches(IReadOnlyList<string> snapshot)
    {
        if (snapshot.Count != ExpectedTrail.Length)
        {
            return false;
        }
        for (var i = 0; i < ExpectedTrail.Length; i++)
        {
            if (!string.Equals(snapshot[i], ExpectedTrail[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
