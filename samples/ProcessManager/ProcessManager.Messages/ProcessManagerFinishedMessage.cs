using System;
using ServiceConnect.Interfaces;

namespace ProcessManager.Messages
{
    public class ProcessManagerFinishedMessage : Message
    {
        public ProcessManagerFinishedMessage(Guid correlationId) : base(correlationId)
        {
        }
        
    }
}
