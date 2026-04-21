using RabbitMQ.Client;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Represents a RabbitMQ connection abstraction used by ServiceConnect transport components.
/// </summary>
public interface IServiceConnectConnection : IAsyncDisposable
{
    /// <summary>
    /// Creates a new channel on the underlying RabbitMQ connection.
    /// </summary>
    /// <returns>A channel that can be used for RabbitMQ operations.</returns>
    Task<IChannel> CreateChannelAsync();

    /// <summary>
    /// Creates a new channel on the underlying RabbitMQ connection with the specified options
    /// (for example, to enable publisher confirms).
    /// </summary>
    /// <param name="options">Channel options applied to the underlying RabbitMQ channel, or <see langword="null"/> for defaults.</param>
    /// <returns>A channel that can be used for RabbitMQ operations.</returns>
    Task<IChannel> CreateChannelAsync(CreateChannelOptions? options);

    /// <summary>
    /// Determines whether the underlying RabbitMQ connection is currently open.
    /// </summary>
    /// <returns><see langword="true"/> when the connection is open; otherwise, <see langword="false"/>.</returns>
    bool IsConnected();
}
