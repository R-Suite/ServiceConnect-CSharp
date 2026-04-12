using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

public sealed class Client : IAsyncDisposable
{
    private const ushort DefaultRetryCount = 60;
    private const ushort DefaultRetryTimeInSeconds = 10;

    private IChannel? _model;
    private readonly IServiceConnectConnection _connection;
    private ConsumerEventHandler? _consumerEventHandler;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly ILogger _logger;

    private bool _autoDelete;
    private string _queueName = "";
    private readonly int _maxRetries;
    private readonly bool _errorsDisabled;
    private readonly ushort _prefetchCount;
    private readonly bool _disablePrefetch;
    private readonly IDictionary<string, object?> _queueArguments;
    private string _retryQueueName = "";
    private string _errorExchange = "";
    private string _auditExchange = "";

    private int _messagesBeingProcessed;
    private AsyncEventingBasicConsumer? _consumer;

    public Client(IServiceConnectConnection connection, ITransportConfiguration transportConfiguration, IQueueConfiguration queueConfiguration, ILogger logger)
    {
        _connection = connection;
        _queueConfiguration = queueConfiguration;
        _logger = logger;

        var settings = transportConfiguration.ClientSettings;
        _maxRetries = transportConfiguration.MaxRetries;
        _autoDelete = settings.TryGetValue(RabbitMQSettingKeys.AutoDelete, out var autoDeleteVal) && (bool)autoDeleteVal;
        _errorsDisabled = queueConfiguration.DisableErrors;
        _prefetchCount = settings.TryGetValue(RabbitMQSettingKeys.PrefetchCount, out var prefetchVal) ? Convert.ToUInt16((int)prefetchVal) : transportConfiguration.PrefetchCount;
        _disablePrefetch = settings.TryGetValue(RabbitMQSettingKeys.DisablePrefetch, out var disablePrefetchVal) && (bool)disablePrefetchVal;
        _queueArguments = settings.TryGetValue(RabbitMQSettingKeys.Arguments, out var argsVal) ? (IDictionary<string, object?>)argsVal : new Dictionary<string, object?>();
    }

