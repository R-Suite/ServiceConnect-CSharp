using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Built-in <see cref="IMessageProcessingMiddleware"/> that emits one consume
/// activity per inbound message via <see cref="ServiceConnectActivitySource"/>.
/// </summary>
internal sealed class TelemetryProcessingMiddleware(
    ServiceConnectInstrumentationOptions options,
    IMessagingSystemAttributes attributes) : IMessageProcessingMiddleware
{
    private readonly ServiceConnectInstrumentationOptions _options = options;
    private readonly IMessagingSystemAttributes _attributes = attributes;

    /// <inheritdoc/>
    public async Task<ConsumeEventResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object message,
        IDictionary<string, object> headers,
        Envelope envelope,
        MessageProcessingDelegate next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(next);

        Activity? activity = null;
        IDisposable? inboundFallback = null;
        if (ServiceConnectActivitySource.IsConsumeTelemetryEnabled(_options))
        {
            var args = new ConsumeEventArgs
            {
                Message = envelope.Body.ToArray(),
                Type = messageType.FullName ?? string.Empty,
                // IMessageProcessingMiddleware's contract types `headers` as IDictionary<string,object>;
                // the in-tree RabbitMQ transport always supplies a Dictionary<,> (which also implements
                // IReadOnlyDictionary<,>), but third-party transports may supply an IDictionary impl that
                // doesn't — a downcast would throw InvalidCastException mid-pipeline. Defensive copy
                // bounded to consume-telemetry-enabled probes: the ConsumeEventArgs surface needs
                // IReadOnlyDictionary<,>, so we materialise one. Cost is one Dictionary alloc with the
                // 5-15 typical ServiceConnect headers.
                Headers = new Dictionary<string, object>(headers, StringComparer.Ordinal),
            };
            activity = ServiceConnectActivitySource.Consume(args, _options, _attributes);
        }
        else if (ServiceConnectActivitySource.IsPublishTelemetryEnabled(_options) || ServiceConnectActivitySource.IsSendTelemetryEnabled(_options))
        {
            // Consume telemetry is disabled but the handler may still publish or send. Without
            // intervention, Activity.Current is null when the handler invokes Bus.Send/Publish,
            // so the new publish/send activity becomes a fresh trace root and the downstream
            // consumer cannot stitch the graph across this hop. Extract the inbound traceparent
            // into an AsyncLocal so the publish/send paths use it as their parent context.
            inboundFallback = TryStashInboundTraceFallback(headers);
        }

        try
        {
            var result = await next(messageBytes, messageType, message, headers, envelope, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                if (result.Exception is not null)
                {
                    ServiceConnectActivitySource.SetError(activity, result.Exception, _options);
                }
                else
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Dispatch returned Success=false without an exception");
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            ServiceConnectActivitySource.SetError(activity, ex, _options);
            throw;
        }
        finally
        {
            activity?.Dispose();
            inboundFallback?.Dispose();
        }
    }

    private static IDisposable? TryStashInboundTraceFallback(IDictionary<string, object> headers)
    {
        // Mirror the propagator's W3C extract: read traceparent and (optional) tracestate
        // from the inbound headers and stash the raw strings so the publish-side fallback
        // can write them verbatim into outgoing headers. Header values arrive byte[]-encoded
        // from the RabbitMQ transport; HeaderDecoder unwraps both byte[] and string forms.
        if (!headers.TryGetValue(TraceParentHeaderKey, out var traceParentObj))
        {
            return null;
        }
        var traceParent = HeaderDecoder.Decode(traceParentObj);
        if (string.IsNullOrEmpty(traceParent))
        {
            return null;
        }

        string? traceState = null;
        if (headers.TryGetValue(TraceStateHeaderKey, out var traceStateObj))
        {
            traceState = HeaderDecoder.Decode(traceStateObj);
        }

        return ServiceConnectActivitySource.SetInboundTraceFallback(traceParent, traceState);
    }

    private const string TraceParentHeaderKey = "traceparent";
    private const string TraceStateHeaderKey = "tracestate";
}
