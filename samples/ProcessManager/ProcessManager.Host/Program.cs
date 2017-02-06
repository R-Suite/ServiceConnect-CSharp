using System;
using Ruffer.Reporting.SqlTransformation;
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
                config.SetThreads(1);
                //config.SetProcessManagerFinder<MongoDbProcessManagerFinder>();
                config.SetProcessManagerFinder<SingletonProcessManagerFinder>();
                config.SetHost("localhost");
            });

            Console.ReadLine();
        }
    }
}
