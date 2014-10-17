using System;
using R.MessageBus;
using R.MessageBus.Persistance.InMemory;

namespace McDonalds.Cashier
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Cashier ***********");
            var bus = Bus.Initialize(x =>
            {
                x.ScanForMesssageHandlers = true;
                x.SetProcessManagerFinder<InMemoryProcessManagerFinder>();
                x.SetHost("lonappdev04");
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
