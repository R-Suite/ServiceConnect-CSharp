using System;
using ServiceConnect.Interfaces;

namespace McDonalds.Messages
{
    public class CookBurgerMessage : Message
    {
        public CookBurgerMessage(Guid correlationId) : base(correlationId)
        {
        }

        public string BurgerSize { get; set; }
    }
}