using ServiceConnect.Examples.StressHarness.Patterns.Middleware;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Filters;

/// <summary>
/// OnConsumedSuccessfully-stage filter that records its execution into the shared
/// <see cref="MiddlewareTrail"/> as the final stage of the pipeline-ordering
/// pattern. The driver asserts the trail captures
/// <c>[before, mid-enter, handler, mid-exit, on-success]</c>.
/// </summary>
/// <remarks>
/// <para>
/// This stage runs only after a successful handler dispatch (the dispatcher chain
/// returned <c>Success=true</c> and <c>NotHandled=false</c>). A failed dispatch
/// skips this filter entirely, so the marker doubles as evidence the handler
/// completed without throwing.
/// </para>
/// <para>
/// Per <see cref="IFilter"/>'s exception contract, OnConsumedSuccessfully filters
/// SHOULD NOT throw — a throw flips a successful dispatch to <c>Success=false</c>
/// and forces a redelivery that re-runs the handler's side effects. This filter
/// only appends to the trail and returns <see cref="FilterAction.Continue"/>; the
/// trail mutation is in-process and cannot fail.
/// </para>
/// </remarks>
public sealed class StressOnSuccessFilter(MiddlewareTrail trail) : IFilter
{
    public Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            trail.Record(flowId, "on-success");
        }
        return Task.FromResult(FilterAction.Continue);
    }
}
