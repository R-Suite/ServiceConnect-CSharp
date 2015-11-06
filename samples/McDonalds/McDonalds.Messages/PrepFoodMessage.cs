using System;
using ServiceConnect.Interfaces;

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