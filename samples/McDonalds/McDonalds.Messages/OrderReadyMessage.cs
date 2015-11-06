using System;
using ServiceConnect.Interfaces;

namespace McDonalds.Messages
{
    public class OrderReadyMessage : Message
    {
        public OrderReadyMessage(Guid correlationId) : base(correlationId)
        {
        }

        public string Meal { get; set; }
        public string Size { get; set; }
    }
}