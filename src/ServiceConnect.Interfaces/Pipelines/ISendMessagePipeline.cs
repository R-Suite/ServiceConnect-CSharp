namespace ServiceConnect.Interfaces;

/// <summary>
/// Executes the configured outgoing message pipeline.
/// </summary>
public interface ISendMessagePipeline : IAsyncDisposable
{
    /// <summary>
    /// Executes the publish pipeline for an outgoing message.
    /// </summary>
    /// <param name="typeObject">The CLR message type being published.</param>
    /// <param name="messageBytes">The serialized message payload.</param>
    /// <param name="headers">Optional outgoing headers.</param>
    /// <param name="endPoint">An optional destination endpoint override.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task ExecutePublishMessagePipelineAsync(Type typeObject, byte[] messageBytes, IDictionary<string, string>? headers = null, string? endPoint = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the send pipeline for an outgoing message.
    /// </summary>
    /// <param name="typeObject">The CLR message type being sent.</param>
    /// <param name="messageBytes">The serialized message payload.</param>
    /// <param name="headers">Optional outgoing headers.</param>
    /// <param name="endPoint">An optional destination endpoint override.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task ExecuteSendMessagePipelineAsync(Type typeObject, byte[] messageBytes, IDictionary<string, string>? headers = null, string? endPoint = null, CancellationToken cancellationToken = default);
}
