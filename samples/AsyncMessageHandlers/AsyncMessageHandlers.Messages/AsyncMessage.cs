using System;
using ServiceConnect.Interfaces;

namespace AsyncMessagehandlers.Messages
{
    public class AsyncMessage : Message
    {
        public AsyncMessage(Guid correlationId) : base(correlationId)
        {
        }
        
    }
}
