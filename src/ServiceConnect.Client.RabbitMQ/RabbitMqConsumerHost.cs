using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Owns the RabbitMQ channel, consumer, and ack/nack lifecycle for a single queue.
/// Delegates failure-path policy to <see cref="MessageRetryHandler"/> and success-path
/// audit publish to <see cref="MessageAuditPublisher"/>.
/// </summary>
internal sealed class RabbitMqConsumerHost : IAsyncDisposable
{
    private readonly IServiceConnectConnection _connection;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly MessageRetryHandler _retryHandler;
    private readonly MessageAuditPublisher _auditPublisher;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    // R-050: inbound header count and per-value size limits to prevent resource exhaustion.
    private const int DefaultMaxHeaderCount = 64;
    private const int DefaultMaxHeaderValueBytes = 8192;

    private readonly bool _errorsDisabled;
    private readonly ushort _prefetchCount;
    private readonly bool _disablePrefetch;
    private readonly IDictionary<string, object?> _queueArguments;
    private readonly int _gracefulShutdownTimeoutMs;
    private readonly bool _includeMachineNameInHeaders;
    private readonly long _maxInboundMessageSize;

    private IChannel? _model;
    private ConsumerEventHandler? _consumerEventHandler;
    private AsyncEventingBasicConsumer? _consumer;
    private bool _autoDelete;
    private string _queueName = "";
    private string _retryQueueName = "";
    private int _messagesBeingProcessed;

    public RabbitMqConsumerHost(
        IServiceConnectConnection connection,
        ITransportConfiguration transportConfiguration,
        IQueueConfiguration queueConfiguration,
        IBusConfiguration busConfiguration,
        MessageRetryHandler retryHandler,
        MessageAuditPublisher auditPublisher,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _queueConfiguration = queueConfiguration ?? throw new ArgumentNullException(nameof(queueConfiguration));
        _retryHandler = retryHandler ?? throw new ArgumentNullException(nameof(retryHandler));
        _auditPublisher = auditPublisher ?? throw new ArgumentNullException(nameof(auditPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        ArgumentNullException.ThrowIfNull(transportConfiguration);
        ArgumentNullException.ThrowIfNull(busConfiguration);

        // R-043: Extract configuration in constructor, make fields readonly
        _includeMachineNameInHeaders = busConfiguration.IncludeMachineNameInHeaders;

        var settings = transportConfiguration.ClientSettings;
        _errorsDisabled = queueConfiguration.DisableErrors;
        _autoDelete = settings.TryGetValue(RabbitMQSettingKeys.AutoDelete, out var autoDeleteVal) && (bool)autoDeleteVal;
        _prefetchCount = settings.TryGetValue(RabbitMQSettingKeys.PrefetchCount, out var prefetchVal)
            ? Convert.ToUInt16((int)prefetchVal)
            : transportConfiguration.PrefetchCount;
        _disablePrefetch = settings.TryGetValue(RabbitMQSettingKeys.DisablePrefetch, out var disablePrefetchVal) && (bool)disablePrefetchVal;
        _queueArguments = settings.TryGetValue(RabbitMQSettingKeys.Arguments, out var argsVal)
            ? (IDictionary<string, object?>)argsVal
            : new Dictionary<string, object?>();
        _gracefulShutdownTimeoutMs = transportConfiguration.GracefulShutdownTimeoutMilliseconds > 0
            ? transportConfiguration.GracefulShutdownTimeoutMilliseconds
            : 5000;
        _maxInboundMessageSize = settings.TryGetValue(RabbitMQSettingKeys.MessageSize, out var maxSizeVal)
            ? Convert.ToInt64(maxSizeVal)
            : 64 * 1024;
    }

    public async Task StartConsumingAsync(
        ConsumerEventHandler messageReceived, string queueName,
        bool? exclusive = null, bool? autoDelete = null, CancellationToken cancellationToken = default)
    {
        _consumerEventHandler = messageReceived;
        _queueName = queueName;
        _retryQueueName = queueName + RabbitMqQueueNaming.RetryQueueSuffix;

        if (autoDelete.HasValue) _autoDelete = autoDelete.Value;

        _model = await _connection.CreateChannelAsync().ConfigureAwait(false);
        if (!_disablePrefetch)
            await _model.BasicQosAsync(0, _prefetchCount, false).ConfigureAwait(false);

        _consumer = new AsyncEventingBasicConsumer(_model);
        _consumer.ReceivedAsync += async (sender, args) => await EventAsync(sender, args, cancellationToken).ConfigureAwait(false);

        var consumerTag = await _model.BasicConsumeAsync(_queueName, false, "", false, false, null, _consumer).ConfigureAwait(false);
        _logger.LogDebug("Started consuming on {QueueName}, tag={ConsumerTag}", _queueName, consumerTag);
    }

    public async Task ConsumeMessageTypeAsync(string messageTypeName)
    {
        await _model!.QueueBindAsync(_queueName, messageTypeName, string.Empty, _queueArguments).ConfigureAwait(false);
    }

    // R-011: Pass cancellationToken as method parameter instead of storing it
    private async Task EventAsync(object consumer, BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        // Capture _model before any await so that a concurrent DisposeAsync cannot
        // null it out from under us in the finally block (R-020).
        var model = _model;
        bool processed = false;
        try
        {
            Interlocked.Increment(ref _messagesBeingProcessed);

            if (args.BasicProperties.Headers == null ||
                (!args.BasicProperties.Headers.ContainsKey(HeaderKeys.TypeName) &&
                 !args.BasicProperties.Headers.ContainsKey(HeaderKeys.FullTypeName)))
            {
                _logger.LogError("Error processing message, Message headers must contain type name.");
                processed = true; // no retry possible for malformed messages, ack to discard
                return;
            }

            if (args.Body.Length > _maxInboundMessageSize)
            {
                _logger.LogWarning(
                    "Rejecting oversized message: {Size} bytes exceeds limit of {Max} bytes on queue {Queue}",
                    args.Body.Length, _maxInboundMessageSize, _queueConfiguration.QueueName);
                return; // processed stays false → nacked by the finally block
            }

            // R-050: reject messages with excessive header count or oversized header values before
            // doing any work. processed stays false → nacked by the finally block.
            var inboundHeaders = args.BasicProperties.Headers;
            if (inboundHeaders != null && inboundHeaders.Count > DefaultMaxHeaderCount)
            {
                _logger.LogWarning(
                    "Rejecting message: header count {Count} exceeds limit of {Max} on queue {Queue}",
                    inboundHeaders.Count, DefaultMaxHeaderCount, _queueConfiguration.QueueName);
                return;
            }

            if (inboundHeaders != null)
            {
                foreach (var kvp in inboundHeaders)
                {
                    if (kvp.Value is byte[] bytes && bytes.Length > DefaultMaxHeaderValueBytes)
                    {
                        _logger.LogWarning(
                            "Rejecting message: header '{Key}' value size {Size} bytes exceeds limit of {Max} bytes on queue {Queue}",
                            kvp.Key, bytes.Length, DefaultMaxHeaderValueBytes, _queueConfiguration.QueueName);
                        return;
                    }
                }
            }

            await ProcessMessageAsync(args, cancellationToken).ConfigureAwait(false);
            processed = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
        }
        finally
        {
            try
            {
                if (model == null)
                {
                    _logger.LogWarning("Channel was null during ack/nack — message {DeliveryTag} may be redelivered", args.DeliveryTag);
                }
                else if (processed)
                    await model.BasicAckAsync(args.DeliveryTag, false).ConfigureAwait(false);
                else
                    await model.BasicNackAsync(args.DeliveryTag, false, true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error acking/nacking the message");
            }

            Interlocked.Decrement(ref _messagesBeingProcessed);
        }
    }

    private async Task ProcessMessageAsync(BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        ConsumeEventResult result;
        // Pre-size the dict to the incoming header count plus 3 consumer-added
        // headers (TimeReceived, DestinationMachine, DestinationAddress) so we
        // avoid rehashes during the copy — per-message hot path (P-09, P-018).
        var sourceHeaders = args.BasicProperties.Headers;

        var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 4) + 3);
        if (sourceHeaders != null)
        {
            foreach (var kvp in sourceHeaders)
            {
                if (kvp.Value is not null) headers[kvp.Key] = kvp.Value;
            }
        }

        if (args.Redelivered)
            HeaderHelpers.SetHeader(headers, HeaderKeys.Redelivered, true);

        try
        {
            HeaderHelpers.SetHeader(headers, HeaderKeys.TimeReceived, FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime));
            if (_includeMachineNameInHeaders)
                HeaderHelpers.SetHeader(headers, HeaderKeys.DestinationMachine, Environment.MachineName);
            HeaderHelpers.SetHeader(headers, HeaderKeys.DestinationAddress, _queueConfiguration.QueueName);

            // P-019: single TryGetValue lookup instead of ContainsKey + indexer.
            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var typeNameRaw))
                typeNameRaw = headers[HeaderKeys.TypeName];
            string typeName = HeaderDecoder.Decode(typeNameRaw) ?? "";

