using System;
using ServiceConnect.Interfaces;

namespace AsyncProcessManager.Messages
{
    public class MessageResponse : Message
    {
        public MessageResponse(Guid correlationId) : base(correlationId)
        {
        }
    }
}