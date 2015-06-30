using System.IO;
using System.IO.Compression;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Filters.GzipCompression
{
    public class IncomingGzipCompressionFilter : IFilter
    {
        public bool Process(Envelope envelope)
        {
            using (var compressedMessageMemoryStream = new MemoryStream(envelope.Body))
            using (var messageMemoryStream = new MemoryStream())
            {
                using (var gzipStream = new GZipStream(compressedMessageMemoryStream, CompressionMode.Decompress))
                {
                    MemoryStreamUtilities.CopyTo(gzipStream, messageMemoryStream);
                }

                envelope.Body = messageMemoryStream.ToArray();
            }
            return true;
        }
    }
}