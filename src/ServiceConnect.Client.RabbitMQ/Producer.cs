using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using System.Reflection;

namespace ServiceConnect.Client.RabbitMQ;

public sealed class Producer : IProducer
{
    private const long DefaultMaxMessageSize = 65536;
    private const ushort DefaultRetryCount = 60;
    private const ushort DefaultRetryTimeInSeconds = 10;

    private readonly ITransportConfiguration _transportConfiguration;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly ILogger<Producer> _logger;
    private volatile IChannel? _model;
    private IConnection? _connection;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private ConnectionFactory? _connectionFactory;
    private readonly string[] _hosts;
    private readonly ushort _retryCount;
    private readonly ushort _retryTimeInSeconds;
    private readonly bool _publisherAcks;
#if NET9_0_OR_GREATER
    private readonly Lock _connectionLock = new();
#else
    private readonly object _connectionLock = new();
#endif
    private volatile bool _connected;
    private volatile bool _disposed;

    public Producer(ITransportConfiguration transportConfiguration, IQueueConfiguration queueConfiguration, ILogger<Producer> logger)
    {
        _transportConfiguration = transportConfiguration;
        _queueConfiguration = queueConfiguration;
        _logger = logger;

        var settings = transportConfiguration.ClientSettings;
        MaximumMessageSize = GetSetting(settings, RabbitMQSettingKeys.MessageSize, DefaultMaxMessageSize, Convert.ToInt64);
        _publisherAcks = GetSetting(settings, RabbitMQSettingKeys.PublisherAcknowledgements, false, Convert.ToBoolean);
        _hosts = transportConfiguration.Host.Split(',');
        _retryCount = GetSetting(settings, RabbitMQSettingKeys.RetryCount, DefaultRetryCount, v => Convert.ToUInt16(v));
        _retryTimeInSeconds = GetSetting(settings, RabbitMQSettingKeys.RetrySeconds, DefaultRetryTimeInSeconds, v => Convert.ToUInt16(v));
    }

    private static T GetSetting<T>(IDictionary<string, object> settings, string key, T defaultValue, Func<object, T> converter)
    {
        return settings.TryGetValue(key, out var value) ? converter(value) : defaultValue;
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected) return;

