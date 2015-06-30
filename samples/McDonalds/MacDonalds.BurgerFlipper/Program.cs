using System;
using R.MessageBus;

namespace McDonalds.BurgerFlipper
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Burger Flipper ***********");
            var bus = Bus.Initialize(x =>
            {
                x.SetAuditingEnabled(true);
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
