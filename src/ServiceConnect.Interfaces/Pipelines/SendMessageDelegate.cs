namespace ServiceConnect.Interfaces;

/// <summary>
/// Represents the next step in the outgoing send/publish middleware chain.
/// </summary>
/// <param name="context">The send context threaded through the pipeline.</param>
/// <param name="cancellationToken">A token that cancels the operation.</param>
public delegate Task SendMessageDelegate(
    SendContext context,
    CancellationToken cancellationToken);
