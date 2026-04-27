using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Built-in <see cref="ISendMessageMiddleware"/> that emits one publish or
/// send activity per outgoing message via <see cref="ServiceConnectActivitySource"/>.
/// </summary>
internal sealed class TelemetrySendMiddleware(ServiceConnectInstrumentationOptions options) : ISendMessageMiddleware
{
    private readonly ServiceConnectInstrumentationOptions _options = options;

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
            }),
            SendOperation.Send or SendOperation.Request => ServiceConnectActivitySource.Send(new SendEventArgs
            {
                Message = context.Message,
                Headers = context.Headers,
                EndPoint = context.EndPoint ?? string.Empty,
            }),
            _ => null,
        };

        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServiceConnectActivitySource.SetError(activity, ex);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }
}
