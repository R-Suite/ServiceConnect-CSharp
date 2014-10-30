using System;
using R.MessageBus.Interfaces;

namespace PolymorphicMessages.Messages
{
    public class BaseType : Message
    {
        public BaseType(Guid correlationId) : base(correlationId)
        {
        }
    }
}
