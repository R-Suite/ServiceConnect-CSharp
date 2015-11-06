using System;
using ServiceConnect.Interfaces;

namespace ProcessManager.Messages
{
    public class Process1RequestMessage : Message
    {
        public Process1RequestMessage(Guid correlationId) : base(correlationId)
        {
        }

        public int ProcessId { get; set; }
    }
}
