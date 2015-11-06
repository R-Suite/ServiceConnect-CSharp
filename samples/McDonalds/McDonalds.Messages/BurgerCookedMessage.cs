using System;
using ServiceConnect.Interfaces;

namespace McDonalds.Messages
{
    public class BurgerCookedMessage : Message
    {
        public BurgerCookedMessage(Guid correlationId) : base(correlationId)
        {
        }
    }
}