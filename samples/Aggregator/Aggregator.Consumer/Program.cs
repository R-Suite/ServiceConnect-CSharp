using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ServiceConnect;
using ServiceConnect.Persistance.MongoDb;

namespace Aggregator.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            Bus.Initialize(x =>
            {
                x.SetQueueName("Aggregator.Consumer");
                x.PurgeQueuesOnStart();
                x.SetAuditingEnabled(true);
                x.SetHost("localhost");
                x.SetThreads(10);
            });

            Console.ReadLine();
        }
    }
}
