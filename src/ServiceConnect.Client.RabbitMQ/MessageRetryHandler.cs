using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Failure-path policy for a RabbitMQ client. Given a failed delivery, either re-publishes
/// the message to the per-queue ".Retries" queue (incrementing the retry counter) or,
/// once max retries are exhausted, publishes to the configured error exchange with
/// redacted exception info in the header.
/// </summary>
internal sealed class MessageRetryHandler
{
    private readonly int _maxRetries;
    private readonly string _errorExchange;
    private readonly ILogger _logger;

    public MessageRetryHandler(int maxRetries, string errorExchange, ILogger logger)
    {
        _maxRetries = maxRetries;
        _errorExchange = errorExchange ?? throw new ArgumentNullException(nameof(errorExchange));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task HandleFailureAsync(
        IChannel channel, string retryQueueName,
        BasicDeliverEventArgs args, Dictionary<string, object> headers, Exception? ex)
    {
        int retryCount = 0;
        if (headers.TryGetValue(HeaderKeys.RetryCount, out var raw)
            && int.TryParse(raw?.ToString(), out int parsed)
            && parsed >= 0 && parsed <= _maxRetries + 1)
        {
            retryCount = parsed;
        }

        if (retryCount < _maxRetries)
        {
            retryCount++;
            HeaderHelpers.SetHeader(headers, HeaderKeys.RetryCount, retryCount);
            var props = new BasicProperties(args.BasicProperties) { Headers = HeaderHelpers.ToNullableHeaders(headers) };
            await channel.BasicPublishAsync(string.Empty, retryQueueName, mandatory: false, props, args.Body).ConfigureAwait(false);
            return;
        }

        if (ex != null)
        {
            // Only include type + message in headers. No stack traces or internal details
            // that could leak sensitive information to error-queue consumers. Full diagnostics
            // are logged server-side below.
            HeaderHelpers.SetHeader(headers, HeaderKeys.Exception, JsonConvert.SerializeObject(new
            {
                TimeStamp = DateTime.UtcNow,
                ExceptionType = ex.GetType().FullName,
                Message = HeaderHelpers.GetErrorMessage(ex)
            }));

            _logger.LogError(ex, "Max retries exceeded for MessageId {MessageId}", args.BasicProperties.MessageId);
        }
        else
        {
            _logger.LogError("Max retries exceeded for MessageId {MessageId}", args.BasicProperties.MessageId);
        }
        var errorProps = new BasicProperties(args.BasicProperties) { Headers = HeaderHelpers.ToNullableHeaders(headers) };
        await channel.BasicPublishAsync(_errorExchange, string.Empty, mandatory: false, errorProps, args.Body).ConfigureAwait(false);
    }
}
