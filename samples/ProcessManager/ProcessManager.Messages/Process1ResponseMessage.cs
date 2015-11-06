using System;
using ServiceConnect.Interfaces;

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

        public int ProcessId { get; set; }

        public string Name { get; set; }

        public Widget Widget { get; set; }
    }
}
