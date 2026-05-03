using ServiceConnect.Examples.Streaming.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Streaming.Receiver;

public sealed class DocumentUploadedHandler : IStreamHandler<DocumentUploaded>
{
    public Task ExecuteAsync(DocumentUploaded message, IMessageBusReadStream stream, CancellationToken cancellationToken = default)
    {
        var bytes = stream.Read();
        ConsoleStatus.Success("streaming-receiver", $"received {message.FileName} with {bytes.Length} bytes");
        return Task.CompletedTask;
    }
}
