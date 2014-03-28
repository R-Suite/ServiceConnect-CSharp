using System;
using McDonalds.Messages;
using R.MessageBus.Interfaces;

namespace McDonalds.Customer
{
    public class MealReady : IMessageHandler<OrderReadyMessage>
    {
        public void Execute(OrderReadyMessage command)
        {
            Console.WriteLine("Meal ready: OrderId - {0}", command.CorrelationId);
        }
    }
}