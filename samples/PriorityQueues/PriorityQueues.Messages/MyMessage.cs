using System;
using ServiceConnect.Interfaces;

namespace PriorityQueues.Messages
{
    public class MyMessage : Message
    {
        public MyMessage(Guid correlationId) : base(correlationId)
        {
        }

        public string Name { get; set; }
    }
}
