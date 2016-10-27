using System;
using ServiceConnect;
using ServiceConnect.Persistance.MongoDb;

namespace ProcessManager.Host
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** ProcessManager.Host ***********");
            Bus.Initialize(config =>
            {
                config.SetProcessManagerFinder<MongoDbProcessManagerFinder>();
                //config.SetProcessManagerFinder<InMemoryProcessManagerFinder>();
                config.EnableProcessManagerTimeouts = true;
            });

            Console.ReadLine();
        }
    }
}
