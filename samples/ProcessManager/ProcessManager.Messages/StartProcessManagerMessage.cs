using System;
using R.MessageBus.Interfaces;

namespace ProcessManager.Messages
{
    public class StartProcessManagerMessage : Message
    {
        public StartProcessManagerMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
