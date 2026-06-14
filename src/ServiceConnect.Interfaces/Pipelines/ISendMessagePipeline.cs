namespace ServiceConnect.Interfaces;

/// <summary>
/// Executes the configured outgoing message pipeline.
/// </summary>
public interface ISendMessagePipeline : IAsyncDisposable
{
    /// <summary>
    /// Executes the publish pipeline for an outgoing message.
    /// </summary>
    /// <param name="context">The send context for the publish.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task ExecutePublishMessagePipelineAsync(
        SendContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the send pipeline for an outgoing message.
    /// </summary>
    /// <param name="context">The send context for the send.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task ExecuteSendMessagePipelineAsync(
        SendContext context,
        CancellationToken cancellationToken = default);
}
