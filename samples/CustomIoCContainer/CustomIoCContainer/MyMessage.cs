using System;
using R.MessageBus.Interfaces;

namespace CustomIoCContainer
{
    public class MyMessage : Message
    {
        public MyMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}
