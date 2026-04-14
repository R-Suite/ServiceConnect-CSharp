namespace ServiceConnect.Interfaces;

public delegate Task<ConsumeEventResult> ConsumerEventHandler(ReadOnlyMemory<byte> message, string type, IDictionary<string, object> headers, CancellationToken cancellationToken);
