using System;
using ServiceConnect.Interfaces;

namespace AsyncProcessManager.Messages
{
    public class CompleteMessage : Message
    {
        public CompleteMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}