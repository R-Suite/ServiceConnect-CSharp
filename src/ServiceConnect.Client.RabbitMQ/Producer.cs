using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using System.Collections.Concurrent;
using System.Reflection;

namespace ServiceConnect.Client.RabbitMQ;

public class Producer : IProducer
{
    private readonly ITransportConfiguration _transportConfiguration;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly ILogger<Producer> _logger;
    private IModel? _model;
    private IConnection? _connection;
    private readonly object _lock = new();
    private ConnectionFactory? _connectionFactory;
    private readonly string[] _hosts;
    private readonly ushort _retryCount;
    private readonly ushort _retryTimeInSeconds;
    private readonly bool _publisherAcks;
    private readonly ConcurrentDictionary<ulong, string> _messagesSent = new();

    public Producer(ITransportConfiguration transportConfiguration, IQueueConfiguration queueConfiguration, ILogger<Producer> logger)
    {
        _transportConfiguration = transportConfiguration;
        _queueConfiguration = queueConfiguration;
        _logger = logger;
        MaximumMessageSize = transportConfiguration.ClientSettings.ContainsKey("MessageSize") ? Convert.ToInt64(_transportConfiguration.ClientSettings["MessageSize"]) : 65536;
        _publisherAcks = transportConfiguration.ClientSettings.ContainsKey("PublisherAcknowledgements") && Convert.ToBoolean(_transportConfiguration.ClientSettings["PublisherAcknowledgements"]);
        _hosts = transportConfiguration.Host.Split(',');
        _retryCount = transportConfiguration.ClientSettings.ContainsKey("RetryCount") ? Convert.ToUInt16((int)transportConfiguration.ClientSettings["RetryCount"]) : Convert.ToUInt16(60);
        _retryTimeInSeconds = transportConfiguration.ClientSettings.ContainsKey("RetrySeconds") ? Convert.ToUInt16((int)transportConfiguration.ClientSettings["RetrySeconds"]) : Convert.ToUInt16(10);

        Retry.Do(CreateConnection, ex =>
        {
            _logger.LogError(ex, "Error creating connection");
            DisposeConnection();
        }, new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
    }

    private void CreateConnection()
    {
        var port = _transportConfiguration.ClientSettings.ContainsKey("Port")
            ? Convert.ToInt32(_transportConfiguration.ClientSettings["Port"])
            : AmqpTcpEndpoint.UseDefaultPort;

        _connectionFactory = new ConnectionFactory
        {
            VirtualHost = "/",
            Port = port,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

        if (!string.IsNullOrEmpty(_transportConfiguration.Username))
        {
            _connectionFactory.UserName = _transportConfiguration.Username;
        }

        if (!string.IsNullOrEmpty(_transportConfiguration.Password))
        {
            _connectionFactory.Password = _transportConfiguration.Password;
        }

        if (_transportConfiguration.SslEnabled)
        {
            _connectionFactory.Ssl = SslConfigurationBuilder.BuildSslOptions(_transportConfiguration);
            _connectionFactory.Port = AmqpTcpEndpoint.DefaultAmqpSslPort;
        }

        if (!string.IsNullOrEmpty(_transportConfiguration.VirtualHost))
        {
            _connectionFactory.VirtualHost = _transportConfiguration.VirtualHost;
        }

        string producerName = Assembly.GetEntryAssembly() != null ? Assembly.GetEntryAssembly()!.GetName().Name! : System.Diagnostics.Process.GetCurrentProcess().ProcessName;

        _connection = _connectionFactory.CreateConnection(_hosts, producerName);
        _model = _connection.CreateModel();

        _model.ConfirmSelect();
        _model.BasicAcks += (o, e) => CleanOutstandingConfirms(e.DeliveryTag, e.Multiple);
        _model.BasicNacks += (o, e) =>
        {
            _logger.LogWarning("Message with delivery tag {DeliveryTag} was not acknowledged by the broker", e.DeliveryTag);
            CleanOutstandingConfirms(e.DeliveryTag, e.Multiple);
        };
    }

    public Task PublishAsync(Type type, byte[] message, Dictionary<string, string>? headers = null)
    {
        DoPublish(type, message, headers);
        return Task.CompletedTask;
    }

    private void DoPublish(Type type, byte[] message, Dictionary<string, string>? headers)
    {
        lock (_lock)
        {
            IBasicProperties basicProperties = _model!.CreateBasicProperties();

            Dictionary<string, object> messageHeaders = GetHeaders(type, headers, _queueConfiguration.QueueName, "Publish");

            Envelope envelope = new()
            {
                Body = message,
                Headers = messageHeaders
            };

            basicProperties.Headers = envelope.Headers;
            if (basicProperties.Headers != null && basicProperties.Headers.ContainsKey("MessageId"))
            {
                basicProperties.MessageId = basicProperties.Headers["MessageId"]?.ToString();
            }
            basicProperties.Persistent = true;
            if (envelope.Headers != null && envelope.Headers.ContainsKey("Priority"))
            {
                try
                {
                    basicProperties.Priority = Convert.ToByte(envelope.Headers["Priority"]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error setting message priority");
                }
            }

            string exchName = type.FullName!.Replace(".", string.Empty);
            string exchangeName = ConfigureExchange(exchName, "fanout");

            Retry.Do(() => ClientPublish(exchangeName, "", basicProperties, envelope.Body),
            ex =>
            {
                _logger.LogError(ex, "Error publishing message");
                DisposeConnection();
                RetryConnection();
            }, new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
        }
    }

    private void RetryConnection()
    {
        _logger.LogDebug("In Producer.RetryConnection()");
        CreateConnection();
    }

    public Task SendAsync(Type type, byte[] message, Dictionary<string, string>? headers = null)
    {
        lock (_lock)
        {
            IBasicProperties basicProperties = _model!.CreateBasicProperties();
            basicProperties.Persistent = true;
            if (headers != null && headers.ContainsKey("Priority"))
            {
                try
                {
                    basicProperties.Priority = Convert.ToByte(headers["Priority"]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error setting message priority");
                }
            }

            IList<string> endPoints = _queueConfiguration.QueueMappings[type.FullName!];

            foreach (string endPoint in endPoints)
            {
                Dictionary<string, object> messageHeaders = GetHeaders(type, headers, endPoint, "Send");

                basicProperties.Headers = messageHeaders;
                if (basicProperties.Headers != null && basicProperties.Headers.ContainsKey("MessageId"))
                {
                    basicProperties.MessageId = basicProperties.Headers["MessageId"]?.ToString();
                }

                Retry.Do(() => ClientPublish(string.Empty, endPoint, basicProperties, message),
                ex =>
                {
                    _logger.LogError(ex, "Error sending message");
                    DisposeConnection();
                    RetryConnection();
                },
                new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
            }
        }

        return Task.CompletedTask;
    }

    public Task SendAsync(string endPoint, Type type, byte[] message, Dictionary<string, string>? headers = null)
    {
        if (string.IsNullOrWhiteSpace(endPoint))
        {
            throw new ArgumentException($"Cannot send message of type {type} to empty endpoint");
        }

        lock (_lock)
        {
            IBasicProperties basicProperties = _model!.CreateBasicProperties();
            basicProperties.Persistent = true;
            if (headers != null && headers.ContainsKey("Priority"))
            {
                try
                {
                    basicProperties.Priority = Convert.ToByte(headers["Priority"]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error setting message priority");
                }
            }

            Dictionary<string, object> messageHeaders = GetHeaders(type, headers, endPoint, "Send");

            basicProperties.Headers = messageHeaders;
            if (basicProperties.Headers != null && basicProperties.Headers.ContainsKey("MessageId"))
            {
                basicProperties.MessageId = basicProperties.Headers["MessageId"]?.ToString();
            }

            Retry.Do(() => ClientPublish(string.Empty, endPoint, basicProperties, message),
            ex =>
            {
                _logger.LogError(ex, "Error sending message");
                DisposeConnection();
                RetryConnection();
            },
            new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
        }

        return Task.CompletedTask;
    }

    private Dictionary<string, object> GetHeaders(Type type, Dictionary<string, string>? headers, string queueName, string messageType)
    {
        headers ??= new Dictionary<string, string>();

        if (!headers.ContainsKey("DestinationAddress"))
        {
            headers["DestinationAddress"] = queueName;
        }

        if (!headers.ContainsKey("MessageId"))
        {
            headers["MessageId"] = Guid.NewGuid().ToString();
        }

        if (!headers.ContainsKey("MessageType"))
        {
            headers["MessageType"] = messageType;
        }

        headers["SourceAddress"] = _queueConfiguration.QueueName;
        headers["TimeSent"] = DateTime.UtcNow.ToString("O");
        headers["SourceMachine"] = Environment.MachineName;
        headers["TypeName"] = type.FullName!;
        headers["FullTypeName"] = type.AssemblyQualifiedName!;
        headers["ConsumerType"] = "RabbitMQ";
        headers["Language"] = "C#";

        return headers.ToDictionary(x => x.Key, x => (object)x.Value);
    }

    public Task DisconnectAsync()
    {
        _logger.LogDebug("In Producer.DisconnectAsync()");
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // Wait until all messages have been processed.
        int timeout = 0;
        while (_messagesSent.Count != 0 && timeout < 6000)
        {
            Thread.Sleep(100);
            timeout++;
        }

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
                _logger.LogWarning(ex, "Error disposing model");
            }
        }

        if (_connection != null)
        {
            try
            {
                _logger.LogDebug("Disposing connection");
                _connection.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing connection");
            }
            _connection = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public long MaximumMessageSize { get; }

    public Task SendBytesAsync(string endPoint, byte[] packet, Dictionary<string, string>? headers = null)
    {
        lock (_lock)
        {
            IBasicProperties basicProperties = _model!.CreateBasicProperties();
            basicProperties.Persistent = true;

            Dictionary<string, object> messageHeaders = GetHeaders(typeof(byte[]), headers, endPoint, "ByteStream");

            Envelope envelope = new()
            {
                Body = packet,
                Headers = messageHeaders
            };

            basicProperties.Headers = envelope.Headers;
            if (basicProperties.Headers != null && basicProperties.Headers.ContainsKey("MessageId"))
            {
                basicProperties.MessageId = basicProperties.Headers["MessageId"]?.ToString();
            }

            Retry.Do(() => ClientPublish(string.Empty, endPoint, basicProperties, envelope.Body),
            ex =>
            {
                _logger.LogError(ex, "Error sending message");
                DisposeConnection();
                RetryConnection();
            },
            new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
        }

        return Task.CompletedTask;
    }

    private void ClientPublish(string exchange, string routingKey, IBasicProperties basicProperties, byte[] message)
    {
        ulong sequenceNumber = _model!.NextPublishSeqNo;
        _ = _messagesSent.TryAdd(sequenceNumber, string.Empty);

        _model.BasicPublish(exchange, routingKey, basicProperties, message);

        if (_publisherAcks)
        {
            _ = _model.WaitForConfirms();
        }
    }

    private void CleanOutstandingConfirms(ulong sequenceNumber, bool multiple)
    {
        if (multiple)
        {
            IEnumerable<KeyValuePair<ulong, string>> confirmed = _messagesSent.Where(k => k.Key <= sequenceNumber);
            foreach (KeyValuePair<ulong, string> entry in confirmed)
            {
                _ = _messagesSent.TryRemove(entry.Key, out _);
            }
        }
        else
        {
            _ = _messagesSent.TryRemove(sequenceNumber, out _);
        }
    }

    private string ConfigureExchange(string exchangeName, string type)
    {
        try
        {
            _model!.ExchangeDeclare(exchangeName, type, true, false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring exchange - {Message}", ex.Message);
        }

        return exchangeName;
    }

    private void DisposeConnection()
    {
        try
        {
            if (_connection != null)
            {
                lock (_connection)
                {
                    if (_connection != null && _connection.IsOpen)
                    {
                        _connection.Close();
                        _connection.Dispose();
                        _connection = null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception trying to close connection");
        }

        try
        {
            if (_model != null)
            {
                lock (_model)
                {
                    if (_model != null && _model.IsOpen)
                    {
                        _model.Close();
                        _model.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception trying to close model");
        }
    }
}
