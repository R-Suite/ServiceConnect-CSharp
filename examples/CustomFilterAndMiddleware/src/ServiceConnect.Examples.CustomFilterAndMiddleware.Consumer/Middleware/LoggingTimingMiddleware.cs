using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Middleware;

/// <summary>
/// Middleware demonstration. Wraps every handler invocation with structured
/// log entries and a timing measurement. Unlike a filter, middleware runs
/// inline around the handler and observes the message bytes / type / instance.
/// </summary>
public sealed class LoggingTimingMiddleware(ILogger<LoggingTimingMiddleware> logger) : IMessageProcessingMiddleware
{
    public async Task<ConsumeEventResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope,
        MessageProcessingDelegate next,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("→ Handler entry for {MessageType}", messageType.Name);
        var sw = Stopwatch.StartNew();

        try
        {
            var result = await next(messageBytes, messageType, message, headers, envelope, cancellationToken)
                .ConfigureAwait(false);
            sw.Stop();
            logger.LogInformation(
                "← Handler exit for {MessageType} in {ElapsedMs}ms (Success={Success})",
                messageType.Name, sw.ElapsedMilliseconds, result.Success);
            return result;
        }
        catch
        {
            sw.Stop();
            logger.LogInformation(
                "← Handler exit for {MessageType} in {ElapsedMs}ms (THREW)",
                messageType.Name, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
