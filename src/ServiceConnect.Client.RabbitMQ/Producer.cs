using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using System.Reflection;

namespace ServiceConnect.Client.RabbitMQ;

public sealed class Producer : IProducer
{
    /// <summary>Default maximum message body size, in bytes (64 KiB).</summary>
    private const long DefaultMaxMessageSize = 64 * 1024;
    /// <summary>Default publish-retry attempt count.</summary>
    private const ushort DefaultRetryCount = 60;
    /// <summary>Default delay between publish retries, in seconds.</summary>
    private const ushort DefaultRetryTimeInSeconds = 10;

    private readonly ITransportConfiguration _transportConfiguration;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly IBusConfiguration _busConfiguration;
    private readonly ILogger<Producer> _logger;
    private volatile IChannel? _model;
    private IConnection? _connection;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private ConnectionFactory? _connectionFactory;
    private readonly string[] _hosts;
    private readonly ushort _retryCount;
    private readonly ushort _retryTimeInSeconds;
    private readonly bool _publisherAcks;
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
    private volatile bool _connected;
    private volatile bool _disposed;

    public Producer(ITransportConfiguration transportConfiguration, IQueueConfiguration queueConfiguration, IBusConfiguration busConfiguration, ILogger<Producer> logger)
    {
        _transportConfiguration = transportConfiguration;
        _queueConfiguration = queueConfiguration;
        _busConfiguration = busConfiguration ?? throw new ArgumentNullException(nameof(busConfiguration));
        _logger = logger;

        var settings = transportConfiguration.ClientSettings;
        MaximumMessageSize = GetSetting(settings, RabbitMQSettingKeys.MessageSize, DefaultMaxMessageSize, Convert.ToInt64);
        _publisherAcks = GetSetting(settings, RabbitMQSettingKeys.PublisherAcknowledgements, false, Convert.ToBoolean);
        _hosts = transportConfiguration.Host.Split(',');
        _retryCount = GetSetting(settings, RabbitMQSettingKeys.RetryCount, DefaultRetryCount, v => Convert.ToUInt16(v));
        _retryTimeInSeconds = GetSetting(settings, RabbitMQSettingKeys.RetrySeconds, DefaultRetryTimeInSeconds, v => Convert.ToUInt16(v));
    }

    private static T GetSetting<T>(IReadOnlyDictionary<string, object> settings, string key, T defaultValue, Func<object, T> converter)
    {
        return settings.TryGetValue(key, out var value) ? converter(value) : defaultValue;
    }

