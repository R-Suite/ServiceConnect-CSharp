using System;
using ServiceConnect.Interfaces;

namespace CompetingConsumers.Messages
{
    public class PointToPointMessage : Message
    {
        public PointToPointMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}