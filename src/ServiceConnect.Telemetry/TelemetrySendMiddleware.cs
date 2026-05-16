using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Built-in <see cref="ISendMessageMiddleware"/> that emits one publish or
/// send activity per outgoing message via <see cref="ServiceConnectActivitySource"/>.
/// </summary>
internal sealed class TelemetrySendMiddleware(
    ServiceConnectInstrumentationOptions options,
    IMessagingSystemAttributes attributes) : ISendMessageMiddleware
{
    private readonly ServiceConnectInstrumentationOptions _options = options;
    private readonly IMessagingSystemAttributes _attributes = attributes;

    /// <inheritdoc/>
    public async Task ProcessAsync(SendContext context, SendMessageDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Activity? activity = context.Operation switch
        {
            SendOperation.Publish => ServiceConnectActivitySource.Publish(new PublishEventArgs
            {
                Message = context.Message,
                Headers = context.Headers,
                RoutingKey = context.RoutingKey ?? string.Empty,
                // OTel messaging semconv: messaging.destination.name carries the broker-side
                // exchange/topic, not the CLR type. SendContext does not currently carry the
                // broker exchange (the producer pipeline resolves it from the type at the
                // transport layer), so without an upstream architectural change we leave
                // Exchange empty here. Spans surface as messaging.destination.anonymous=true,
                // which is correct ("anonymous from the span's perspective") rather than
                // misleadingly stamping the CLR type into destination.name. Callers wanting
                // per-type span discrimination should use the activity DisplayName
                // ("<routing-key> publish") or attach an enricher via EnrichWithMessage.
                Exchange = string.Empty,
            }, _options, _attributes),
            SendOperation.Send or SendOperation.Request => ServiceConnectActivitySource.Send(new SendEventArgs
            {
                Message = context.Message,
                Headers = context.Headers,
                EndPoint = context.EndPoint ?? string.Empty,
            }, _options, _attributes),
            _ => null,
        };

        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceConnectActivitySource.SetError(activity, ex, _options);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }
}
