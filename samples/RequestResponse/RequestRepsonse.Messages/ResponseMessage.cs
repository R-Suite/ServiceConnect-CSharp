using System;
using R.MessageBus.Interfaces;

namespace RequestRepsonse.Messages
{
    public class ResponseMessage : Message
    {
        public ResponseMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
