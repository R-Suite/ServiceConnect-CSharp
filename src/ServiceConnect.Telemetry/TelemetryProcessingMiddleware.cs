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
        }
    }
}
