using System;
using ServiceConnect.Interfaces;

namespace ProcessManager.Messages
{
    public class Process2RequestMessage : Message
    {
        public Process2RequestMessage(Guid correlationId) : base(correlationId)
        {
        }
        
    }
}
