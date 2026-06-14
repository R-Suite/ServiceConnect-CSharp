using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives a single <see cref="ServiceConnect.Interfaces.IBus.PublishRequestAsync"/>
/// fanout per direction and asserts the framework collected exactly
/// <c>ExpectedReplyCount = 2</c> replies — one from each bus subscribed to the
/// <see cref="SearchRequest"/> type-fanout exchange. The directional run is
/// symmetric: α publishes and both α and β reply; β publishes and both α and β
/// reply. The <see cref="StressFlowContext.ExpectedReceiver"/> field is unused
/// here because the publish reaches both buses by construction.
/// </summary>
/// <remarks>
/// <para>
/// Synchronisation is taken from <see cref="IBus.PublishRequestAsync"/>'s awaited
/// completion: the task returns once the reply count or the timeout is reached.
/// The per-reply <see cref="Action{TReply}"/> callback is invoked synchronously by
/// the framework's reply pump on every accepted reply, so a lock around the
/// shared <see cref="List{T}"/> serialises mutations across the two bus pumps.
/// Per-handler signals are still booked in the receiver-side handler for
/// accounting parity with the other drivers, but the driver's success criterion
/// is the reply set.
/// </para>
/// <para>
/// Each publish books two expected handler invocations against the flow id: the
/// fanout reaches one handler per bus, and accounting reconciliation needs to
/// see two <see cref="FlowAccounting.RecordHandled"/> calls for the flow to be
/// considered fully drained. The handler instances each call
/// <see cref="PerHandlerSignal.Signal"/> on the same flow id; the rendezvous
/// registry is one-shot per id so only the first signal wins the await, but the
/// accounting counter is monotonically incremented and observes both.
/// </para>
/// </remarks>
public sealed class ScatterGatherDriver(FlowAccounting accounting, PerHandlerSignal signals) : IPatternDriver
{
    public string Name => "scatter-gather";
    public bool RequiresPersistence => false;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; scatter-gather fanout reaches both buses regardless of which is named the sender.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        _ = signals;
        _ = receiver;
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        // Two expected invocations per flow: one handler per subscribed bus fires
        // on the fanout. Booked up-front so reconciliation cannot observe a handler
        // signal whose corresponding send hasn't been booked yet.
        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: 2);

        var replies = new List<SearchResponse>();
        var repliesLock = new object();

        var request = new SearchRequest(context.FlowId) { Query = context.FlowId.ToString("N") };
        // Scatter-gather is uniquely sensitive to chaos cycles: a flow needs BOTH replies,
        // each carried by its own handler-dispatch chain that can stack one publisher
        // retry budget (~30s ack-wait + ~10s inter-attempt delay) plus the consumer-side
        // retry-queue delay. A single ill-aligned chaos cycle is absorbed by the publish
        // retry; two consecutive cycles bracketing the same request-reply window can push
        // a single reply past the per-flow timeout — at which point the framework's
        // RequestReplyManager evicts the correlation entry and any late-arriving reply is
        // silently dropped (no entry to match against). Doubling the request-reply timeout
        // gives the second reply enough wall-clock to ride out a two-cycle alignment.
        var options = new RequestOptions
        {
            ExpectedReplyCount = 2,
            Timeout = (int)(context.FlowTimeout.TotalMilliseconds * 2),
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = context.FlowId.ToString("N"),
                [StressHeaders.OriginBus] = context.Origin.ToHeaderValue(),
                [StressHeaders.Pattern] = Name,
            },
        };

        try
        {
            await sender.PublishRequestAsync<SearchRequest, SearchResponse>(
                request,
                reply =>
                {
                    // The framework's reply pump may invoke this callback from either bus
                    // delivery thread; serialise the list mutation so a near-simultaneous
                    // pair of replies cannot race on the underlying List<T> backing array.
                    lock (repliesLock)
                    {
                        replies.Add(reply);
                    }
                },
                options,
                cancellationToken).ConfigureAwait(false);

            // Snapshot under the same lock so the assertion path observes the same
            // memory model the writers used. PublishRequestAsync returned, so the
            // reply pump should have finished invoking the callback, but the lock
            // costs nothing and removes any doubt about the happens-before edge.
            List<SearchResponse> snapshot;
            lock (repliesLock)
            {
                snapshot = [.. replies];
            }

            if (snapshot.Count != 2)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"scatter-gather {context.Origin.ToHeaderValue()}: expected 2 replies but observed {snapshot.Count}"));
            }
            else
            {
                // Cross-tenant assertion: a meaningful fanout reaches BOTH subscribers,
                // so the reply set must contain distinct catalog names that match the
                // two bus tags. Two replies from the same bus tag would indicate a
                // duplicate dispatch or a missed subscriber binding.
                var catalogs = snapshot.Select(r => r.CatalogName).ToHashSet(StringComparer.Ordinal);
                if (!catalogs.Contains("alpha") || !catalogs.Contains("beta"))
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"scatter-gather {context.Origin.ToHeaderValue()}: expected replies from both 'alpha' and 'beta' but observed [{string.Join(", ", catalogs)}]"));
                }

                foreach (var reply in snapshot)
                {
                    if (reply.CorrelationId != context.FlowId)
                    {
                        failures.Add(string.Create(CultureInfo.InvariantCulture,
                            $"scatter-gather {context.Origin.ToHeaderValue()}: reply correlation id {reply.CorrelationId:N} does not match flow {context.FlowId:N}"));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"scatter-gather {context.Origin.ToHeaderValue()}: fanout did not collect 2 replies within {context.FlowTimeout}"));
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 1, handled: 2)
            : FlowResult.Fail(sw.Elapsed, sent: 1, handled: 0, [.. failures]);
    }
}
