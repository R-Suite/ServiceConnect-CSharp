using System;
using System.Threading;
using McDonalds.Messages;
using R.MessageBus.Interfaces;

namespace McDonalds.FoodPrep
{
    public class PrepFood : IMessageHandler<PrepFoodMessage>
    {
        public void Execute(PrepFoodMessage command)
        {
            Console.WriteLine("Preping order: BunSize - {0}, OrderId - {1}", command.BunSize, command.CorrelationId);

            Thread.Sleep(2000);

            Console.WriteLine("Preping done for order - {0}", command.CorrelationId);
        }
    }
}