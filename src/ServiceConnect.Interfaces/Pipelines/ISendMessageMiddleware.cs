namespace ServiceConnect.Interfaces;

/// <summary>
/// Middleware that wraps outgoing send and publish operations. Implementations
/// receive a <see cref="SendContext"/> exposing the strongly-typed message,
/// serialized bytes, headers, and routing metadata.
/// </summary>
public interface ISendMessageMiddleware
{
    /// <summary>
    /// Processes an outgoing message and optionally delegates to the next middleware.
    /// </summary>
    /// <param name="context">The send context.</param>
    /// <param name="next">The next delegate in the chain.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task ProcessAsync(
        SendContext context,
        SendMessageDelegate next,
        CancellationToken cancellationToken);
}
