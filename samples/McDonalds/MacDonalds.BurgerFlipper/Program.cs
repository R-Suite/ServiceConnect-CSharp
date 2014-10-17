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
                x.ScanForMesssageHandlers = true;
                x.SetHost("lonappdev04");
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
