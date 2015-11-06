using System;
using ServiceConnect.Interfaces;

namespace PolymorphicMessages.Messages
{
    public class BaseType : Message
    {
        public BaseType(Guid correlationId) : base(correlationId)
        {
        }
    }
}
