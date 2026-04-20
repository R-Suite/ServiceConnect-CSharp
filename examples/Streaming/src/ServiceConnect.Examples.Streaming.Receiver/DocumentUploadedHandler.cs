using ServiceConnect.Examples.Streaming.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Streaming.Receiver;

public sealed class DocumentUploadedHandler : IStreamHandler<DocumentUploaded>
{
    public IMessageBusReadStream Stream { get; set; } = null!;

    public void Execute(DocumentUploaded message)
    {
        var bytes = Stream.Read();
        ConsoleStatus.Success("streaming-receiver", $"received {message.FileName} with {bytes.Length} bytes");
    }
}
