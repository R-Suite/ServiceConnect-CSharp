using System;

namespace PolymorphicMessages.Messages
{
    public class DerivedType : BaseType
    {
        public DerivedType(Guid correlationId) : base(correlationId)
        {
        }
    }
}
