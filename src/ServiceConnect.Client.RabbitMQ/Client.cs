using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

public class Client
{
    private IModel? _model;
    private readonly IServiceConnectConnection _connection;
    private ConsumerEventHandler? _consumerEventHandler;
    private readonly ITransportConfiguration _transportConfiguration;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly ILogger _logger;

    private bool _autoDelete;
    private string _queueName = "";
    private readonly int _maxRetries;
    private readonly ushort _retryCount;
    private readonly bool _errorsDisabled;
    private readonly ushort _prefetchCount;
    private readonly bool _disablePrefetch;
    private readonly ushort _retryTimeInSeconds;
    private readonly IDictionary<string, object> _queueArguments;
    private string _retryQueueName = "";
    private string _errorExchange = "";
    private string _auditExchange = "";

    private int _messagesBeingProcessed;
    private AsyncEventingBasicConsumer? _consumer;

    public Client(IServiceConnectConnection connection, ITransportConfiguration transportConfiguration, IQueueConfiguration queueConfiguration, ILogger logger)
    {
        _connection = connection;
        _transportConfiguration = transportConfiguration;
        _queueConfiguration = queueConfiguration;
        _logger = logger;

        _maxRetries = transportConfiguration.MaxRetries;
        _autoDelete = transportConfiguration.ClientSettings.ContainsKey("AutoDelete") && (bool)transportConfiguration.ClientSettings["AutoDelete"];
        _errorsDisabled = queueConfiguration.DisableErrors;
        _prefetchCount = transportConfiguration.ClientSettings.ContainsKey("PrefetchCount") ? Convert.ToUInt16((int)transportConfiguration.ClientSettings["PrefetchCount"]) : transportConfiguration.PrefetchCount;
        _disablePrefetch = transportConfiguration.ClientSettings.ContainsKey("DisablePrefetch") && (bool)transportConfiguration.ClientSettings["DisablePrefetch"];
        _retryCount = transportConfiguration.ClientSettings.ContainsKey("RetryCount") ? Convert.ToUInt16((int)transportConfiguration.ClientSettings["RetryCount"]) : Convert.ToUInt16(60);
        _retryTimeInSeconds = transportConfiguration.ClientSettings.ContainsKey("RetrySeconds") ? Convert.ToUInt16((int)transportConfiguration.ClientSettings["RetrySeconds"]) : Convert.ToUInt16(10);
        _queueArguments = transportConfiguration.ClientSettings.ContainsKey("Arguments") ? (IDictionary<string, object>)transportConfiguration.ClientSettings["Arguments"] : new Dictionary<string, object>();
    }

