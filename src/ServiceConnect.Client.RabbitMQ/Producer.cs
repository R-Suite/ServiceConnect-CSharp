using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using System.Collections.Concurrent;
using System.Reflection;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// RabbitMQ-backed implementation of <see cref="IProducer"/> for publishing and sending messages.
/// </summary>
public sealed class Producer : IProducer
{
    /// <summary>Default maximum message body size, in bytes (64 KiB).</summary>
    private const long DefaultMaxMessageSize = 64 * 1024;
    private const int StampedHeaderCount = 11;
    /// <summary>Default publish-retry attempt count.</summary>
    private const ushort DefaultRetryCount = 60;
    /// <summary>Default delay between publish retries, in seconds.</summary>
    private const ushort DefaultRetryTimeInSeconds = 10;

    // Cache process/assembly name — computed once at startup, reused on every reconnect.
    private static readonly string ProducerName = Assembly.GetEntryAssembly()?.GetName().Name
        ?? System.Diagnostics.Process.GetCurrentProcess().ProcessName;

    // Cache (FullName, AssemblyQualifiedName) per Type — these are constant for a given Type.
    private static readonly ConcurrentDictionary<Type, (string FullName, string AQN)> _typeNameCache = new();

    // Cache the computed exchange name (FullName with dots stripped) per FullName string.
    private readonly ConcurrentDictionary<string, string> _exchangeNameCache = new();
    // Track which exchange names have already been declared on the current connection.
    //        Cleared on reconnect because exchange state is per-connection.
    private readonly ConcurrentDictionary<string, bool> _declaredExchanges = new();

    private readonly ITransportConfiguration _transportConfiguration;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly IBusConfiguration _busConfiguration;
    private readonly ILogger<Producer> _logger;
    private readonly TimeProvider _timeProvider;
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
    private int _disposedInt;
    internal Func<CancellationToken, Task>? ReconnectForTests;
    internal Func<ConnectionFactory, string[], string, CancellationToken, Task<IConnection>>? CreateConnectionForTests;

    /// <summary>
    /// Initializes a new producer instance using the supplied ServiceConnect configuration.
    /// </summary>
    /// <param name="transportConfiguration">Transport settings used to configure RabbitMQ connectivity and retries.</param>
    /// <param name="queueConfiguration">Queue settings used when stamping message headers and resolving queue mappings.</param>
    /// <param name="busConfiguration">Bus settings that control emitted message headers.</param>
    /// <param name="logger">The logger used for producer lifecycle and retry logging.</param>
    /// <param name="timeProvider">An optional time provider used when stamping outbound message headers.</param>
    public Producer(ITransportConfiguration transportConfiguration, IQueueConfiguration queueConfiguration, IBusConfiguration busConfiguration, ILogger<Producer> logger, TimeProvider? timeProvider = null)
    {
        _transportConfiguration = transportConfiguration;
        _queueConfiguration = queueConfiguration;
        _busConfiguration = busConfiguration ?? throw new ArgumentNullException(nameof(busConfiguration));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

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

    private Task EnsureConnectedAsync()
    {
        return EnsureConnectedAsync(CancellationToken.None);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposedInt != 0, this);
        if (_connected) return;

        await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected) return;

            await Retry.DoAsync(() => CreateConnectionAsync(cancellationToken), async ex =>
            {
                _logger.LogError(ex, "Error creating connection");
                await TearDownChannelAndConnectionAsync().ConfigureAwait(false);
            }, TimeSpan.FromSeconds(_retryTimeInSeconds), _retryCount, cancellationToken).ConfigureAwait(false);

