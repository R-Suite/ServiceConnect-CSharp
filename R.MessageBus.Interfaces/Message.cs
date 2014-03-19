using System;

namespace R.MessageBus.Interfaces
{
    [Serializable]
    public class Message
    {
        public Guid CorrelationId { get; private set; }
        public Message(Guid correlationId)
        {
            CorrelationId = correlationId;
        }
    }
}
