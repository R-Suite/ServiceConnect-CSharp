using System;
using ServiceConnect.Interfaces;

namespace Kafka.Messages
{
    public class NetCoreTestMessage : Message
    {
        public NetCoreTestMessage(Guid correlationId) : base(correlationId){ }
        public byte[] Data { get; set; }
        public int SerialNumber { get; set; }
    }
}
