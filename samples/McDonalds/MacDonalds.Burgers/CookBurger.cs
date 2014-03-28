using System;
using System.Threading;
using McDonalds.Messages;
using R.MessageBus.Interfaces;

namespace MacDonalds.Burgers
{
    public class CookBurger : IMessageHandler<CookBurgerMessage>
    {
        public void Execute(CookBurgerMessage command)
        {
            Console.WriteLine("Cooking burger: Burger size - {0},  Order Id - {1}", command.BurgerSize, command.CorrelationId);

            Thread.Sleep(3000);

            Console.WriteLine("Burger ready for order - {0}", command.CorrelationId);
        }
    }
}