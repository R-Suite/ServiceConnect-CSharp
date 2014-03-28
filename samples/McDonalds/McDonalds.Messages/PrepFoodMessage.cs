using System;
using R.MessageBus.Interfaces;

namespace McDonalds.Messages
{
    public class PrepFoodMessage : Message
    {
        public PrepFoodMessage(Guid correlationId) : base(correlationId)
        {
        }

        public string BunSize { get; set; }
    }
}