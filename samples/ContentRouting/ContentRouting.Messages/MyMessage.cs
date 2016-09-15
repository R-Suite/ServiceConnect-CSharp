using System;
using ServiceConnect.Interfaces;

namespace ContentRouting.Messages
{

    public class MyBaseMessage : Message
    {
        public MyBaseMessage(Guid correlationId)
            : base(correlationId)
        {
        }
    }

    public class MyMessage : MyBaseMessage
    {
        public MyMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
