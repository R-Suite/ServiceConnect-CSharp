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
/// error) are NOT auto-recovered by RabbitMQ.Client v7. When a non-Application initiator
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
    /// queue deleted, policy expired, peer protocol error). Latched until disposal.
    /// </summary>
    internal bool IsCancelledByBroker => Volatile.Read(ref _consumerCancelledByBroker) != 0;

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
        // Publish channel needs an independent shutdown subscriber: RabbitMQ.Client v7 does
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
        // 404/406 against the consumer channel) is NOT auto-recovered by RabbitMQ.Client v7
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Unsubscribe shutdown handlers BEFORE close so a late shutdown signal doesn't fire
        // OnXxxShutdownAsync against a half-disposed host. Match the host's existing dispose
        // ordering for the channels themselves: publish channel first (so retry/audit
        // publishes that may still be in flight see a clean close), then the consume channel.
        if (_publishChannel is not null)
        {
            _publishChannel.ChannelShutdownAsync -= OnPublishChannelShutdownAsync;
            try { await _publishChannel.CloseAsync().ConfigureAwait(false); }
            catch { /* closing an already-closed channel throws; swallow it */ }
            await _publishChannel.DisposeAsync().ConfigureAwait(false);
            _publishChannel = null;
        }

        if (_model is not null)
        {
            _model.ChannelShutdownAsync -= OnChannelShutdownAsync;
            try { await _model.CloseAsync().ConfigureAwait(false); }
            catch { /* closing an already-closed channel throws; swallow it */ }
            await _model.DisposeAsync().ConfigureAwait(false);
            _model = null;
        }
    }
}
