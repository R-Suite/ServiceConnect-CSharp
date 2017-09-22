using System;
using ServiceConnect.Interfaces;

namespace AsyncProcessManager.Messages
{
    public class MessageRequest : Message
    {
        public MessageRequest(Guid correlationId) : base(correlationId)
        {
        }
    }
}