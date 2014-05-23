using System;
using System.Threading;
using McDonalds.Messages;
using R.MessageBus.Interfaces;

namespace McDonalds.BurgerFlipper
{
    public class CookBurger : IMessageHandler<CookBurgerMessage>
    {
        private readonly IBus _bus;

        public CookBurger(IBus bus)
        {
            _bus = bus;
        }

        public void Execute(CookBurgerMessage message)
        {
            Console.WriteLine("Cooking burger: Burger size - {0},  Order Id - {1}", message.BurgerSize, message.CorrelationId);

            Thread.Sleep(3000);

            Console.WriteLine("Burger ready for order - {0}", message.CorrelationId);

            _bus.Publish(new BurgerCookedMessage(message.CorrelationId));
        }

        public IConsumeContext Context { get; set; }
    }
}