using System;
using ServiceConnect.Interfaces;

namespace PointToPoint.ZeroMQ.Messages
{
    public class PointToPointMessage : Message
    {
        public PointToPointMessage(Guid correlationId) : base(correlationId) { }

        public int Count { get; set; }
    }
}
