using System;
using R.MessageBus.Interfaces;

namespace McDonalds.Messages
{
    public class OrderReadyMessage : Message
    {
        public OrderReadyMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}