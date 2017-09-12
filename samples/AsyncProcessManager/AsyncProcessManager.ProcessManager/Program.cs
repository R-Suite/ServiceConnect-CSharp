using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ServiceConnect;
using ServiceConnect.Persistance.InMemory;

namespace AsyncProcessManager.ProcessManager
{
    class Program
    {
        static void Main(string[] args)
        {
        
            Console.WriteLine("*********** ProcessManager ***********");

            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("AsyncProcessManager.ProcessManager");
                config.SetHost("localhost");
                config.SetProcessManagerFinder<InMemoryProcessManagerFinder>();
            });
            bus.StartConsuming();

            Console.ReadLine();

            bus.Dispose();
        }
    }
}
