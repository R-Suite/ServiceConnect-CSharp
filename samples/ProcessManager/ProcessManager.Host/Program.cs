using System;
using ServiceConnect;
using ServiceConnect.Persistance.InMemory;
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
                //config.SetProcessManagerFinder<MongoDbProcessManagerFinder>();
                //config.SetProcessManagerFinder<InMemoryProcessManagerFinder>();
                config.EnableTimeouts = true;
                config.PersistenceStoreConnectionString = "Server=RUFFER-F330852;Database=ServiceConnect;Trusted_Connection=True;";
            });

            Console.ReadLine();
        }
    }
}
