using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Owns the consume channel (<c>Model</c>) and publish channel (<c>PublishChannel</c>) for a
/// single <c>RabbitMqConsumerHost</c>. Encapsulates channel acquisition, shutdown-event
/// subscription, the broker-cancelled flag, and disposal ordering.
/// </summary>
/// <remarks>
/// Channel-shutdown events from the broker (queue deleted, policy expired, peer protocol
/// error) are NOT auto-recovered by RabbitMQ.Client. When a non-Application initiator
/// closes the channel, this class flips the broker-cancelled flag so the consumer host's
/// IsCancelledByBroker accessor and BusConsumingHealthCheck report Unhealthy. Application-
/// initiated shutdown (host DisposeAsync / StopAsync) does NOT flip the flag.
///
/// The two channels are exposed as nullable properties; callers must null-check before
/// invoking channel operations. Disposal is idempotent; channels are closed in reverse-of-
/// create order (publish channel first so in-flight publishes drain before the consume
/// channel goes away).
/// </remarks>
internal sealed class RabbitMqChannelHost : IAsyncDisposable
{
    private readonly IServiceConnectConnection _connection;
    private readonly ILogger _logger;
    private readonly string _queueName;
    private IChannel? _model;
    private IChannel? _publishChannel;
    private int _consumerCancelledByBroker;
    private int _disposed;

