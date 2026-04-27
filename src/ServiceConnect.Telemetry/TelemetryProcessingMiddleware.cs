using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Built-in <see cref="IMessageProcessingMiddleware"/> that emits one consume
/// activity per inbound message via <see cref="ServiceConnectActivitySource"/>.
/// </summary>
internal sealed class TelemetryProcessingMiddleware : IMessageProcessingMiddleware
{
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

        var args = new ConsumeEventArgs
        {
            Message = envelope.Body.ToArray(),
            Type = messageType.FullName ?? string.Empty,
            Headers = headers,
        };
        Activity? activity = ServiceConnectActivitySource.Consume(args);

        try
        {
            var result = await next(messageBytes, messageType, message, headers, envelope, cancellationToken).ConfigureAwait(false);
            if (!result.Success && result.Exception is not null)
            {
                ServiceConnectActivitySource.SetError(activity, result.Exception);
            }
            return result;
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
