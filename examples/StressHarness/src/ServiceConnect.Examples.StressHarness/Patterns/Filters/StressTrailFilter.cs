using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Filters;

/// <summary>
/// BeforeConsuming-stage filter that records its execution into the per-flow
/// <see cref="FilterTrail"/> before the matching handler runs. The companion
/// handler appends its own marker; the filters driver asserts the trail captures
/// <c>filter</c> strictly before <c>handler</c>.
/// </summary>
/// <remarks>
/// <para>
/// The filter is invoked once per inbound message on the receiver bus. It extracts
/// the flow id from the envelope's headers — values arrive as <see cref="object"/>
/// because the transport may deliver them as either <see cref="string"/>
/// (in-process / serialiser fast-path) or <see cref="byte"/>[] (RabbitMQ wire
/// format). <see cref="HeaderDecoder.Decode"/> normalises both shapes; a raw
/// <c>is string</c> check would miss the wire form and silently skip every flow
/// on the live broker.
/// </para>
/// <para>
/// The filter returns <see cref="FilterAction.Continue"/> unconditionally — it is
/// observational and must not block the message, otherwise the handler-marker
/// half of the assertion can never fire.
/// </para>
/// </remarks>
public sealed class StressTrailFilter(FilterTrail trail) : IFilter
{
    public Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            trail.Record(flowId, "filter");
        }
        return Task.FromResult(FilterAction.Continue);
    }
}
