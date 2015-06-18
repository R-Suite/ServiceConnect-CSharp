using System;
using R.MessageBus;
using R.MessageBus.Container.Ninject;
using R.MessageBus.Container.StructureMap;
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
                x.SetContainerType<NinjectContainer>();
                //x.SetContainerType<StructureMapContainer>();
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
