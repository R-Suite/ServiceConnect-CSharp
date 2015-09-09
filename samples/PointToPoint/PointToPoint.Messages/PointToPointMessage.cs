using System;
using R.MessageBus.Interfaces;

namespace PointToPoint.Messages
{
    public class PointToPointMessage : Message
    {
        public PointToPointMessage(Guid correlationId) : base(correlationId){}
        public byte[] Data { get; set; }
    }
}
