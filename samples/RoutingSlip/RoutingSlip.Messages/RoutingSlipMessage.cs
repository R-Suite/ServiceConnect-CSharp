using System;
using R.MessageBus.Interfaces;

namespace RoutingSlip.Messages
{
    public class RoutingSlipMessage : Message
    {
        public RoutingSlipMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
