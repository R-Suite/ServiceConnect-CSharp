using System;
using R.MessageBus.Interfaces;

namespace ProcessManager.Messages
{
    public class Process2RequestMessage : Message
    {
        public Process2RequestMessage(Guid correlationId) : base(correlationId)
        {
        }

        public int ProcessId { get; set; }
    }
}
