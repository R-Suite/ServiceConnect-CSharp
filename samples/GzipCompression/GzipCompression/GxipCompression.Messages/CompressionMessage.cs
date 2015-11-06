using System;
using ServiceConnect.Interfaces;

namespace GzipCompression.Messages
{
    public class CompressionMessage : Message
    {
        public CompressionMessage(Guid correlationId) : base(correlationId)
        {
        }

        public string Data { get; set; }
    }
}
