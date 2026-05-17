using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// RabbitMQ-backed implementation of <see cref="IProducer"/> for publishing and sending messages.
/// </summary>
internal sealed class Producer : IProducer
{
    /// <summary>Default maximum message body size, in bytes (64 KiB).</summary>
    private const long DefaultMaxMessageSize = 64 * 1024;

    // Cache the computed exchange name (FullName with dots stripped) per FullName string.
    private readonly ConcurrentDictionary<string, string> _exchangeNameCache = new(StringComparer.Ordinal);

    private readonly IQueueConfiguration _queueConfiguration;
    private readonly OutboundHeaderBuilder _headerBuilder;
    private readonly ProducerConnection _producerConnection;
    private readonly ILogger<Producer> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly TimeSpan _publishTimeout;
    private readonly ushort _retryCount;
    private readonly ushort _retryTimeInSeconds;
    private int _disposed;

    /// <summary>Overrides the dispose lock-wait timeout for unit tests.</summary>
    internal TimeSpan? DisposeTimeoutForTests;

    /// <summary>
    /// Test seam: routed through to <see cref="ProducerConnection.CreateConnectionForTests"/>.
    /// </summary>
    internal Func<ConnectionFactory, string[], string, CancellationToken, Task<IConnection>>? CreateConnectionForTests
    {
        get => _producerConnection.CreateConnectionForTests;
        set => _producerConnection.CreateConnectionForTests = value;
    }

    /// <summary>
    /// Test seam: when set, replaces the <c>Task.Delay</c> calls in the retry loop.
    /// The delegate receives the computed jittered delay and the caller's cancellation token.
    /// Production code leaves this null and calls <c>Task.Delay</c> directly.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task>? RetryDelayForTests;

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
        _timeProvider = timeProvider ?? TimeProvider.System;
        _headerBuilder = new OutboundHeaderBuilder(busConfiguration, queueConfiguration, _timeProvider, logger);
        _producerConnection = new ProducerConnection(transportConfiguration, logger);

