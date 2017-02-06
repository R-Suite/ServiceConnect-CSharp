using System;
using ServiceConnect.Interfaces;

namespace ProcessManager.Messages
{

    public class Process2ResponseMessage : Message
    {
        public Process2ResponseMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
