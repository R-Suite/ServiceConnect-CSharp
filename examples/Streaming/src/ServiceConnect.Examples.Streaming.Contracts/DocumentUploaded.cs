using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Streaming.Contracts;

public sealed class DocumentUploaded(Guid correlationId) : Message(correlationId)
{
    public string FileName { get; init; } = string.Empty;

    public int TotalBytes { get; init; }
}