        lock (_connectionLock)
        {
            if (_connected) return;

            Retry.Do(CreateConnection, ex =>
            {
                _logger.LogError(ex, "Error creating connection");
                DisposeConnection();
            }, TimeSpan.FromSeconds(_retryTimeInSeconds), _retryCount);

            _connected = true;
        }
    }

    private void CreateConnection()
    {
        var port = _transportConfiguration.ClientSettings.TryGetValue(RabbitMQSettingKeys.Port, out var portVal)
            ? Convert.ToInt32(portVal)
            : AmqpTcpEndpoint.UseDefaultPort;

        _connectionFactory = new ConnectionFactory
        {
            VirtualHost = "/",
            Port = port,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

        if (!string.IsNullOrEmpty(_transportConfiguration.Username))
            _connectionFactory.UserName = _transportConfiguration.Username;

        if (!string.IsNullOrEmpty(_transportConfiguration.Password))
            _connectionFactory.Password = _transportConfiguration.Password;

        if (_transportConfiguration.SslEnabled)
        {
            _connectionFactory.Ssl = SslConfigurationBuilder.BuildSslOptions(_transportConfiguration);
            _connectionFactory.Port = AmqpTcpEndpoint.DefaultAmqpSslPort;
        }

        if (!string.IsNullOrEmpty(_transportConfiguration.VirtualHost))
            _connectionFactory.VirtualHost = _transportConfiguration.VirtualHost;

        string producerName = Assembly.GetEntryAssembly()?.GetName().Name
            ?? System.Diagnostics.Process.GetCurrentProcess().ProcessName;

        _connection = _connectionFactory.CreateConnectionAsync(_hosts, producerName).GetAwaiter().GetResult();

        if (_publisherAcks)
        {
            var channelOptions = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);
            _model = _connection.CreateChannelAsync(channelOptions).GetAwaiter().GetResult();
        }
        else
        {
            _model = _connection.CreateChannelAsync().GetAwaiter().GetResult();
        }
    }

    public async Task PublishAsync(Type type, byte[] message, Dictionary<string, string>? headers = null)
    {
        EnsureConnected();
        await _publishLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var messageHeaders = GetHeaders(type, headers, _queueConfiguration.QueueName, "Publish");
            var basicProperties = CreateBasicProperties(messageHeaders);

            string exchangeName = await ConfigureExchangeAsync(type.FullName!.Replace(".", string.Empty), "fanout").ConfigureAwait(false);
            await PublishWithRetryAsync(exchangeName, "", basicProperties, message).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }

    public async Task SendAsync(Type type, byte[] message, Dictionary<string, string>? headers = null)
    {
        EnsureConnected();
        await _publishLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_queueConfiguration.QueueMappings.TryGetValue(type.FullName!, out IList<string>? endPoints))
                throw new InvalidOperationException($"No queue mapping configured for message type '{type.FullName}'. Register a mapping via QueueMappings.");

            foreach (string endPoint in endPoints)
            {
                var messageHeaders = GetHeaders(type, headers, endPoint, "Send");
                var basicProperties = CreateBasicProperties(messageHeaders);
                await PublishWithRetryAsync(string.Empty, endPoint, basicProperties, message).ConfigureAwait(false);
            }
        }
        finally { _publishLock.Release(); }
    }

    public async Task SendAsync(string endPoint, Type type, byte[] message, Dictionary<string, string>? headers = null)
    {
        if (string.IsNullOrWhiteSpace(endPoint))
            throw new ArgumentException($"Cannot send message of type {type} to empty endpoint");

        EnsureConnected();
        await _publishLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var messageHeaders = GetHeaders(type, headers, endPoint, "Send");
            var basicProperties = CreateBasicProperties(messageHeaders);
            await PublishWithRetryAsync(string.Empty, endPoint, basicProperties, message).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }

    public async Task SendBytesAsync(string endPoint, byte[] packet, Dictionary<string, string>? headers = null)
    {
        EnsureConnected();
        await _publishLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var messageHeaders = GetHeaders(typeof(byte[]), headers, endPoint, HeaderKeys.ByteStream);
            var basicProperties = CreateBasicProperties(messageHeaders);
            await PublishWithRetryAsync(string.Empty, endPoint, basicProperties, packet).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }

    public Task DisconnectAsync()
    {
        _logger.LogDebug("In Producer.DisconnectAsync()");
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_connectionLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        await DisposeModelAsync();
        await DisposeConnectionInstanceAsync();
    }

    public long MaximumMessageSize { get; }

    private BasicProperties CreateBasicProperties(Dictionary<string, object> messageHeaders)
    {
        var basicProperties = new BasicProperties
        {
            Headers = new Dictionary<string, object?>(messageHeaders.Select(kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value))),
            Persistent = true
        };

        if (messageHeaders.TryGetValue(HeaderKeys.MessageId, out var messageId))
            basicProperties.MessageId = messageId?.ToString();

        if (messageHeaders.TryGetValue(HeaderKeys.Priority, out var priority))
        {
            try
            {
                basicProperties.Priority = Convert.ToByte(priority);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting message priority");
            }
        }

        return basicProperties;
    }

    private async Task PublishWithRetryAsync(string exchange, string routingKey, BasicProperties basicProperties, byte[] message)
    {
        await _model!.BasicPublishAsync(exchange, routingKey, mandatory: false, basicProperties, (ReadOnlyMemory<byte>)message).ConfigureAwait(false);
    }

    private Dictionary<string, object> GetHeaders(Type type, Dictionary<string, string>? headers, string queueName, string messageType)
    {
        headers ??= new Dictionary<string, string>();

        if (!headers.ContainsKey(HeaderKeys.DestinationAddress))
            headers[HeaderKeys.DestinationAddress] = queueName;

        if (!headers.ContainsKey(HeaderKeys.MessageId))
            headers[HeaderKeys.MessageId] = Guid.NewGuid().ToString();

        if (!headers.ContainsKey(HeaderKeys.MessageType))
            headers[HeaderKeys.MessageType] = messageType;

        headers[HeaderKeys.SourceAddress] = _queueConfiguration.QueueName;
        headers[HeaderKeys.TimeSent] = DateTime.UtcNow.ToString("O");
        headers[HeaderKeys.SourceMachine] = Environment.MachineName;

        if (!headers.ContainsKey(HeaderKeys.TypeName))
            headers[HeaderKeys.TypeName] = type.FullName!;
        if (!headers.ContainsKey(HeaderKeys.FullTypeName))
            headers[HeaderKeys.FullTypeName] = type.AssemblyQualifiedName!;

        headers[HeaderKeys.ConsumerType] = "RabbitMQ";
        headers[HeaderKeys.Language] = "C#";

        return headers.ToDictionary(x => x.Key, x => (object)x.Value);
    }

    private async Task<string> ConfigureExchangeAsync(string exchangeName, string type)
    {
        try
        {
            await _model!.ExchangeDeclareAsync(exchangeName, type, true, false, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring exchange - {Message}", ex.Message);
        }

        return exchangeName;
    }

    private async Task DisposeModelAsync()
    {
        if (_model != null)
        {
            try
            {
                _logger.LogDebug("Disposing Model");
                if (_model.IsOpen)
                    await _model.CloseAsync();
                _model.Dispose();
                _model = null;
            }
            catch (ObjectDisposedException) { _model = null; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing model");
                _model = null;
            }
        }
    }

    private async Task DisposeConnectionInstanceAsync()
    {
        if (_connection != null)
        {
            try
            {
                _logger.LogDebug("Disposing connection");
                if (_connection.IsOpen)
                    await _connection.CloseAsync();
                _connection.Dispose();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing connection");
            }
            _connection = null;
        }
    }

    private void DisposeConnection()
    {
        lock (_connectionLock)
        {
            try
            {
                if (_connection != null && _connection.IsOpen)
                {
                    _connection.CloseAsync().GetAwaiter().GetResult();
                    _connection.Dispose();
                    _connection = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception trying to close connection");
            }

            try
            {
                if (_model != null && _model.IsOpen)
                {
                    _model.CloseAsync().GetAwaiter().GetResult();
                    _model.Dispose();
                    _model = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception trying to close model");
            }

            _connected = false;
        }
    }
}
