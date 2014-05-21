using System;
using R.MessageBus.Interfaces;

namespace RequestRepsonse.Messages
{
    public class RequestMessage : Message
    {
        public RequestMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