            if (_consumerEventHandler == null)
            {
                _logger.LogError("Consumer event handler not set — message will be nacked for redelivery. Queue: {Queue}", _queueConfiguration.QueueName);
                result = new ConsumeEventResult { Success = false };
            }
            else
            {
                result = await _consumerEventHandler(args.Body, typeName, headers, cancellationToken).ConfigureAwait(false);
            }

            HeaderHelpers.SetHeader(headers, HeaderKeys.TimeProcessed, FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime));
        }
        catch (Exception ex)
        {
            result = new ConsumeEventResult { Exception = ex, Success = false };
        }

        if (!result.Success)
        {
            await _retryHandler.HandleFailureAsync(_model!, _retryQueueName, args, headers, result.Exception).ConfigureAwait(false);
        }
        else if (!_errorsDisabled)
        {
            await _auditPublisher.PublishAuditIfEnabledAsync(_model!, args, headers).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var deadline = Environment.TickCount64 + _gracefulShutdownTimeoutMs;
        while (Volatile.Read(ref _messagesBeingProcessed) > 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        if (_autoDelete && _model != null)
        {
            try
            {
                _logger.LogDebug("Deleting retry queue");
                await _model.QueueDeleteAsync(_retryQueueName, false, false, false).ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error deleting retry queue");
            }
        }

        await CloseChannelAsync().ConfigureAwait(false);
    }

    // P-015: avoid StringBuilder allocation inside DateTime.ToString("O").
    private static string FormatTimestamp(DateTime dt)
    {
        Span<char> buffer = stackalloc char[33]; // "O" format max length
        dt.TryFormat(buffer, out int charsWritten, "O");
        return new string(buffer[..charsWritten]);
    }

    private async Task CloseChannelAsync()
    {
        if (_model == null) return;
        try
        {
            if (_model.IsOpen) await _model.CloseAsync(200, "Goodbye", false).ConfigureAwait(false);
            _model.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing channel during dispose");
        }
        _model = null;
    }
}
