using System;
using McDonalds.Messages;
using ServiceConnect.Interfaces;

namespace McDonalds.Customer
{
    public class MealReady : IMessageHandler<OrderReadyMessage>
    {
        public void Execute(OrderReadyMessage message)
        {
            Console.WriteLine("Meal ready: Meal - {0}, Size - {1} OrderId - {2}", message.Meal, message.Size, message.CorrelationId);
        }

        public IConsumeContext Context { get; set; }
    }
}