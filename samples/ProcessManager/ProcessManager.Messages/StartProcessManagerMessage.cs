using System;
using ServiceConnect.Interfaces;

namespace ProcessManager.Messages
{
    public class StartProcessManagerMessage : Message
    {
        public StartProcessManagerMessage(Guid correlationId) : base(correlationId)
        {
        }

        public int ProcessId { get; set; }
    }
}
