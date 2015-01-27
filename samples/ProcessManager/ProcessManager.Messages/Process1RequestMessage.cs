using System;
using R.MessageBus.Interfaces;

namespace ProcessManager.Messages
{
    public class Process1RequestMessage : Message
    {
        public Process1RequestMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
