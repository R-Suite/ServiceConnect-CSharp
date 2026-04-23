using ServiceConnect.Examples.Streaming.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Streaming.Receiver;

public sealed class DocumentUploadedHandler : IStreamHandler<DocumentUploaded>
{
    public IMessageBusReadStream Stream { get; set; } = null!;

    public Task ExecuteAsync(DocumentUploaded message, CancellationToken cancellationToken = default)
    {
        var bytes = Stream.Read();
        ConsoleStatus.Success("streaming-receiver", $"received {message.FileName} with {bytes.Length} bytes");
        return Task.CompletedTask;
    }
}
