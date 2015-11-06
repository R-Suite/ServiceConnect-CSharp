using System;
using ServiceConnect.Interfaces;

namespace CustomIoCContainer
{
    public class MyMessage : Message
    {
        public MyMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