            _connected = true;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    private async Task CreateConnectionAsync(CancellationToken cancellationToken)
    {
        _connectionFactory = ConnectionFactoryBuilder.Build(_transportConfiguration);

        // Exchange declarations are per-connection — reset the cache on every (re)connect.
        _declaredExchanges.Clear();

        IConnection? connection = null;
        IChannel? model = null;

        try
        {
            if (CreateConnectionForTests != null)
            {
                connection = await CreateConnectionForTests(_connectionFactory, _hosts, ProducerName, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                connection = await _connectionFactory.CreateConnectionAsync(_hosts, ProducerName, cancellationToken).ConfigureAwait(false);
            }

            if (_publisherAcks)
            {
                var channelOptions = new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true);
                model = await connection.CreateChannelAsync(channelOptions, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                model = await connection.CreateChannelAsync(null, cancellationToken).ConfigureAwait(false);
            }

            _connection = connection;
            _model = model;
        }
        catch
        {
            await DisposeModelAsync(model).ConfigureAwait(false);
            await DisposeConnectionInstanceAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ExecuteWithConnectionRetryAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await Retry.DoAsync(
                action,
                ex => ReconnectAfterPublishFailureAsync(ex, cancellationToken),
                TimeSpan.FromSeconds(_retryTimeInSeconds),
                _retryCount,
                IsRetriablePublishException,
                cancellationToken).ConfigureAwait(false);
        }
        catch (global::RabbitMQ.Client.Exceptions.PublishException pex)
        {
            _logger.LogWarning(pex, "Broker nacked publish: {Reason}", pex.Message);
            throw;
        }
    }

    // Broker-side nacks (PublishException) are usually poison messages — rejected by a
    // policy (e.g. max-length, unroutable, access denied). Retrying them burns the entire
    // retry budget against a condition that will not heal, and worse, triggers a reconnect
    // loop that tears down the connection for a publish-layer error. Only transport-level
    // failures should flow into the reconnect-retry path.
    private static bool IsRetriablePublishException(Exception ex)
    {
        if (ex is global::RabbitMQ.Client.Exceptions.PublishException) return false;
        if (ex is OperationCanceledException) return false;
        return true;
    }

    private async Task ReconnectAfterPublishFailureAsync(Exception ex, CancellationToken cancellationToken)
    {
        _logger.LogError(ex, "Error publishing message");

        await DisposeConnectionAsync().ConfigureAwait(false);
        _declaredExchanges.Clear();

        if (ReconnectForTests != null)
        {
            await ReconnectForTests(cancellationToken).ConfigureAwait(false);
            return;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes a message to the exchange derived from the specified message type.
    /// </summary>
    /// <param name="type">The logical message type used to determine the publish exchange and stamped headers.</param>
    /// <param name="message">The serialized message body.</param>
    /// <param name="headers">Optional custom headers to include with the message.</param>
    /// <param name="cancellationToken">A token used to cancel the publish operation.</param>
    public async Task PublishAsync(Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message.Length > MaximumMessageSize)
            throw new InvalidOperationException(
                $"Message size {message.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messageHeaders = GetHeaders(type, headers, _queueConfiguration.QueueName, "Publish");
            var basicProperties = CreateBasicProperties(messageHeaders);

            // Compute the exchange name once per type and cache it.
            // Only issue ExchangeDeclareAsync once per connection — skip on subsequent publishes.
            string exchangeName = _exchangeNameCache.GetOrAdd(type.AssemblyQualifiedName ?? type.FullName!, _ => ServiceConnect.Services.MessageTypeExchangeName.From(type));

            await ExecuteWithConnectionRetryAsync(async () =>
            {
                if (!_declaredExchanges.ContainsKey(exchangeName))
                    await ConfigureExchangeAsync(exchangeName, ExchangeType.Fanout, cancellationToken).ConfigureAwait(false);

                await _model!.BasicPublishAsync(
                    exchangeName,
                    string.Empty,
                    false,
                    basicProperties,
                    (ReadOnlyMemory<byte>)message,
                    cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }

    /// <summary>
    /// Sends a message to each endpoint mapped to the specified message type.
    /// </summary>
    /// <param name="type">The logical message type used to resolve destination queues.</param>
    /// <param name="message">The serialized message body.</param>
    /// <param name="headers">Optional custom headers to include with the message.</param>
    /// <param name="cancellationToken">A token used to cancel the send operation.</param>
    public async Task SendAsync(Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message.Length > MaximumMessageSize)
            throw new InvalidOperationException(
                $"Message size {message.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_queueConfiguration.TryGetQueueMapping(type, out IReadOnlyList<string>? endPoints))
                throw new InvalidOperationException($"No queue mapping configured for message type '{type.FullName}'. Register a mapping via AddQueueMapping.");

            // Build base headers once outside the loop; only DestinationAddress varies per endpoint.
            var baseHeaders = GetHeaders(type, headers, string.Empty, "Send");
            foreach (string endPoint in endPoints)
            {
                baseHeaders[HeaderKeys.DestinationAddress] = endPoint;
                var basicProperties = CreateBasicProperties(baseHeaders);
                await ExecuteWithConnectionRetryAsync(
                    () => _model!.BasicPublishAsync(
                        string.Empty,
                        endPoint,
                        false,
                        basicProperties,
                        (ReadOnlyMemory<byte>)message,
                        cancellationToken).AsTask(),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _publishLock.Release(); }
    }

    /// <summary>
    /// Sends a message directly to the specified endpoint.
    /// </summary>
    /// <param name="endPoint">The destination queue name.</param>
    /// <param name="type">The logical message type used when stamping headers.</param>
    /// <param name="message">The serialized message body.</param>
    /// <param name="headers">Optional custom headers to include with the message.</param>
    /// <param name="cancellationToken">A token used to cancel the send operation.</param>
    public async Task SendAsync(string endPoint, Type type, byte[] message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(endPoint))
            throw new ArgumentException($"Cannot send message of type {type} to empty endpoint");
        if (message.Length > MaximumMessageSize)
            throw new InvalidOperationException(
                $"Message size {message.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messageHeaders = GetHeaders(type, headers, endPoint, "Send");
            var basicProperties = CreateBasicProperties(messageHeaders);
            await ExecuteWithConnectionRetryAsync(
                () => _model!.BasicPublishAsync(
                    string.Empty,
                    endPoint,
                    false,
                    basicProperties,
                    (ReadOnlyMemory<byte>)message,
                    cancellationToken).AsTask(),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }

    /// <summary>
    /// Sends raw bytes directly to the specified endpoint.
    /// </summary>
    /// <param name="endPoint">The destination queue name.</param>
    /// <param name="type">The logical message type the packet represents; used to stamp the reserved type headers authoritatively.</param>
    /// <param name="packet">The raw payload to send.</param>
    /// <param name="headers">Optional custom headers to include with the packet.</param>
    /// <param name="cancellationToken">A token used to cancel the send operation.</param>
    public async Task SendBytesAsync(string endPoint, Type type, byte[] packet, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(endPoint))
            throw new ArgumentException($"Cannot send packet of type {type} to empty endpoint");
        if (packet.Length > MaximumMessageSize)
            throw new InvalidOperationException(
                $"Message size {packet.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var messageHeaders = GetHeaders(type, headers, endPoint, HeaderKeys.ByteStream);
            var basicProperties = CreateBasicProperties(messageHeaders);
            await ExecuteWithConnectionRetryAsync(
                () => _model!.BasicPublishAsync(
                    string.Empty,
                    endPoint,
                    false,
                    basicProperties,
                    (ReadOnlyMemory<byte>)packet,
                    cancellationToken).AsTask(),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }

    /// <summary>
    /// Disconnects the producer and releases its RabbitMQ resources.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the disconnect request before disposal begins.</param>
    [Obsolete("Use DisposeAsync instead.")]
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug("In Producer.DisconnectAsync()");
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the producer's RabbitMQ channel, connection, and synchronization primitives.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposedInt, 1) != 0) return;

        // Wait for in-flight publishes and (re)connections to complete before tearing down
        // the channel/connection. Without this, a publisher that held the lock during
        // dispose would touch a disposed IChannel and throw ObjectDisposedException mid-publish.
        //
        // Use a bounded timeout so a stuck publish cannot block dispose indefinitely —
        // after the timeout we proceed with tear-down and any remaining publisher will
        // observe the disposed state via their normal exception path.
        var disposeTimeout = TimeSpan.FromSeconds(30);
        var publishLockAcquired = false;
        var connectionLockAcquired = false;
        try
        {
            publishLockAcquired = await _publishLock.WaitAsync(disposeTimeout).ConfigureAwait(false);
            connectionLockAcquired = await _connectionSemaphore.WaitAsync(disposeTimeout).ConfigureAwait(false);

            // If we couldn't acquire both locks, a publisher is still in-flight on the channel.
            // Tearing down now would crash it with ObjectDisposedException. Skip tear-down and
            // accept the resource leak — the GC will reclaim the channel/connection eventually.
            // Leaving the semaphores undisposed is also deliberate: disposing one whose waiter
            // hasn't returned yet would throw on that waiter's Release() call.
            if (publishLockAcquired && connectionLockAcquired)
            {
                await TearDownChannelAndConnectionAsync().ConfigureAwait(false);
            }
            else
            {
                _logger.LogError(
                    "Producer dispose timed out waiting for locks (publish={PublishAcquired}, connection={ConnectionAcquired}); skipping teardown to avoid crashing in-flight publishers.",
                    publishLockAcquired, connectionLockAcquired);
                return;
            }
        }
        finally
        {
            if (connectionLockAcquired) _connectionSemaphore.Release();
            if (publishLockAcquired) _publishLock.Release();
        }

        _publishLock.Dispose();
        _connectionSemaphore.Dispose();
    }

    /// <summary>
    /// Gets the maximum allowed outbound message size, in bytes.
    /// </summary>
    public long MaximumMessageSize { get; }

    // Avoid StringBuilder allocation inside DateTime.ToString("O").
    private static string FormatTimestamp(DateTime dt)
    {
        Span<char> buffer = stackalloc char[33]; // "O" format max length
        dt.TryFormat(buffer, out int charsWritten, "O");
        return new string(buffer[..charsWritten]);
    }

    private BasicProperties CreateBasicProperties(Dictionary<string, object> messageHeaders)
    {
        // foreach avoids the LINQ Select + enumerator allocation per message.
        var headersCopy = new Dictionary<string, object?>(messageHeaders.Count);
        foreach (var kvp in messageHeaders)
            headersCopy[kvp.Key] = kvp.Value;

        var basicProperties = new BasicProperties
        {
            Headers = headersCopy,
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

    private Dictionary<string, object> GetHeaders(Type type, Dictionary<string, string>? headers, string queueName, string messageType)
    {
        // Build the final object-valued dictionary directly rather than populating a
        // string-valued copy and then rewriting it. Pre-sized to the maximum
        // number of stamped keys + any caller-provided entries.
        var callerCount = headers?.Count ?? 0;
        var result = new Dictionary<string, object>(callerCount + StampedHeaderCount);

        if (headers is not null)
        {
            foreach (var kvp in headers)
                result[kvp.Key] = kvp.Value;
        }

        result[HeaderKeys.DestinationAddress] = queueName;
        result[HeaderKeys.MessageId] = Guid.NewGuid().ToString();
        result[HeaderKeys.MessageType] = messageType;

        result[HeaderKeys.SourceAddress] = _queueConfiguration.QueueName;
        result[HeaderKeys.TimeSent] = FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime);
        if (_busConfiguration.IncludeMachineNameInHeaders)
            result[HeaderKeys.SourceMachine] = Environment.MachineName;

        var (fullName, aqn) = _typeNameCache.GetOrAdd(type, static t => (t.FullName!, t.AssemblyQualifiedName!));
        result[HeaderKeys.TypeName] = fullName;
        result[HeaderKeys.FullTypeName] = aqn;

        result[HeaderKeys.ConsumerType] = "RabbitMQ";
        result[HeaderKeys.Language] = "C#";

        return result;
    }

    private async Task ConfigureExchangeAsync(string exchangeName, string type, CancellationToken cancellationToken)
    {
        await _model!.ExchangeDeclareAsync(exchangeName, type, true, false, null, false, false, cancellationToken).ConfigureAwait(false);
        // Mark as declared so subsequent publishes skip the round-trip.
        _declaredExchanges[exchangeName] = true;
    }

    private async Task DisposeModelAsync(IChannel? model)
    {
        if (model != null)
        {
            try
            {
                _logger.LogDebug("Disposing Model");
                if (model.IsOpen)
                    await model.CloseAsync();
                model.Dispose();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing model");
            }
        }
    }

    private async Task DisposeConnectionInstanceAsync(IConnection? connection)
    {
        if (connection != null)
        {
            try
            {
                _logger.LogDebug("Disposing connection");
                if (connection.IsOpen)
                    await connection.CloseAsync();
                connection.Dispose();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing connection");
            }
        }
    }

    private async Task DisposeConnectionAsync()
    {
        await _connectionSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownChannelAndConnectionAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    private async Task TearDownChannelAndConnectionAsync()
    {
        var model = Interlocked.Exchange(ref _model, null);
        var connection = Interlocked.Exchange(ref _connection, null);

        await DisposeModelAsync(model).ConfigureAwait(false);
        await DisposeConnectionInstanceAsync(connection).ConfigureAwait(false);
        _connected = false;
    }
}
