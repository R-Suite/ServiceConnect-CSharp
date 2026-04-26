namespace ServiceConnect.Interfaces;

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
        IDictionary<string, string> headers, string? endPoint,
        SendMessageDelegate next,
        CancellationToken cancellationToken);
}
