using RabbitMQ.Client;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Represents a RabbitMQ connection abstraction used by ServiceConnect transport components.
/// </summary>
internal interface IServiceConnectConnection : IAsyncDisposable
{
    /// <summary>
    /// Creates a new channel on the underlying RabbitMQ connection.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel connection-establishment and channel-open operations.</param>
    /// <returns>A channel that can be used for RabbitMQ operations.</returns>
    Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new channel on the underlying RabbitMQ connection with the specified options
    /// (for example, to enable publisher confirms).
    /// </summary>
    /// <param name="options">Channel options applied to the underlying RabbitMQ channel, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">A token used to cancel connection-establishment and channel-open operations.</param>
    /// <returns>A channel that can be used for RabbitMQ operations.</returns>
    Task<IChannel> CreateChannelAsync(CreateChannelOptions? options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether the underlying RabbitMQ connection is currently open.
    /// </summary>
    /// <returns><see langword="true"/> when the connection is open; otherwise, <see langword="false"/>.</returns>
    bool IsConnected();

    /// <summary>
    /// Returns the underlying <see cref="IConnection"/>, or <see langword="null"/> if the connection
    /// has not been established yet or has been disposed. Used by <see cref="RabbitMqConsumerHost"/>
    /// to subscribe to connection-level events (shutdown, blocked, unblocked) for observability.
    /// </summary>
    IConnection? UnderlyingConnection { get; }
}
