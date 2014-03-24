using System;
using System.Security.Policy;

namespace R.MessageBus.Interfaces
{
    [Serializable]
    public class Message
    {
        public Message(Guid correlationId)
        {
            CorrelationId = correlationId;
        }
        public Guid CorrelationId { get; private set; }
    }
}
