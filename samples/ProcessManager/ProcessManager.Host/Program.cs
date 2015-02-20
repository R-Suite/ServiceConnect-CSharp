using System;
using R.MessageBus;
using R.MessageBus.Persistance.InMemory;

namespace ProcessManager.Host
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** ProcessManager.Host ***********");
            Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<InMemoryProcessManagerFinder>();
            });

            Console.ReadLine();
        }
    }
}
