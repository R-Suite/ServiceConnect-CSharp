namespace ServiceConnect.Interfaces;

/// <summary>
/// Represents the next step in the outgoing send/publish middleware chain.
/// </summary>
/// <param name="typeObject">The CLR message type being sent.</param>
/// <param name="messageBytes">The serialized message payload.</param>
/// <param name="headers">The outgoing headers.</param>
/// <param name="endPoint">The destination endpoint, when applicable.</param>
/// <param name="cancellationToken">A token that cancels the operation.</param>
public delegate Task SendMessageDelegate(
    Type typeObject, byte[] messageBytes,
    Dictionary<string, string> headers, string? endPoint,
    CancellationToken cancellationToken);

/// <summary>
/// Middleware that wraps outgoing send and publish operations.
/// </summary>
public interface ISendMessageMiddleware
{
    /// <summary>
    /// Processes an outgoing message and optionally delegates to the next middleware.
    /// </summary>
    /// <param name="typeObject">The CLR message type being sent.</param>
    /// <param name="messageBytes">The serialized message payload.</param>
    /// <param name="headers">The outgoing headers.</param>
    /// <param name="endPoint">The destination endpoint, when applicable.</param>
    /// <param name="next">The next delegate in the chain.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task ProcessAsync(Type typeObject, byte[] messageBytes,
        Dictionary<string, string> headers, string? endPoint,
        SendMessageDelegate next,
        CancellationToken cancellationToken);
}
