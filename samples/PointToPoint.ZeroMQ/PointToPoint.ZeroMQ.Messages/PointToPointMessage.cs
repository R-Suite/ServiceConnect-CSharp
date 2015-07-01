using System;
using R.MessageBus.Interfaces;

namespace PointToPoint.ZeroMQ.Messages
{
    public class PointToPointMessage : Message
    {
        public PointToPointMessage(Guid correlationId) : base(correlationId) { }
    }
}