        var settings = transportConfiguration.ClientSettings;
        MaximumMessageSize = GetSetting(settings, RabbitMQSettingKeys.MessageSize, DefaultMaxMessageSize, Convert.ToInt64);
        _publishTimeout = GetSetting(settings, RabbitMQSettingKeys.PublishTimeout, TimeSpan.FromSeconds(30), v => (TimeSpan)v);
        // Every publish path calls CancellationTokenSource.CancelAfter(_publishTimeout); the BCL
        // accepts only non-negative TimeSpans or Timeout.InfiniteTimeSpan (-1ms). Any other negative
        // value throws ArgumentOutOfRangeException at every publish, which the retry loop classifies
        // as retriable and burns the full retryCount*retrySeconds budget against. Reject loudly here.
        if (_publishTimeout < TimeSpan.Zero && _publishTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transportConfiguration),
                $"Setting '{RabbitMQSettingKeys.PublishTimeout}' must be non-negative or Timeout.InfiniteTimeSpan; got {_publishTimeout}.");
        }
        _retryCount = GetSetting(settings, RabbitMQSettingKeys.RetryCount, (ushort)60, Convert.ToUInt16);
        _retryTimeInSeconds = GetSetting(settings, RabbitMQSettingKeys.RetrySeconds, (ushort)10, Convert.ToUInt16);

        // Reject the dangerous combination: explicit publisher-acks=false with a finite
        // publish timeout is silent breakage. Without confirms, BasicPublishAsync returns
        // as soon as the frame is on the wire — the linked CTS in PublishWithTimeoutAsync
        // never fires for a stalled broker, so the configured timeout has no effect.
        // Acks-on (the default) is the safe path; an explicit acks-off must accompany
        // PublishTimeout=Zero or Timeout.InfiniteTimeSpan.
        var publisherAcks = GetSetting(settings, RabbitMQSettingKeys.PublisherAcknowledgements, true, Convert.ToBoolean);
        if (!publisherAcks && _publishTimeout > TimeSpan.Zero && _publishTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new InvalidOperationException(
                $"Conflicting RabbitMQ producer configuration: " +
                $"{RabbitMQSettingKeys.PublisherAcknowledgements}=false but " +
                $"{RabbitMQSettingKeys.PublishTimeout}={_publishTimeout.TotalSeconds:0.###}s. " +
                "Without publisher acknowledgements, BasicPublishAsync returns once the frame is on the wire, " +
                "so the publish timeout never fires for a stalled broker. " +
                $"Either remove the {RabbitMQSettingKeys.PublisherAcknowledgements}=false override (the default, true, is safe), " +
                $"or set {RabbitMQSettingKeys.PublishTimeout} to Timeout.InfiniteTimeSpan / TimeSpan.Zero.");
        }
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
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _producerConnection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes <paramref name="lockedAction"/> under <see cref="_publishLock"/> with retry on
    /// retriable failures. Critically: <see cref="EnsureConnectedAsync"/> and the inter-attempt
    /// delay run OUTSIDE the lock, so a slow reconnect cannot block concurrent publishers.
    /// On retriable failure the lock is released, <see cref="ProducerConnection.MarkResetRequired"/>
    /// is called, then the next attempt's prologue calls <see cref="EnsureConnectedAsync"/> which
    /// drives the reconnect under <c>_connectionSemaphore</c>.
    /// </summary>
    private async Task ExecuteRetryingPublishAsync(
        Func<CancellationToken, Task> lockedAction,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (int attempt = 0; attempt <= _retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // EnsureConnectedAsync runs OUTSIDE _publishLock so a slow reconnect (held under
                // _connectionSemaphore) never serialises concurrent publishers behind it.
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Re-check after acquiring the lock — DisposeAsync may have set _disposed
                    // and torn down the channel while we were waiting.
                    ObjectDisposedException.ThrowIf(_disposed != 0, this);
                    // Also re-check the channel: between EnsureConnectedAsync returning and
                    // _publishLock.WaitAsync acquiring, another publisher's slow-path teardown
                    // (driven by MarkResetRequired from an earlier timeout) can land
                    // ProducerConnection._model = null. Reading `Channel` here would throw
                    // InvalidOperationException with the misleading "before EnsureConnected"
                    // message AND classify retriable, which would call MarkResetRequired again —
                    // burning reconnect budget for a state that's already being reset. Throw
                    // the typed ChannelTransientException instead so the retriable path skips
                    // the redundant reset.
                    if (_producerConnection.TryGetChannel() is null)
                    {
                        throw new ChannelTransientException(
                            "Producer channel was torn down concurrently between connect and publish; retrying.");
                    }
                    await lockedAction(cancellationToken).ConfigureAwait(false);
                    return;
                }
                finally { _publishLock.Release(); }
            }
            catch (global::RabbitMQ.Client.Exceptions.PublishException pex)
            {
                // Broker-side nack — poison message, not a transport failure. Log and propagate
                // immediately; retrying would just re-fail against the same policy condition.
                _logger.LogWarning(pex, "Broker nacked publish: {Reason}", pex.Message);
                throw;
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException)
            {
                // PublishWithTimeoutAsync already flagged MarkResetRequired before throwing —
                // do not double-flag and do not retry. The next publish's EnsureConnectedAsync
                // consumes the flag and rebuilds the channel.
                throw;
            }
            catch (ObjectDisposedException)
            {
                // The producer was disposed mid-loop: EnsureConnectedAsync (or the post-lock
                // disposed re-check) sees _disposed flipped and throws. Retrying would burn
                // the full retryCount * retrySeconds budget against a permanently dead instance
                // (default 60 * 10s = 10 min). Pre-restructure this could not happen because
                // _publishLock spanned every retry attempt; now the lock is released between
                // attempts so dispose can race in. Mirror the predicate used in
                // ProducerConnection.EnsureConnectedAsync's Retry.DoAsync.
                throw;
            }
            catch (ChannelTransientException ex)
            {
                // Channel was torn down between EnsureConnectedAsync and lock acquisition.
                // The teardown is already the reset; retrying without MarkResetRequired drives
                // the next attempt's EnsureConnectedAsync prologue (rebuild via _connectionSemaphore)
                // without doubling the reconnect budget. Inter-attempt delay still runs OUTSIDE
                // the lock so other publishers can interleave.
                lastException = ex;
                if (attempt < _retryCount)
                {
                    _logger.LogDebug(
                        "Publish attempt {Attempt}/{Total} hit transient channel state; retrying after {Delay}s",
                        attempt + 1,
                        _retryCount + 1,
                        _retryTimeInSeconds);
                    var transientDelay = JitteredRetryDelay();
                    await (RetryDelayForTests?.Invoke(transientDelay, cancellationToken) ?? Task.Delay(transientDelay, cancellationToken)).ConfigureAwait(false);
                    continue;
                }
                throw;
            }
            catch (Exception ex) when (IsRetriablePublishException(ex))
            {
                lastException = ex;
                _producerConnection.MarkResetRequired();
                if (attempt < _retryCount)
                {
                    _logger.LogWarning(
                        ex,
                        "Publish attempt {Attempt}/{Total} failed; will retry after {Delay}s",
                        attempt + 1,
                        _retryCount + 1,
                        _retryTimeInSeconds);
                    // Inter-attempt delay also runs OUTSIDE the lock so other publishers can interleave.
                    // Mean is fixed (not exponential) — connection-create inside EnsureConnectedAsync
                    // already does its own exponential backoff via Retry.DoAsync, so layering exponentials
                    // would double-grow the wall-clock budget. ±50% jitter is applied per attempt so
                    // concurrent producers do not reconnect in lockstep after a broker restart.
                    var retriableDelay = JitteredRetryDelay();
                    await (RetryDelayForTests?.Invoke(retriableDelay, cancellationToken) ?? Task.Delay(retriableDelay, cancellationToken)).ConfigureAwait(false);
                    continue;
                }
                throw;
            }
        }

        // Defensive: every loop arm either returns or throws; this is a regression guard.
        throw lastException ?? new InvalidOperationException(
            "ExecuteRetryingPublishAsync exited without success or exception.");
    }

    // Produces a uniformly-distributed delay in [mean*0.5, mean*1.5) around the configured
    // retry mean. Random.Shared is thread-safe under .NET 6+. Keeping the mean fixed (rather
    // than exponential) avoids stacking two independent exponential growth curves: the
    // connection-creation path inside EnsureConnectedAsync already applies exponential backoff
    // via Retry.DoAsync. The ±50% jitter ensures concurrent producers don't all retry in
    // lockstep after a broker restart even though the mean wall-clock budget is unchanged.
    private TimeSpan JitteredRetryDelay()
    {
        var meanSeconds = _retryTimeInSeconds;
        var jitterFactor = 0.5 + Random.Shared.NextDouble(); // [0.5, 1.5)
        return TimeSpan.FromSeconds(meanSeconds * jitterFactor);
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
    /// <param name="body">The serialized message body.</param>
    /// <param name="headers">Optional custom headers to include with the message.</param>
    /// <param name="cancellationToken">A token used to cancel the publish operation.</param>
    public Task PublishAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
        => PublishAsync(type, body, routingKey: null, headers, cancellationToken);

    public async Task PublishAsync(Type type, ReadOnlyMemory<byte> body, string? routingKey, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        cancellationToken.ThrowIfCancellationRequested();
        if (body.Length > MaximumMessageSize)
        {
            throw new InvalidOperationException(
                $"Message size {body.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        }

        // Capture timestamp before EnsureConnectedAsync — connect latency is part of the
        // user-visible publish duration. exchangeName is resolved inside the locked action;
        // see EmitPublishMetrics for the empty-destination fallback.
        var startTimestamp = Stopwatch.GetTimestamp();
        string exchangeName = string.Empty;
        bool succeeded = false;
        Exception? failure = null;
        // RabbitMQ.Client interprets a null routing key as empty string; normalise here so
        // metric tagging and BasicPublishAsync see the same value. Fanout exchanges ignore
        // routing keys; topic/direct exchanges use them for routing — the caller may have
        // configured a non-fanout exchange override and supplied a key via PublishOptions.RoutingKey.
        var resolvedRoutingKey = routingKey ?? string.Empty;
        try
        {
            await ExecuteRetryingPublishAsync(async ct =>
            {
                var messageHeaders = _headerBuilder.BuildHeaders(type, headers, _queueConfiguration.QueueName, "Publish");
                var basicProperties = _headerBuilder.BuildBasicProperties(messageHeaders);

                // Compute the exchange name once per type and cache it.
                // Only issue ExchangeDeclareAsync once per connection — skip on subsequent publishes.
                exchangeName = GetExchangeName(type);

                await _producerConnection.EnsureExchangeDeclaredAsync(exchangeName, ExchangeType.Fanout, ct).ConfigureAwait(false);
                await PublishWithTimeoutAsync(
                    _producerConnection.Channel,
                    exchangeName,
                    resolvedRoutingKey,
                    false,
                    basicProperties,
                    body,
                    ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            succeeded = true;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            EmitPublishMetrics(startTimestamp, exchangeName, succeeded, failure);
        }
    }

    /// <summary>
    /// Sends a message to each endpoint mapped to the specified message type.
    /// </summary>
    /// <param name="type">The logical message type used to resolve destination queues.</param>
    /// <param name="body">The serialized message body.</param>
    /// <param name="headers">Optional custom headers to include with the message.</param>
    /// <param name="cancellationToken">A token used to cancel the send operation.</param>
    /// <remarks>
    /// When the message type maps to multiple queues, every endpoint is attempted; per-endpoint
    /// failures are collected and surface as an <see cref="AggregateException"/> at the end of
    /// the loop. Cancellation via <paramref name="cancellationToken"/> propagates as
    /// <see cref="OperationCanceledException"/> directly and aborts the remaining iterations.
    /// </remarks>
    public async Task SendAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        cancellationToken.ThrowIfCancellationRequested();
        if (body.Length > MaximumMessageSize)
        {
            throw new InvalidOperationException(
                $"Message size {body.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        }

        if (!_queueConfiguration.TryGetQueueMapping(type, out IReadOnlyList<string>? endPoints))
        {
            throw new InvalidOperationException($"No queue mapping configured for message type '{type.FullName}'. Register a mapping via AddQueueMapping.");
        }

        // Build base headers once outside the loop. DestinationAddress, MessageId, and TimeSent
        // are re-stamped per endpoint because each on-wire message is logically distinct.
        // The aliasing-safety invariant: publisher confirms gate the prior await before the
        // next iteration mutates baseHeaders, so reuse is safe. CorrelationId is the
        // cross-fan-out correlator and is NOT re-minted here.
        var baseHeaders = _headerBuilder.BuildHeaders(type, headers, string.Empty, "Send");
        List<Exception>? endpointFailures = null;
        foreach (string endPoint in endPoints)
        {
            baseHeaders[HeaderKeys.DestinationAddress] = endPoint;
            baseHeaders[HeaderKeys.MessageId] = Guid.NewGuid().ToString();
            baseHeaders[HeaderKeys.TimeSent] = OutboundHeaderBuilder.FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime);
            var basicProperties = _headerBuilder.BuildBasicProperties(baseHeaders);

            // Per-endpoint metric scope: each delivery on the fan-out is a logically
            // independent publish — record duration + success/error individually so the
            // tag set carries the correct destination queue and partial-fan-out failures
            // are visible per endpoint.
            var endpointStart = Stopwatch.GetTimestamp();
            bool endpointSucceeded = false;
            Exception? endpointFailure = null;
            try
            {
                // Each endpoint is independently retriable; ExecuteRetryingPublishAsync releases
                // _publishLock between endpoints so other publishers may interleave. The
                // aliasing invariant still holds — publisher confirms gate each endpoint's
                // await before the next iteration mutates baseHeaders.
                await ExecuteRetryingPublishAsync(async ct =>
                {
                    await PublishWithTimeoutAsync(
                        _producerConnection.Channel,
                        string.Empty,
                        endPoint,
                        // mandatory:true — Send routes via the default exchange + queue-name
                        // routing key. With publisher confirms, an unroutable publish (queue
                        // not declared, typo, deleted) surfaces as PublishException rather
                        // than being silently dropped at the broker. IsRetriablePublishException
                        // returns false for PublishException, so the failure surfaces to the
                        // caller's fan-out catch and is collected into the AggregateException.
                        true,
                        basicProperties,
                        body,
                        ct).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
                endpointSucceeded = true;
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                // Caller-initiated cancellation aborts subsequent endpoints, but if prior
                // endpoints already failed in this fan-out, those failures must NOT be lost:
                // aggregate them with the OCE. With no prior failures, OCE propagates plain
                // so caller-side cancellation handlers see the canonical type.
                //
                // The `when (cancellationToken.IsCancellationRequested)` filter is load-bearing:
                // an OCE thrown from a middleware-internal linked CTS (custom timeout, per-
                // endpoint deadline) carries a different token and is NOT caller cancellation.
                // Those fall through to the generic catch and aggregate as endpoint failures,
                // matching the semantics of Bus.SendToManyAsync.
                endpointFailure = ex;
                if (endpointFailures is { Count: > 0 })
                {
                    endpointFailures.Add(ex);
                    throw new AggregateException(
                        $"One or more endpoints failed during fan-out send for message type '{type.FullName}', and a later endpoint was cancelled.",
                        endpointFailures);
                }
                throw;
            }
            catch (ObjectDisposedException ex)
            {
                // The producer is permanently dead; retrying against further endpoints would
                // emit N redundant failure metrics and produce an AggregateException of N
                // identical ODEs. Aggregate any prior failures with this ODE and abort the
                // fan-out so the caller sees a single ODE-shaped failure on a disposed
                // producer.
                endpointFailure = ex;
                if (endpointFailures is { Count: > 0 })
                {
                    endpointFailures.Add(ex);
                    throw new AggregateException(
                        $"One or more endpoints failed during fan-out send for message type '{type.FullName}', and the producer was disposed mid-fan-out.",
                        endpointFailures);
                }
                throw;
            }
            catch (Exception ex)
            {
                endpointFailure = ex;
                (endpointFailures ??= []).Add(ex);
            }
            finally
            {
                EmitPublishMetrics(endpointStart, endPoint, endpointSucceeded, endpointFailure);
            }
        }

        if (endpointFailures is { Count: > 0 })
        {
            throw new AggregateException(
                $"One or more endpoints failed during fan-out send for message type '{type.FullName}'.",
                endpointFailures);
        }
    }

    /// <summary>
    /// Sends a message directly to the specified endpoint.
    /// </summary>
    /// <param name="endPoint">The destination queue name.</param>
    /// <param name="type">The logical message type used when stamping headers.</param>
    /// <param name="body">The serialized message body.</param>
    /// <param name="headers">Optional custom headers to include with the message.</param>
    /// <param name="cancellationToken">A token used to cancel the send operation.</param>
    public Task SendAsync(string endPoint, Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
        => SendAsync(endPoint, type, body, routingSlipHopsCompleted: null, headers, cancellationToken);

    /// <inheritdoc />
    public async Task SendAsync(string endPoint, Type type, ReadOnlyMemory<byte> body, int? routingSlipHopsCompleted, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(endPoint))
        {
            throw new ArgumentException($"Cannot send message of type {type} to empty endpoint", nameof(endPoint));
        }

        if (body.Length > MaximumMessageSize)
        {
            throw new InvalidOperationException(
                $"Message size {body.Length} bytes exceeds maximum allowed size of {MaximumMessageSize} bytes.");
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        bool succeeded = false;
        Exception? failure = null;
        try
        {
            await ExecuteRetryingPublishAsync(async ct =>
            {
                var messageHeaders = _headerBuilder.BuildHeaders(type, headers, endPoint, "Send", routingSlipHopsCompleted);
                var basicProperties = _headerBuilder.BuildBasicProperties(messageHeaders);
                await PublishWithTimeoutAsync(
                    _producerConnection.Channel,
                    string.Empty,
                    endPoint,
                    // mandatory:true — Send to a specific endpoint must surface unroutable
                    // (queue not declared, typo, deleted) as PublishException instead of
                    // silently dropping at the broker. See SendAsync(Type) for the rationale.
                    true,
                    basicProperties,
                    body,
                    ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            succeeded = true;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            EmitPublishMetrics(startTimestamp, endPoint, succeeded, failure);
        }
    }

    /// <summary>
    /// Sends raw bytes directly to the specified endpoint.
    /// </summary>
    /// <param name="endPoint">The destination queue name.</param>
    /// <param name="type">The logical message type the packet represents; used to stamp the reserved type headers authoritatively.</param>
    /// <param name="packet">The raw payload to send.</param>
    /// <param name="headers">Optional custom headers to include with the packet.</param>
    /// <param name="cancellationToken">A token used to cancel the send operation.</param>
    public async Task SendBytesAsync(string endPoint, Type type, ReadOnlyMemory<byte> packet, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
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

        var startTimestamp = Stopwatch.GetTimestamp();
        bool succeeded = false;
        Exception? failure = null;
        try
        {
            await ExecuteRetryingPublishAsync(async ct =>
            {
                var messageHeaders = _headerBuilder.BuildHeaders(type, headers, endPoint, HeaderKeys.ByteStream);
                var basicProperties = _headerBuilder.BuildBasicProperties(messageHeaders);
                await PublishWithTimeoutAsync(
                    _producerConnection.Channel,
                    string.Empty,
                    endPoint,
                    // mandatory:true — SendBytes to a specific endpoint must surface
                    // unroutable (queue not declared, typo, deleted) as PublishException
                    // instead of silently dropping at the broker. Same rationale as SendAsync.
                    true,
                    basicProperties,
                    packet,
                    ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            succeeded = true;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            EmitPublishMetrics(startTimestamp, endPoint, succeeded, failure);
        }
    }

    // Emits messaging.publish.duration (always) and messaging.client.published.messages
    // (only on success). Tags follow OTel semantic conventions for messaging.
    // Caller passes the per-attempt destination — the exchange for fan-out publishes,
    // the queue name for direct sends. An empty/null destination indicates the publish
    // failed before the destination was resolved (e.g. EnsureConnectedAsync threw); we
    // emit "<unresolved>" rather than dropping the metric so the failure is still visible.
    private static void EmitPublishMetrics(long startTimestamp, string destination, bool succeeded, Exception? failure)
    {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
        var resolvedDestination = string.IsNullOrEmpty(destination) ? "<unresolved>" : destination;

        var durationTags = new TagList
        {
            { "messaging.system", "rabbitmq" },
            { "messaging.operation.type", "publish" },
            { "messaging.operation.name", "publish" },
            { "messaging.destination.name", resolvedDestination },
        };
        // TimeoutException from PublishWithTimeoutAsync indicates the broker ack didn't arrive
        // within the budget — the message MAY still have been delivered. The dedicated
        // PublishConfirmTimeouts counter (emitted in PublishWithTimeoutAsync) is the
        // authoritative signal; suppress error.type here so dashboards don't count
        // confirm-timeouts as definite failures alongside the dedicated counter.
        if (failure is { } nonTimeoutFailure and not TimeoutException)
        {
            durationTags.Add("error.type", ExceptionTypeMapper.Map(nonTimeoutFailure));
        }
        ServiceConnectMeter.RecordPublishDuration(elapsed, durationTags);

        if (succeeded)
        {
            var successTags = new TagList
            {
                { "messaging.system", "rabbitmq" },
                { "messaging.operation.type", "publish" },
                { "messaging.operation.name", "publish" },
                { "messaging.destination.name", resolvedDestination },
            };
            ServiceConnectMeter.AddPublishedMessage(successTags);
        }
    }


    /// <summary>
    /// Releases the producer's RabbitMQ channel, connection, and synchronization primitives.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
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

            // Release the publish lock BEFORE CloseAsync. A parallel publisher already
            // past EnsureConnectedAsync and blocked at _publishLock.WaitAsync would
            // otherwise wait the full disposeTimeout + close duration before its
            // post-acquire ObjectDisposedException re-check fires. Releasing first lets
            // that publisher acquire-and-trip-ODE in the typical microsecond range while
            // CloseAsync proceeds in parallel; `_disposed=1` is already set above so the
            // unblocked publisher cannot start new work, only short-circuit out.
            if (publishLockAcquired)
            {
                _publishLock.Release();
            }

            try { await _producerConnection.CloseAsync(remaining).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Producer connection close failed during dispose"); }

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

    /// <inheritdoc />
    public bool HasAttemptedConnection => _producerConnection.HasAttemptedConnection;

    /// <inheritdoc />
    public ProducerHealthSnapshot GetHealthSnapshot()
        => _producerConnection.GetSnapshot();

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
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Mark the channel for reset on the next publish. The reset runs inside EnsureConnectedAsync,
            // which is called BEFORE _publishLock.WaitAsync, so concurrent publishers are not blocked
            // behind it. We do NOT reconnect here: doing so would hold _publishLock for up to
            // retryCount * retrySeconds (default 60 * 10s = 10 minutes) blocking every other publisher.
            // The broker may still eventually ack this timed-out publish; a fresh connection + channel
            // on the next publish clears the confirm-tracker's state before any subsequent publish runs.
            _producerConnection.MarkResetRequired();

            // Tag schema matches the sibling publish-duration histograms (Producer.cs:580):
            // operation.type for cross-metric dashboards, system for messaging-system filter,
            // destination.name for per-queue alerting. SendAsync passes exchange="" with the
            // real destination on routingKey (point-to-point goes through the default direct
            // exchange), so prefer routingKey when exchange is empty rather than emitting a
            // placeholder that obscures which queue stalled.
            var destinationTag = !string.IsNullOrEmpty(exchange)
                ? exchange
                : (!string.IsNullOrEmpty(routingKey) ? routingKey : "<empty>");
            ServiceConnectMeter.AddPublishConfirmTimeout(new TagList
            {
                { "messaging.system", "rabbitmq" },
                { "messaging.operation.type", "publish" },
                { "messaging.destination.name", destinationTag },
            });

            throw new TimeoutException(
                $"BasicPublishAsync exceeded the configured publish timeout of {_publishTimeout.TotalSeconds:0.###}s " +
                $"(exchange='{exchange}', routingKey='{routingKey}', messageId='{basicProperties.MessageId ?? "<none>"}'). " +
                "The broker may be stalled or the connection may be half-open.");
        }
    }
}

