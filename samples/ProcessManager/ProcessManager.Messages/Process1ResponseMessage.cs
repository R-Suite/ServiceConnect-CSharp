using System;
using R.MessageBus.Interfaces;

namespace ProcessManager.Messages
{
    public class Process1ResponseMessage : Message
    {
        public Process1ResponseMessage(Guid correlationId) : base(correlationId)
        {
        }

        public string Name { get; set; }

        public int Age { get; set; }
    }
}
