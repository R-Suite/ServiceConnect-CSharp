using System.IO.Compression;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.GzipCompression;

public class IncomingGzipCompressionFilter : IFilter
{
    private const int MaxDecompressedSize = 10 * 1024 * 1024; // 10 MB

    public IBus Bus { get; set; } = null!;

    public bool Process(Envelope envelope)
    {
        using var compressedMessageMemoryStream = new MemoryStream(envelope.Body);
        using var messageMemoryStream = new MemoryStream();
        using (var gzipStream = new GZipStream(compressedMessageMemoryStream, CompressionMode.Decompress))
        {
            gzipStream.CopyTo(messageMemoryStream);
            if (messageMemoryStream.Length > MaxDecompressedSize)
                throw new InvalidOperationException($"Decompressed message exceeds maximum size of {MaxDecompressedSize / (1024 * 1024)} MB.");
        }
        envelope.Body = messageMemoryStream.ToArray();
        return true;
    }
}
