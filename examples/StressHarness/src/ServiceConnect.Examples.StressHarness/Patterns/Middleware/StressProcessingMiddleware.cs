using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Middleware;

/// <summary>
/// Message-processing middleware that brackets the handler dispatch with
/// <c>mid-enter</c> and <c>mid-exit</c> markers in the shared
/// <see cref="MiddlewareTrail"/>. Combined with the BeforeConsuming filter, the
/// handler, and the OnConsumedSuccessfully filter, this completes the five-stage
/// pipeline ordering the driver asserts:
/// <c>[before, mid-enter, handler, mid-exit, on-success]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The middleware reads the flow id from the inbound <paramref name="headers"/>
/// dictionary rather than the envelope so it sees the same shape as the dispatcher
/// — header values arrive as <see cref="object"/> because the transport may
/// deliver them as either <see cref="string"/> (in-process / serialiser fast-path)
/// or <see cref="byte"/>[] (RabbitMQ wire format). <see cref="HeaderDecoder.Decode"/>
/// normalises both shapes; a raw <c>is string</c> check would miss the wire form.
/// </para>
/// <para>
/// The middleware re-throws any exception raised by <paramref name="next"/> so the
/// dispatcher can flip the consume result to <c>Success=false</c>, but always
/// records the <c>mid-exit</c> marker first via a <c>finally</c> block. The trail
/// therefore captures middleware entry and exit even on a failed handler — though
/// the driver's happy-path assertion only exercises the success branch.
/// </para>
/// </remarks>
public sealed class StressProcessingMiddleware(MiddlewareTrail trail) : IMessageProcessingMiddleware
{
    public async Task<ConsumeEventResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object message,
        IDictionary<string, object> headers,
        Envelope envelope,
        MessageProcessingDelegate next,
        CancellationToken cancellationToken)
    {
        Guid? flowId = null;
        if (headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var parsed))
        {
            flowId = parsed;
            trail.Record(parsed, "mid-enter");
        }

        try
        {
            return await next(messageBytes, messageType, message, headers, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (flowId is { } id)
            {
                trail.Record(id, "mid-exit");
            }
        }
    }
}
