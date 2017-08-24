using System;
using ServiceConnect.Interfaces;

namespace PointToPoint.Messages
{
    public class PointToPointMessage : Message
    {
        public PointToPointMessage(Guid correlationId) : base(correlationId){}
    }
}
