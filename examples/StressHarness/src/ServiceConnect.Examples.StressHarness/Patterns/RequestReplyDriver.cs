using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives the request-reply round-trip across the bus pair. One direction per
/// <see cref="RunFlowAsync"/> invocation; <see cref="Orchestrator.FlowRunner"/>
/// schedules α→β and β→α concurrently against the same driver instance.
/// </summary>
/// <remarks>
/// <para>
/// Routing is endpoint-explicit: the driver computes the receiver's queue name from
/// <see cref="StressFlowContext.ExpectedReceiver"/> and stamps it on
/// <see cref="RequestOptions.EndPoint"/>. The reply destination is supplied by the
/// request-reply manager via the framework's reply-to header machinery; both buses
/// already invoke <c>StartConsumingAsync</c> in <see cref="Orchestrator.HarnessHost"/>,
/// so the requester is already pumping its queue when <see cref="IBus.SendRequestAsync"/>
/// awaits the reply.
/// </para>
/// <para>
/// Synchronisation is taken from the awaited reply itself rather than
/// <see cref="PerHandlerSignal"/> — the handler still records and signals for
/// reconciliation purposes, but the driver's success criterion is the matching
/// <see cref="QuoteResponse.CorrelationId"/> coming back through the request-reply
/// pipeline. The <c>receiver</c> bus parameter is required by the
/// <see cref="IPatternDriver"/> contract and is unused here.
/// </para>
/// </remarks>
public sealed class RequestReplyDriver(FlowAccounting accounting, PerHandlerSignal signals) : IPatternDriver
{
    public string Name => "request-reply";
    public bool RequiresPersistence => false;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; signals are populated by the handler for reconciliation.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        _ = signals;
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        // Receiver-side queue name matches HarnessHost.BuildServices: alpha = stress-a.work,
        // beta = stress-b.work. RequestOptions.EndPoint pins the destination so each
        // direction's quote request reaches exactly the bus the driver expects, even
        // though both buses have a QuoteRequestHandler registered.
        var receiverEndpoint = context.ExpectedReceiver == BusIdentity.Alpha ? "stress-a.work" : "stress-b.work";
        var request = new QuoteRequest(context.FlowId) { ProductId = context.FlowId.ToString("N") };
        var options = new RequestOptions
        {
            EndPoint = receiverEndpoint,
            Timeout = (int)context.FlowTimeout.TotalMilliseconds,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = context.FlowId.ToString("N"),
                [StressHeaders.OriginBus] = context.Origin.ToHeaderValue(),
                [StressHeaders.Pattern] = Name,
            },
        };

        // Record the send before the await so the reconciliation pass cannot observe a
        // handler signal whose corresponding send hasn't been booked yet (would surface
        // as a spurious "handled without record of send" failure).
        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: 1);

        try
        {
            var response = await sender.SendRequestAsync<QuoteRequest, QuoteResponse>(
                request, options, cancellationToken).ConfigureAwait(false);

            if (response.CorrelationId != context.FlowId)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"request-reply {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: reply correlation id {response.CorrelationId:N} does not match flow {context.FlowId:N}"));
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add($"request-reply {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: reply did not arrive within {context.FlowTimeout}");
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 1, handled: 1)
            : FlowResult.Fail(sw.Elapsed, sent: 1, handled: 0, [.. failures]);
    }
}
