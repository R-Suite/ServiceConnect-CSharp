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
                // SendContext doesn't carry the broker-side exchange (the producer maps that
                // from the message type), but the type's full name matches the convention the
                // RabbitMQ producer uses, so it's a meaningful destination tag for the span.
                Exchange = context.MessageType.FullName ?? string.Empty,
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
