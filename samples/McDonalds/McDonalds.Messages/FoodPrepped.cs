using System;
using R.MessageBus.Interfaces;

namespace McDonalds.Messages
{
    public class FoodPrepped : Message
    {
        public FoodPrepped(Guid correlationId) : base(correlationId)
        {
        }
    }
}