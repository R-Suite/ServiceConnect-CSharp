using System;
using R.MessageBus.Interfaces;

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