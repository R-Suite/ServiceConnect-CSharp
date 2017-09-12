using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ServiceConnect;

namespace AsyncProcessManager.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");

            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("AsyncProcessManager.Consumer");
                config.SetThreads(10);
                config.SetHost("localhost");
            });
            bus.StartConsuming();

            Console.ReadLine();

            bus.Dispose();
        }
    }
}
