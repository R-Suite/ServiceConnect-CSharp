using System;
using ServiceConnect.Interfaces;

namespace McDonalds.Messages
{
    public class FoodPrepped : Message
    {
        public FoodPrepped(Guid correlationId) : base(correlationId)
        {
        }
    }
}