    /// <summary>
    /// Event fired on HandleBasicDeliver
    /// </summary>
    public async Task Event(object consumer, BasicDeliverEventArgs args)
    {
        try
        {
            Interlocked.Increment(ref _messagesBeingProcessed);

            if (args.BasicProperties.Headers == null ||
                (!args.BasicProperties.Headers.ContainsKey("TypeName") &&
                 !args.BasicProperties.Headers.ContainsKey("FullTypeName")))
            {
                const string errMsg = "Error processing message, Message headers must contain type name.";
                _logger.LogError(errMsg);
                return;
            }

            if (args.Redelivered)
            {
                SetHeader(args.BasicProperties.Headers, "Redelivered", true);
            }

            await ProcessMessage(args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
            throw;
        }
        finally
        {
            try
            {
                _model!.BasicAck(args.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error acking the message");
            }

            Interlocked.Decrement(ref _messagesBeingProcessed);
        }
    }

    private async Task ProcessMessage(BasicDeliverEventArgs args)
    {
        ConsumeEventResult result;
        IDictionary<string, object> headers = args.BasicProperties.Headers;

        try
        {
            SetHeader(args.BasicProperties.Headers, "TimeReceived", DateTime.UtcNow.ToString("O"));
            SetHeader(args.BasicProperties.Headers, "DestinationMachine", Environment.MachineName);
            SetHeader(args.BasicProperties.Headers, "DestinationAddress", _queueConfiguration.QueueName);

            string typeName = Encoding.UTF8.GetString((byte[])(headers.ContainsKey("FullTypeName") ? headers["FullTypeName"] : headers["TypeName"]));

            result = await _consumerEventHandler!(args.Body.ToArray(), typeName, headers);

            SetHeader(args.BasicProperties.Headers, "TimeProcessed", DateTime.UtcNow.ToString("O"));
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

            if (args.BasicProperties.Headers.ContainsKey("RetryCount"))
            {
                retryCount = (int)args.BasicProperties.Headers["RetryCount"];
            }

            if (retryCount < _maxRetries)
            {
                retryCount++;
                SetHeader(args.BasicProperties.Headers, "RetryCount", retryCount);

                _model!.BasicPublish(string.Empty, _retryQueueName, args.BasicProperties, args.Body);
            }
            else
            {
                if (result.Exception != null)
                {
                    string jsonException = string.Empty;
                    try
                    {
                        jsonException = JsonConvert.SerializeObject(result.Exception);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error serializing exception");
                    }

                    SetHeader(args.BasicProperties.Headers, "Exception", JsonConvert.SerializeObject(new
                    {
                        TimeStamp = DateTime.Now,
                        ExceptionType = result.Exception.GetType().FullName,
                        Message = GetErrorMessage(result.Exception),
                        result.Exception.StackTrace,
                        result.Exception.Source,
                        Exception = jsonException
                    }));
                }

                _logger.LogError("Max number of retries exceeded. MessageId: {MessageId}", args.BasicProperties.MessageId);
                _model!.BasicPublish(_errorExchange, string.Empty, args.BasicProperties, args.Body);
            }
        }
        else if (!_errorsDisabled)
        {
            string? messageType = null;
            if (headers.ContainsKey("MessageType"))
            {
                messageType = Encoding.UTF8.GetString((byte[])headers["MessageType"]);
            }

            if (_queueConfiguration.AuditingEnabled && messageType != "ByteStream")
            {
                _model!.BasicPublish(_auditExchange, string.Empty, args.BasicProperties, args.Body);
            }
        }
    }

    public void StartConsuming(ConsumerEventHandler messageReceived, string queueName, bool? exclusive = null, bool? autoDelete = null)
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

        Retry.Do(CreateConsumer, ex =>
        {
            _logger.LogError(ex, "Error creating model - queueName: {QueueName}", queueName);
        }, new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
    }

    private void CreateConsumer()
    {
        _model = _connection.CreateModel();

        if (!_disablePrefetch)
        {
            _model.BasicQos(0, _prefetchCount, false);
        }

        _consumer = new AsyncEventingBasicConsumer(_model);
        _consumer.Received += Event;

        _ = _model.BasicConsume(_queueName, false, _consumer);

        _logger.LogDebug("Started consuming");
    }

    public void ConsumeMessageType(string messageTypeName)
    {
        // messageTypeName is the name of the exchange
        _model!.QueueBind(_queueName, messageTypeName, string.Empty, _queueArguments);
    }

    private string GetErrorMessage(Exception exception)
    {
        StringBuilder sbMessage = new();
        _ = sbMessage.Append(exception.Message + Environment.NewLine);
        Exception? ie = exception.InnerException;
        while (ie != null)
        {
            _ = sbMessage.Append(ie.Message + Environment.NewLine);
            ie = ie.InnerException;
        }

        return sbMessage.ToString();
    }

    private static void SetHeader<T>(IDictionary<string, object> headers, string key, T value)
    {
        if (Equals(value, default(T)))
        {
            _ = headers.Remove(key);
        }
        else
        {
            headers[key] = value!;
        }
    }

    public void StopConsuming()
    {
        Dispose();
    }

    public void Dispose()
    {
        // Stop consuming
        if (_consumer != null)
        {
            foreach (string tag in _consumer.ConsumerTags)
            {
                try
                {
                    _model!.BasicCancel(tag);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error cancelling consumer");
                }
            }
        }

        // Wait until all messages have been processed.
        int timeout = 0;
        while (_messagesBeingProcessed > 0 && timeout < 6000)
        {
            Thread.Sleep(100);
            timeout++;
        }

        if (_autoDelete && _model != null)
        {
            _logger.LogDebug("Deleting retry queue");
            _ = _model.QueueDelete(_queueName + ".Retries");
        }

        // Dispose model
        if (_model != null)
        {
            try
            {
                _logger.LogDebug("Disposing Model");
                _model.Dispose();
                _model = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing consumer");
            }
        }
    }
}
