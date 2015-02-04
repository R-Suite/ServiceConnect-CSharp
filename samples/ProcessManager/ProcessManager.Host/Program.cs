using System;
using R.MessageBus;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.SqlServer;

namespace ProcessManager.Host
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** ProcessManager.Host ***********");
            IBus bus = Bus.Initialize(config =>
            {
                config.ScanForMesssageHandlers = true;
                //config.SetProcessManagerFinder<MongoDbProcessManagerFinder>();
                //config.PersistenceStoreConnectionString = "mongodb://localhost/";
                config.PersistenceStoreDatabaseName = "RMessageBus-ProcessManagerSample";
                config.SetProcessManagerFinder<SqlServerProcessManagerFinder>();
                config.PersistenceStoreConnectionString = @"Server=*** DataBase=RMessageBus-ProcessManagerSample;Trusted_Connection=True";
                config.TransportSettings.ClientSettings["HeartbeatEnabled"] = false;
            });
            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
