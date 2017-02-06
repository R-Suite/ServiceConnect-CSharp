using System;
using ServiceConnect.Interfaces;

namespace ProcessManager.Messages
{

    public class Process1ResponseMessage : Message
    {
        public Process1ResponseMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
