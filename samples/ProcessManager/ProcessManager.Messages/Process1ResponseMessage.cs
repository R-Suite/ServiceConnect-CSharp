using System;
using R.MessageBus.Interfaces;

namespace ProcessManager.Messages
{
    public class Widget
    {
        public int Size { get; set; }
    }

    public class Process1ResponseMessage : Message
    {
        public Process1ResponseMessage(Guid correlationId) : base(correlationId)
        {
        }

        public string Name { get; set; }

        public int Age { get; set; }

        public Widget Widget { get; set; }
    }
}