    /// <summary>
    /// Event fired on HandleBasicDeliver
    /// </summary>
    public async Task Event(object consumer, BasicDeliverEventArgs args)
    {
        bool processed = false;
        try
        {
            Interlocked.Increment(ref _messagesBeingProcessed);

            if (args.BasicProperties.Headers == null ||
                (!args.BasicProperties.Headers.ContainsKey(HeaderKeys.TypeName) &&
                 !args.BasicProperties.Headers.ContainsKey(HeaderKeys.FullTypeName)))
            {
                const string errMsg = "Error processing message, Message headers must contain type name.";
                _logger.LogError(errMsg);
                processed = true; // no retry possible for malformed messages, ack to discard
                return;
            }

            await ProcessMessage(args).ConfigureAwait(false);
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
                if (processed)
                    await _model!.BasicAckAsync(args.DeliveryTag, false).ConfigureAwait(false);
                else
                    await _model!.BasicNackAsync(args.DeliveryTag, false, true).ConfigureAwait(false); // requeue
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error acking/nacking the message");
            }

            Interlocked.Decrement(ref _messagesBeingProcessed);
        }
    }

    private async Task ProcessMessage(BasicDeliverEventArgs args)
    {
        ConsumeEventResult result;
        
        var headers = new Dictionary<string, object>();
        if (args.BasicProperties.Headers != null)
        {
            foreach (var kvp in args.BasicProperties.Headers)
            {
                if (kvp.Value is not null)
                    headers[kvp.Key] = kvp.Value;
            }
        }

        if (args.Redelivered)
            SetHeader(headers, HeaderKeys.Redelivered, true);

        try
        {
            SetHeader(headers, HeaderKeys.TimeReceived, DateTime.UtcNow.ToString("O"));
            SetHeader(headers, HeaderKeys.DestinationMachine, Environment.MachineName);
            SetHeader(headers, HeaderKeys.DestinationAddress, _queueConfiguration.QueueName);

            var typeNameRaw = headers.ContainsKey(HeaderKeys.FullTypeName) ? headers[HeaderKeys.FullTypeName] : headers[HeaderKeys.TypeName];
            string typeName = HeaderDecoder.Decode(typeNameRaw) ?? "";

            if (_consumerEventHandler == null)
            {
                _logger.LogError("Consumer event handler not set — message will be nacked for redelivery. Queue: {Queue}", _queueConfiguration.QueueName);
                result = new ConsumeEventResult { Success = false };
            }
            else
            {
                result = await _consumerEventHandler(args.Body.ToArray(), typeName, headers).ConfigureAwait(false);
            }

            SetHeader(headers, HeaderKeys.TimeProcessed, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            result = new ConsumeEventResult
            {
                Exception = ex,
                Success = false
            };
        }

        if (!result.Success)
        {
            int retryCount = 0;

            if (headers.TryGetValue(HeaderKeys.RetryCount, out var retryCountVal)
                && int.TryParse(retryCountVal?.ToString(), out int parsedRetry)
                && parsedRetry >= 0 && parsedRetry <= _maxRetries + 1)
            {
                retryCount = parsedRetry;
            }

            if (retryCount < _maxRetries)
            {
                retryCount++;
                SetHeader(headers, HeaderKeys.RetryCount, retryCount);

                var retryProps = new BasicProperties(args.BasicProperties) { Headers = ToNullableHeaders(headers) };
                await _model!.BasicPublishAsync(string.Empty, _retryQueueName, mandatory: false, retryProps, args.Body).ConfigureAwait(false);
            }
            else
            {
                if (result.Exception != null)
                {
                    // Only include type + message in headers — no stack traces or internal details
                    // that could leak sensitive information to error queue consumers.
                    // Full diagnostics are logged server-side below.
                    SetHeader(headers, HeaderKeys.Exception, JsonConvert.SerializeObject(new
                    {
                        TimeStamp = DateTime.UtcNow,
                        ExceptionType = result.Exception.GetType().FullName,
                        Message = GetErrorMessage(result.Exception)
                    }));

                    _logger.LogError(result.Exception, "Max retries exceeded for MessageId {MessageId}",
                        args.BasicProperties.MessageId);
                }

                _logger.LogError("Max number of retries exceeded. MessageId: {MessageId}", args.BasicProperties.MessageId);
                var errorProps = new BasicProperties(args.BasicProperties) { Headers = ToNullableHeaders(headers) };
                await _model!.BasicPublishAsync(_errorExchange, string.Empty, mandatory: false, errorProps, args.Body).ConfigureAwait(false);
            }
        }
        else if (!_errorsDisabled)
        {
            string? messageType = null;
            if (headers.TryGetValue(HeaderKeys.MessageType, out var mtRaw))
            {
                messageType = HeaderDecoder.Decode(mtRaw);
            }

            if (_queueConfiguration.AuditingEnabled && messageType != HeaderKeys.ByteStream)
            {
                var auditProps = new BasicProperties(args.BasicProperties) { Headers = ToNullableHeaders(headers) };
                await _model!.BasicPublishAsync(_auditExchange, string.Empty, mandatory: false, auditProps, args.Body).ConfigureAwait(false);
            }
        }
    }

    public async Task StartConsumingAsync(ConsumerEventHandler messageReceived, string queueName, bool? exclusive = null, bool? autoDelete = null)
    {
        _consumerEventHandler = messageReceived;
        _queueName = queueName;
        _retryQueueName = queueName + ".Retries";
        _errorExchange = _queueConfiguration.ErrorQueueName;
        _auditExchange = _queueConfiguration.AuditQueueName;

        if (autoDelete.HasValue)
        {
            _autoDelete = autoDelete.Value;
        }

        await CreateConsumerAsync().ConfigureAwait(false);
    }

    private async Task CreateConsumerAsync()
    {
        _model = await _connection.CreateChannelAsync().ConfigureAwait(false);

        if (!_disablePrefetch)
        {
            await _model.BasicQosAsync(0, _prefetchCount, false).ConfigureAwait(false);
        }

        _consumer = new AsyncEventingBasicConsumer(_model);
        _consumer.ReceivedAsync += Event;

        var consumerTag = await _model.BasicConsumeAsync(_queueName, false, _consumer).ConfigureAwait(false);

        _logger.LogDebug("Started consuming on {QueueName}, tag={ConsumerTag}", _queueName, consumerTag);
    }

    public async Task ConsumeMessageTypeAsync(string messageTypeName)
    {
        // messageTypeName is the name of the exchange
        await _model!.QueueBindAsync(_queueName, messageTypeName, string.Empty, _queueArguments).ConfigureAwait(false);
    }

    private static string GetErrorMessage(Exception exception)
    {
        var sbMessage = new StringBuilder();
        sbMessage.AppendLine(exception.Message);
        var ie = exception.InnerException;
        while (ie != null)
        {
            sbMessage.AppendLine(ie.Message);
            ie = ie.InnerException;
        }
        return sbMessage.ToString();
    }

    private static Dictionary<string, object?> ToNullableHeaders(IDictionary<string, object> headers)
    {
        return headers.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value);
    }

    private static void SetHeader<T>(IDictionary<string, object> headers, string key, T value)
    {
        if (value is null)
        {
            _ = headers.Remove(key);
        }
        else
        {
            headers[key] = value;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Volatile.Read(ref _messagesBeingProcessed) > 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        await CloseChannelAsync().ConfigureAwait(false);

        if (_autoDelete && _model != null)
        {
            try
            {
                _logger.LogDebug("Deleting retry queue");
                await _model.QueueDeleteAsync(_queueName + ".Retries").ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error deleting retry queue");
            }
        }
    }

    private async Task CloseChannelAsync()
    {
        if (_model == null) return;
        try
        {
            if (_model.IsOpen)
                await _model.CloseAsync().ConfigureAwait(false);
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
