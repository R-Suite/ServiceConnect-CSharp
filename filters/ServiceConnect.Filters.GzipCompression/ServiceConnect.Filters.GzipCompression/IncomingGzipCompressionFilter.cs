using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.GzipCompression;

public class IncomingGzipCompressionFilter : IFilter
{
    private const int MaxDecompressedSize = 10 * 1024 * 1024; // 10 MB


    public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Body.Length < 2 || envelope.Body[0] != 0x1f || envelope.Body[1] != 0x8b)
            return Task.FromResult(true);

        using var compressedMessageMemoryStream = new MemoryStream(envelope.Body);
        using var messageMemoryStream = new MemoryStream();
        using (var gzipStream = new GZipStream(compressedMessageMemoryStream, CompressionMode.Decompress))
        {
            gzipStream.CopyTo(messageMemoryStream);
            if (messageMemoryStream.Length > MaxDecompressedSize)
                throw new InvalidOperationException($"Decompressed message exceeds maximum size of {MaxDecompressedSize / (1024 * 1024)} MB.");
        }
        envelope.Body = messageMemoryStream.ToArray();
        return Task.FromResult(true);
    }
}
