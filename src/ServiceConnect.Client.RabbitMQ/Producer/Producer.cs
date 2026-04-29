using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// RabbitMQ-backed implementation of <see cref="IProducer"/> for publishing and sending messages.
/// </summary>
public sealed class Producer : IProducer
{
    /// <summary>Default maximum message body size, in bytes (64 KiB).</summary>
    private const long DefaultMaxMessageSize = 64 * 1024;

    // Cache the computed exchange name (FullName with dots stripped) per FullName string.
    private readonly ConcurrentDictionary<string, string> _exchangeNameCache = new(StringComparer.Ordinal);

    private readonly IQueueConfiguration _queueConfiguration;
    private readonly OutboundHeaderBuilder _headerBuilder;
    private readonly ProducerConnection _producerConnection;
    private readonly ILogger<Producer> _logger;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly TimeSpan _publishTimeout;
    private readonly ushort _retryCount;
    private readonly ushort _retryTimeInSeconds;
    private int _disposedInt;

    /// <summary>Overrides the dispose lock-wait timeout for unit tests.</summary>
    internal TimeSpan? DisposeTimeoutForTests;

    /// <summary>
    /// Test seam: routed through to <see cref="ProducerConnection.ReconnectForTests"/>.
    /// </summary>
    internal Func<CancellationToken, Task>? ReconnectForTests
    {
        get => _producerConnection.ReconnectForTests;
        set => _producerConnection.ReconnectForTests = value;
    }

    /// <summary>
    /// Test seam: routed through to <see cref="ProducerConnection.CreateConnectionForTests"/>.
    /// </summary>
    internal Func<ConnectionFactory, string[], string, CancellationToken, Task<IConnection>>? CreateConnectionForTests
    {
        get => _producerConnection.CreateConnectionForTests;
        set => _producerConnection.CreateConnectionForTests = value;
    }

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
        _queueConfiguration = queueConfiguration;
        ArgumentNullException.ThrowIfNull(busConfiguration);
        _logger = logger;
        _headerBuilder = new OutboundHeaderBuilder(busConfiguration, queueConfiguration, timeProvider ?? TimeProvider.System, logger);
        _producerConnection = new ProducerConnection(transportConfiguration, logger);

