using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Patterns.Telemetry;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives a single <see cref="TracedEvent"/> publish across the bus pair with
/// <see cref="ServiceConnect.Telemetry.TelemetryBuilderExtensions.AddTelemetry"/>
/// wired into both buses. The framework's telemetry middleware emits a Producer
/// activity on the publishing bus and a Consumer activity on the receiving bus;
/// a process-global <see cref="ActivityListener"/> captures every span into
/// <see cref="TelemetryObservations"/>. The driver asserts at least one activity
/// carrying this flow's correlation id (stamped via the framework as
/// <c>messaging.message.conversation_id</c>) was emitted.
/// </summary>
/// <remarks>
/// <para>
/// Routing follows the pub/sub shape: <see cref="IBus.PublishAsync"/> fans the
/// message out across the shared fanout exchange so each bus binds its own queue
/// to it. The receiver-side handler suppresses the local echo, leaving exactly
/// one cross-tenant invocation per flow — the driver's accounting and the
/// telemetry span count both align on that single delivery.
/// </para>
/// <para>
/// The driver waits for the handler signal first to gate on dispatch completion,
/// then polls <see cref="TelemetryObservations.Activities"/> until the
/// per-flow span lands or the timeout expires. The poll is needed because the
/// outer Consumer activity's <c>Stop()</c> is invoked by the framework's
/// processing pipeline after the handler returns; the rendezvous fires inside
/// the handler so the listener's <c>ActivityStopped</c> callback runs strictly
/// after the driver's await wakes.
/// </para>
/// </remarks>
public sealed class TelemetryDriver(FlowAccounting accounting, PerHandlerSignal signals, TelemetryObservations observations) : IPatternDriver
{
    // Poll interval for the per-flow span landing in the observations bag. The
    // handler signal precedes the Consumer activity's Stop() callback in the
    // framework's processing pipeline, so polling here closes the unavoidable
    // wake-time gap without adding a second rendezvous on the listener.
    private static readonly TimeSpan SpanPollInterval = TimeSpan.FromMilliseconds(10);

    public string Name => "telemetry";
    public bool RequiresPersistence => false;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; pub/sub fan-out flows one way.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        var message = new TracedEvent(context.FlowId) { Topic = context.FlowId.ToString("N") };
        var publishOptions = new PublishOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = context.FlowId.ToString("N"),
                [StressHeaders.OriginBus] = context.Origin.ToHeaderValue(),
                [StressHeaders.Pattern] = Name,
            },
        };

        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: 1);
        await sender.PublishAsync(message, publishOptions, cancellationToken).ConfigureAwait(false);

        try
        {
            var invocation = await signals.AwaitAsync(context.FlowId, cancellationToken).ConfigureAwait(false);
            var crossCheck = CrossTenantAssertions.Check(invocation.Headers, context.ExpectedReceiver, invocation.BusTag);
            if (!crossCheck.Ok)
            {
                failures.Add(crossCheck.Failure);
            }

            // The framework stamps the message's CorrelationId onto the Producer
            // and Consumer activities as messaging.message.conversation_id. The
            // driver uses the flow id (which is the message's CorrelationId) as
            // the per-flow span discriminator so concurrent direction siblings
            // do not cross-contaminate the assertion.
            var conversationId = context.FlowId.ToString();
            var perFlowSpans = await WaitForFlowSpanAsync(conversationId, cancellationToken).ConfigureAwait(false);

            if (perFlowSpans.Count == 0)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"telemetry {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: no activity captured for conversation id {conversationId}"));
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"telemetry {context.Origin.ToHeaderValue()}->{context.ExpectedReceiver.ToHeaderValue()}: handler did not fire within {context.FlowTimeout}"));
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 1, handled: 1)
            : FlowResult.Fail(sw.Elapsed, sent: 1, handled: 0, [.. failures]);
    }

    private async Task<IReadOnlyList<ActivitySnapshot>> WaitForFlowSpanAsync(string conversationId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var matches = observations.Activities
                .Where(a => string.Equals(a.ConversationId, conversationId, StringComparison.Ordinal))
                .ToList();
            if (matches.Count > 0)
            {
                return matches;
            }
            await Task.Delay(SpanPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
