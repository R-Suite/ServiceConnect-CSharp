using System;
using R.MessageBus.Interfaces;

namespace CrossVersionSupport.Messages
{
    public class RMessageBusMessage : Message
    {
        public RMessageBusMessage(Guid correlationId)
            : base(correlationId)
        {
        }

        public dynamic Test { get; set; }
    }
}