    private async Task EnsureConnectedAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected) return;

        await _connectionSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connected) return;

            await Retry.DoAsync(CreateConnectionAsync, async ex =>
            {
                _logger.LogError(ex, "Error creating connection");
                await DisposeConnectionAsync().ConfigureAwait(false);
            }, TimeSpan.FromSeconds(_retryTimeInSeconds), _retryCount).ConfigureAwait(false);

            _connected = true;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    private async Task CreateConnectionAsync()
    {
        _connectionFactory = ConnectionFactoryBuilder.Build(_transportConfiguration, heartbeatInterval: null);

        string producerName = Assembly.GetEntryAssembly()?.GetName().Name
            ?? System.Diagnostics.Process.GetCurrentProcess().ProcessName;

        _connection = await _connectionFactory.CreateConnectionAsync(_hosts, producerName).ConfigureAwait(false);

        if (_publisherAcks)
        {
            var channelOptions = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);
            _model = await _connection.CreateChannelAsync(channelOptions).ConfigureAwait(false);
        }
        else
        {
            _model = await _connection.CreateChannelAsync().ConfigureAwait(false);
        }
    }

    public async Task PublishAsync(Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureConnectedAsync().ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messageHeaders = GetHeaders(type, headers, _queueConfiguration.QueueName, "Publish");
            var basicProperties = CreateBasicProperties(messageHeaders);

            string exchangeName = await ConfigureExchangeAsync(type.FullName!.Replace(".", string.Empty), ExchangeType.Fanout).ConfigureAwait(false);
            await PublishWithRetryAsync(exchangeName, "", basicProperties, message, cancellationToken).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }

    public async Task SendAsync(Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureConnectedAsync().ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_queueConfiguration.TryGetQueueMapping(type, out IReadOnlyList<string>? endPoints))
                throw new InvalidOperationException($"No queue mapping configured for message type '{type.FullName}'. Register a mapping via AddQueueMapping.");

            foreach (string endPoint in endPoints)
            {
                var messageHeaders = GetHeaders(type, headers, endPoint, "Send");
                var basicProperties = CreateBasicProperties(messageHeaders);
                await PublishWithRetryAsync(string.Empty, endPoint, basicProperties, message, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _publishLock.Release(); }
    }

    public async Task SendAsync(string endPoint, Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(endPoint))
            throw new ArgumentException($"Cannot send message of type {type} to empty endpoint");

        await EnsureConnectedAsync().ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messageHeaders = GetHeaders(type, headers, endPoint, "Send");
            var basicProperties = CreateBasicProperties(messageHeaders);
            await PublishWithRetryAsync(string.Empty, endPoint, basicProperties, message, cancellationToken).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }

    public async Task SendBytesAsync(string endPoint, byte[] packet, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureConnectedAsync().ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messageHeaders = GetHeaders(typeof(byte[]), headers, endPoint, HeaderKeys.ByteStream);
            var basicProperties = CreateBasicProperties(messageHeaders);
            await PublishWithRetryAsync(string.Empty, endPoint, basicProperties, packet, cancellationToken).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("In Producer.DisconnectAsync()");
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisposeAsyncCore().ConfigureAwait(false);
    }

    private async Task DisposeAsyncCore()
    {
        await DisposeModelAsync().ConfigureAwait(false);
        await DisposeConnectionInstanceAsync().ConfigureAwait(false);
        _publishLock.Dispose();
        _connectionSemaphore.Dispose();
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

    private async Task PublishWithRetryAsync(string exchange, string routingKey, BasicProperties basicProperties, byte[] message, CancellationToken cancellationToken = default)
    {
        await _model!.BasicPublishAsync(exchange, routingKey, mandatory: false, basicProperties, (ReadOnlyMemory<byte>)message, cancellationToken).ConfigureAwait(false);
    }

    private Dictionary<string, object> GetHeaders(Type type, Dictionary<string, string>? headers, string queueName, string messageType)
    {
        // Build the final object-valued dictionary directly rather than populating a
        // string-valued copy and then rewriting it (P-28). Pre-sized to the maximum
        // number of stamped keys + any caller-provided entries.
        var callerCount = headers?.Count ?? 0;
        var result = new Dictionary<string, object>(callerCount + 11);

        if (headers is not null)
        {
            foreach (var kvp in headers)
                result[kvp.Key] = kvp.Value;
        }

        if (!result.ContainsKey(HeaderKeys.DestinationAddress))
            result[HeaderKeys.DestinationAddress] = queueName;
        if (!result.ContainsKey(HeaderKeys.MessageId))
            result[HeaderKeys.MessageId] = Guid.NewGuid().ToString();
        if (!result.ContainsKey(HeaderKeys.MessageType))
            result[HeaderKeys.MessageType] = messageType;

        result[HeaderKeys.SourceAddress] = _queueConfiguration.QueueName;
        result[HeaderKeys.TimeSent] = DateTime.UtcNow.ToString("O");
        if (_busConfiguration.IncludeMachineNameInHeaders)
            result[HeaderKeys.SourceMachine] = Environment.MachineName;

        if (!result.ContainsKey(HeaderKeys.TypeName))
            result[HeaderKeys.TypeName] = type.FullName!;
        if (!result.ContainsKey(HeaderKeys.FullTypeName))
            result[HeaderKeys.FullTypeName] = type.AssemblyQualifiedName!;

        result[HeaderKeys.ConsumerType] = "RabbitMQ";
        result[HeaderKeys.Language] = "C#";

        return result;
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

    private async Task DisposeConnectionAsync()
    {
        await _connectionSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                if (_connection != null && _connection.IsOpen)
                {
                    await _connection.CloseAsync().ConfigureAwait(false);
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
                    await _model.CloseAsync().ConfigureAwait(false);
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
        finally
        {
            _connectionSemaphore.Release();
        }
    }
}