        var settings = transportConfiguration.ClientSettings;
        MaximumMessageSize = GetSetting(settings, RabbitMQSettingKeys.MessageSize, DefaultMaxMessageSize, Convert.ToInt64);
        _publishTimeout = GetSetting(settings, RabbitMQSettingKeys.PublishTimeout, TimeSpan.FromSeconds(30), v => (TimeSpan)v);
        _retryCount = GetSetting(settings, RabbitMQSettingKeys.RetryCount, (ushort)60, Convert.ToUInt16);
        _retryTimeInSeconds = GetSetting(settings, RabbitMQSettingKeys.RetrySeconds, (ushort)10, Convert.ToUInt16);
    }

    private static T GetSetting<T>(IReadOnlyDictionary<string, object> settings, string key, T defaultValue, Func<object, T> converter)
    {
        return settings.TryGetValue(key, out var value) ? converter(value) : defaultValue;
    }

    // Return the cached exchange name for a type, keying on FullName so that
    // assembly version churn or type forwarding (which changes AQN but not
    // FullName) does not create duplicate entries for the same exchange.
    private string GetExchangeName(Type type)
    {
        return _exchangeNameCache.GetOrAdd(
            type.FullName ?? type.AssemblyQualifiedName!,
            _ => ServiceConnect.Services.MessageTypeExchangeName.From(type));
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposedInt != 0, this);
        await _producerConnection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteWithConnectionRetryAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await Retry.DoAsync(
                action,
                ex => _producerConnection.ReconnectAsync(ex, cancellationToken),
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
    //
    // TimeoutException comes from PublishWithTimeoutAsync when the broker ack doesn't arrive
    // within _publishTimeout. A reconnect won't help — the connection is considered stalled/dead;
    // propagate immediately so callers can decide whether to retry at a higher level.
    private static bool IsRetriablePublishException(Exception ex)
    {
        if (ex is global::RabbitMQ.Client.Exceptions.PublishException)
        {
            return false;
        }

        if (ex is OperationCanceledException)
        {
            return false;
        }

        if (ex is TimeoutException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Publishes a message to the exchange derived from the specified message type.
    /// </summary>
    /// <param name="type">The logical message type used to determine the publish exchange and stamped headers.</param>
    /// <param name="message">The serialized message body.</param>
    /// <param name="headers">Optional custom headers to include with the message.</param>
    /// <param name="cancellationToken">A token used to cancel the publish operation.</param>
    public async Task PublishAsync(Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (message.Length > MaximumMessageSize)
        {
            throw new InvalidOperationException(
                $"Message size {message.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock — DisposeAsync may have set _disposedInt
            // and torn down the channel while we were waiting. Without this check the publish
            // would NRE on the missing channel.
            ObjectDisposedException.ThrowIf(_disposedInt != 0, this);

            var messageHeaders = _headerBuilder.BuildHeaders(type, headers, _queueConfiguration.QueueName, "Publish");
            var basicProperties = _headerBuilder.BuildBasicProperties(messageHeaders);

            // Compute the exchange name once per type and cache it.
            // Only issue ExchangeDeclareAsync once per connection — skip on subsequent publishes.
            string exchangeName = GetExchangeName(type);

            await ExecuteWithConnectionRetryAsync(async () =>
            {
                await _producerConnection.EnsureExchangeDeclaredAsync(exchangeName, ExchangeType.Fanout, cancellationToken).ConfigureAwait(false);

                await PublishWithTimeoutAsync(
                    _producerConnection.Channel,
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
    public async Task SendAsync(Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (message.Length > MaximumMessageSize)
        {
            throw new InvalidOperationException(
                $"Message size {message.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check disposed flag after winning the lock; see PublishAsync.
            ObjectDisposedException.ThrowIf(_disposedInt != 0, this);

            if (!_queueConfiguration.TryGetQueueMapping(type, out IReadOnlyList<string>? endPoints))
            {
                throw new InvalidOperationException($"No queue mapping configured for message type '{type.FullName}'. Register a mapping via AddQueueMapping.");
            }

            // Build base headers once outside the loop; only DestinationAddress varies per endpoint.
            var baseHeaders = _headerBuilder.BuildHeaders(type, headers, string.Empty, "Send");
            foreach (string endPoint in endPoints)
            {
                baseHeaders[HeaderKeys.DestinationAddress] = endPoint;
                var basicProperties = _headerBuilder.BuildBasicProperties(baseHeaders);
                await ExecuteWithConnectionRetryAsync(
                    () => PublishWithTimeoutAsync(
                        _producerConnection.Channel,
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
    public async Task SendAsync(string endPoint, Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(endPoint))
        {
            throw new ArgumentException($"Cannot send message of type {type} to empty endpoint", nameof(endPoint));
        }

        if (message.Length > MaximumMessageSize)
        {
            throw new InvalidOperationException(
                $"Message size {message.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check disposed flag after winning the lock; see PublishAsync.
            ObjectDisposedException.ThrowIf(_disposedInt != 0, this);

            var messageHeaders = _headerBuilder.BuildHeaders(type, headers, endPoint, "Send");
            var basicProperties = _headerBuilder.BuildBasicProperties(messageHeaders);
            await ExecuteWithConnectionRetryAsync(
                () => PublishWithTimeoutAsync(
                    _producerConnection.Channel,
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
    public async Task SendBytesAsync(string endPoint, Type type, byte[] packet, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(packet);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(endPoint))
        {
            throw new ArgumentException($"Cannot send packet of type {type} to empty endpoint", nameof(endPoint));
        }

        if (packet.Length > MaximumMessageSize)
        {
            throw new InvalidOperationException(
                $"Message size {packet.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check disposed flag after winning the lock; see PublishAsync.
            ObjectDisposedException.ThrowIf(_disposedInt != 0, this);

            var messageHeaders = _headerBuilder.BuildHeaders(type, headers, endPoint, HeaderKeys.ByteStream);
            var basicProperties = _headerBuilder.BuildBasicProperties(messageHeaders);
            await ExecuteWithConnectionRetryAsync(
                () => PublishWithTimeoutAsync(
                    _producerConnection.Channel,
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
        if (Interlocked.Exchange(ref _disposedInt, 1) != 0)
        {
            return;
        }

        // Wait for in-flight publishes and (re)connections to complete before tearing down
        // the channel/connection. The two waits SHARE a single stopwatch budget so worst-case
        // dispose latency is bounded by disposeTimeout, not 2 * disposeTimeout. After the
        // budget is exhausted we proceed with forced teardown regardless.
        var disposeTimeout = DisposeTimeoutForTests ?? TimeSpan.FromSeconds(30);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var publishLockAcquired = false;
        try
        {
            publishLockAcquired = await _publishLock.WaitAsync(disposeTimeout).ConfigureAwait(false);
            if (!publishLockAcquired)
            {
                _logger.LogWarning(
                    "Producer dispose could not acquire publish lock within {Timeout}; forcing teardown",
                    disposeTimeout);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Producer dispose lock-wait failed; forcing teardown");
        }
        finally
        {
            // Best-effort teardown ALWAYS runs, whether or not we held the lock.
            // A stuck BasicPublishAsync will observe the channel closing and throw —
            // that is the correct shutdown signal for an in-flight publisher.
            var remaining = disposeTimeout - stopwatch.Elapsed;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            try { await _producerConnection.CloseAsync(remaining).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Producer connection close failed during dispose"); }

            if (publishLockAcquired)
            {
                _publishLock.Release();
            }

            // _publishLock is intentionally NOT Disposed:
            // SemaphoreSlim.Dispose only releases the lazily-allocated WaitHandle, and
            // we never call AvailableWaitHandle, so disposal is a functional no-op. An
            // in-flight publisher's `finally { _publishLock.Release(); }` running on a
            // disposed semaphore throws ObjectDisposedException out of the unwind path,
            // which we cannot prevent without holding GC references to every caller.
            // The field is GC'd with the Producer instance.
        }
    }

    /// <summary>
    /// Gets the maximum allowed outbound message size, in bytes.
    /// </summary>
    public long MaximumMessageSize { get; }

    /// <inheritdoc />
    public bool IsHealthy => _producerConnection.IsHealthy();

    /// <summary>
    /// Wraps <c>IChannel.BasicPublishAsync</c> with a configurable timeout.
    /// If the broker ack does not arrive within <see cref="_publishTimeout"/>, the waiting task
    /// is cancelled and a <see cref="TimeoutException"/> is thrown.  If the caller's own
    /// <paramref name="cancellationToken"/> fires first, the normal
    /// <see cref="OperationCanceledException"/> propagates unchanged.
    /// </summary>
    private async ValueTask PublishWithTimeoutAsync(
        IChannel channel,
        string exchange,
        string routingKey,
        bool mandatory,
        BasicProperties basicProperties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_publishTimeout);
        try
        {
            await channel.BasicPublishAsync(
                exchange,
                routingKey,
                mandatory,
                basicProperties,
                body,
                linked.Token).ConfigureAwait(false);
        }
        // Only remap to TimeoutException when our linked CTS fired AND the caller's token didn't.
        // A spurious OCE (neither token cancelled) propagates as cancellation; a caller-requested
        // cancellation wins priority over timeout mapping.
        catch (OperationCanceledException oce) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Reset the channel: the broker may eventually ack this timed-out publish, which
            // would contaminate the confirm slot of a later in-flight publish. A fresh
            // connection + channel clears the confirm-tracker's state.
            try { await _producerConnection.ReconnectAsync(oce, cancellationToken).ConfigureAwait(false); }
            catch (Exception resetEx) { _logger.LogError(resetEx, "Failed to reset connection after publish timeout; channel state may be indeterminate."); }

            throw new TimeoutException(
                $"BasicPublishAsync exceeded the configured publish timeout of {_publishTimeout.TotalSeconds:0.###}s " +
                $"(exchange='{exchange}', routingKey='{routingKey}', messageId='{basicProperties.MessageId ?? "<none>"}'). " +
                "The broker may be stalled or the connection may be half-open.");
        }
    }
}
