using System;
using R.MessageBus.Interfaces;

namespace ProcessManager.Messages
{
    public class Widget2
    {
        public int Size { get; set; }
    }

    public class Process2ResponseMessage : Message
    {
        public Process2ResponseMessage(Guid correlationId) : base(correlationId)
        {
        }

        public int ProcessId { get; set; }

        public string Name { get; set; }

        public Widget2 Widget { get; set; }
    }
}
