using System;
using System.Collections.Generic;
using ProcessManager.Messages;
using ServiceConnect;
using ServiceConnect.Persistance.InMemory;

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