    internal RabbitMqChannelHost(IServiceConnectConnection connection, ILogger logger, string queueName)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queueName = queueName ?? throw new ArgumentNullException(nameof(queueName));
    }

    /// <summary>The consume channel. Null until <see cref="OpenAsync"/> succeeds; null after disposal.</summary>
    internal IChannel? Model => _model;

    /// <summary>The publish channel. Null until <see cref="OpenAsync"/> succeeds; null after disposal.</summary>
    internal IChannel? PublishChannel => _publishChannel;

    /// <summary>
    /// True if a non-Application channel shutdown fired (broker tore down the channel, e.g.
    /// queue deleted, policy expired, peer protocol error), or if the host explicitly
    /// reported a broker-initiated basic.cancel via <see cref="NotifyBrokerCancelled"/>.
    /// Latched until disposal.
    /// </summary>
    internal bool IsCancelledByBroker => Volatile.Read(ref _consumerCancelledByBroker) != 0;

    /// <summary>
    /// Sets the broker-cancelled flag. Called by the consumer host when the broker issues
    /// a basic.cancel against the consumer (queue deleted, policy expired, mirror promoted).
    /// Idempotent — repeated calls are no-ops.
    /// </summary>
    internal void NotifyBrokerCancelled()
        => Interlocked.Exchange(ref _consumerCancelledByBroker, 1);

    /// <summary>
    /// Removes the channel-shutdown event subscriptions without closing or disposing the channels.
    /// Call this before issuing BasicCancelAsync during a graceful stop so a stale-tag protocol
    /// error (Library-initiator channel close) cannot flip IsCancelledByBroker on an intentional
    /// shutdown. DisposeAsync unsubscribes again idempotently; the delegate removal is a no-op
    /// if the handler is not currently subscribed.
    /// </summary>
    internal void UnsubscribeShutdownHandlers()
    {
        if (_model is not null)
        {
            _model.ChannelShutdownAsync -= OnChannelShutdownAsync;
        }
        if (_publishChannel is not null)
        {
            _publishChannel.ChannelShutdownAsync -= OnPublishChannelShutdownAsync;
        }
    }

    /// <summary>
    /// Opens the consume + publish channels and subscribes the shutdown event handlers.
    /// Idempotent on success — repeated calls return the already-open channels without
    /// re-opening.
    /// </summary>
    internal async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_model is not null && _publishChannel is not null)
        {
            return;
        }

        _model = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);
        // Dedicated publish channel for retry/audit/error; kept separate from the
        // consumer channel because RabbitMQ.Client is not safe to use concurrently on
        // a single channel. Publisher confirms ensure BasicPublishAsync awaits the
        // broker ack before returning, so a lost retry/audit/error publish surfaces as
        // an exception on the consumer path instead of silently disappearing.
        var publishChannelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        _publishChannel = await _connection.CreateChannelAsync(publishChannelOptions, cancellationToken).ConfigureAwait(false);

        _model.ChannelShutdownAsync += OnChannelShutdownAsync;
        // Publish channel needs an independent shutdown subscriber: RabbitMQ.Client does
        // NOT auto-recreate channels closed by a broker protocol error (404 NOT_FOUND on a
        // deleted retry/error exchange, 406 PRECONDITION_FAILED on topology drift). Without
        // this hook a dead publish channel goes unobserved, retry/audit/terminal-failure
        // publishes throw AlreadyClosedException on every delivery, the host nacks-with-requeue,
        // and the broker hot-loops the same delivery against the same dead channel.
        _publishChannel.ChannelShutdownAsync += OnPublishChannelShutdownAsync;
    }

    private Task OnChannelShutdownAsync(object? sender, ShutdownEventArgs args)
    {
        // Broker- or peer-initiated channel close (e.g. queue deleted via management UI;
        // 404/406 against the consumer channel) is NOT auto-recovered by RabbitMQ.Client
        // and consumption stops silently otherwise. Flip the broker-cancelled flag so
        // BusConsumingHealthCheck and ConsumerConnectionHealthCheck flip Unhealthy and
        // operators see the failure rather than green-dashboarding a stalled consumer.
        // ShutdownInitiator.Application is our own DisposeAsync / StopAsync — those must
        // not flip the flag (they're intentional shutdown, not broker cancellation).
        if (args.Initiator != ShutdownInitiator.Application)
        {
            Interlocked.Exchange(ref _consumerCancelledByBroker, 1);
        }
        _logger.LogWarning(
            "AMQP channel shutdown for queue '{Queue}': {ReplyCode} {ReplyText} (initiator: {Initiator})",
            _queueName, args.ReplyCode, args.ReplyText, args.Initiator);
        return Task.CompletedTask;
    }

    private Task OnPublishChannelShutdownAsync(object? sender, ShutdownEventArgs args)
    {
        // Publish channel close is invisible to the consumer's IsConsuming/IsCancelledByBroker
        // chain unless we explicitly raise it. A non-Application close means the broker (or
        // peer protocol error) tore the channel down; downstream retry/audit publishes will
        // throw AlreadyClosedException and the message gets nacked-with-requeue forever.
        // Flip the broker-cancelled flag so the health checks surface the failure and the
        // pod is removed from rotation rather than burning CPU on a redelivery hot-loop.
        if (args.Initiator != ShutdownInitiator.Application)
        {
            Interlocked.Exchange(ref _consumerCancelledByBroker, 1);
        }
        _logger.LogWarning(
            "AMQP publish-channel shutdown for queue '{Queue}': {ReplyCode} {ReplyText} (initiator: {Initiator})",
            _queueName, args.ReplyCode, args.ReplyText, args.Initiator);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => DisposeAsync(CancellationToken.None);

    /// <summary>
    /// Disposes channels with an optional deadline token. Passing a pre-cancelled or
    /// deadline-expiry token causes any stalled <c>CloseAsync</c> to be abandoned,
    /// preserving the consumer host's graceful-shutdown grace window.
    /// </summary>
    internal async ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Unsubscribe shutdown handlers BEFORE close so a late shutdown signal doesn't fire
        // OnXxxShutdownAsync against a half-disposed host. Close in reverse-of-create order:
        // publish channel first (so in-flight retry/audit publishes drain cleanly before the
        // consume channel closes), then the consume channel.
        if (_publishChannel is not null)
        {
            _publishChannel.ChannelShutdownAsync -= OnPublishChannelShutdownAsync;
            // Race close against the deadline token. Task.Delay(Infinite, ct) completes
            // when ct fires, so a stalled CloseAsync (broker unresponsive, mock in tests)
            // doesn't block disposal beyond the caller's grace window.
            try
            {
                await Task.WhenAny(
                    _publishChannel.CloseAsync(200, "Goodbye", false, cancellationToken),
                    Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            }
            catch { /* already-closed and deadline-cancelled close are both expected on teardown */ }
            try { await _publishChannel.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow any dispose-time error so model close still runs */ }
            _publishChannel = null;
        }

        if (_model is not null)
        {
            _model.ChannelShutdownAsync -= OnChannelShutdownAsync;
            // Same deadline-race as publish channel above.
            try
            {
                await Task.WhenAny(
                    _model.CloseAsync(200, "Goodbye", false, cancellationToken),
                    Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            }
            catch { /* already-closed and deadline-cancelled close are both expected on teardown */ }
            try { await _model.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow any dispose-time error */ }
            _model = null;
        }
    }
}
