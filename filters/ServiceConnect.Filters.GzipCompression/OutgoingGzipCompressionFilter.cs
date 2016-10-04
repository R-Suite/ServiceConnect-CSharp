using System.IO;
using System.IO.Compression;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.GzipCompression
{
    public class OutgoingGzipCompressionFilter : IFilter
    {
        public bool Process(Envelope envelope)
        {
            using (var messageMemoryStream = new MemoryStream(envelope.Body))
            using (var compressedMessageMemoryStream = new MemoryStream())
            {
                using (var gzipStream = new GZipStream(compressedMessageMemoryStream, CompressionMode.Compress))
                {
                    MemoryStreamUtilities.CopyTo(messageMemoryStream, gzipStream);
                }

                envelope.Body = compressedMessageMemoryStream.ToArray();
            }
            return true;
        }

        public IBus Bus { get; set; }
    }
}
