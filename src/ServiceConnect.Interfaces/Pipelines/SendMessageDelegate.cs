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
    IDictionary<string, string> headers, string? endPoint,
    CancellationToken cancellationToken);
