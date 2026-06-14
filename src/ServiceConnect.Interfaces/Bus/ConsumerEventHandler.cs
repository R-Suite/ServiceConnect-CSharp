namespace ServiceConnect.Interfaces;

/// <summary>
/// Handles a raw message delivered by a transport consumer.
/// </summary>
/// <param name="message">The raw message payload.</param>
/// <param name="type">The transport type name for the message.</param>
/// <param name="headers">The message headers.</param>
/// <param name="cancellationToken">A token that cancels message processing.</param>
/// <returns>The consume result reported by the handler.</returns>
public delegate Task<ConsumeEventResult> ConsumerEventHandler(ReadOnlyMemory<byte> message, string type, IDictionary<string, object> headers, CancellationToken cancellationToken);
