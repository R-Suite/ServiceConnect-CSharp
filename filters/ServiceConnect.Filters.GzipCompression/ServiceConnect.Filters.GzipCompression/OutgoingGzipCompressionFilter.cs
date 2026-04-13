using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.GzipCompression
{
    public class OutgoingGzipCompressionFilter : IFilter
    {
        public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            using (var messageMemoryStream = new MemoryStream(envelope.Body))
            using (var compressedMessageMemoryStream = new MemoryStream())
            {
                using (var gzipStream = new GZipStream(compressedMessageMemoryStream, CompressionMode.Compress))
                {
                    messageMemoryStream.CopyTo(gzipStream);
                }

                envelope.Body = compressedMessageMemoryStream.ToArray();
            }
            return Task.FromResult(true);
        }

        public IBus Bus { get; set; }
    }
}
