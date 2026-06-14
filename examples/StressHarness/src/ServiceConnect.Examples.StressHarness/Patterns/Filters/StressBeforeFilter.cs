using ServiceConnect.Examples.StressHarness.Patterns.Middleware;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Filters;

/// <summary>
/// BeforeConsuming-stage filter that records its execution into the shared
/// <see cref="MiddlewareTrail"/> as the first stage of the pipeline-ordering
/// pattern. The driver asserts the trail captures
/// <c>[before, mid-enter, handler, mid-exit, on-success]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The filter is invoked once per inbound <c>DedupedMessage</c> on the receiver
/// bus. It extracts the flow id from the envelope's headers — values arrive as
/// <see cref="object"/> because the transport may deliver them as either
/// <see cref="string"/> (in-process / serialiser fast-path) or <see cref="byte"/>[]
/// (RabbitMQ wire format). <see cref="HeaderDecoder.Decode"/> normalises both
/// shapes; a raw <c>is string</c> check would miss the wire form and silently skip
/// every flow on the live broker.
/// </para>
/// <para>
/// Returns <see cref="FilterAction.Continue"/> unconditionally — the filter is
/// observational and must not block the message, otherwise the middleware and
/// handler markers can never fire.
/// </para>
/// </remarks>
public sealed class StressBeforeFilter(MiddlewareTrail trail) : IFilter
{
    public Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            trail.Record(flowId, "before");
        }
        return Task.FromResult(FilterAction.Continue);
    }
}
