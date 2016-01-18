using System;
using ServiceConnect;
using ServiceConnect.Persistance.InMemory;

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
