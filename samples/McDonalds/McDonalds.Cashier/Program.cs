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
                x.SetProcessManagerFinder<InMemoryProcessManagerFinder>();
                x.SetAuditingEnabled(true);
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
