using System;
using ServiceConnect;

namespace ContentRouting.Consumer1
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 1 ***********");

            var bus = Bus.Initialize(x =>
            {
                //x.SetContainer(myContainer);
                x.ScanForMesssageHandlers = true;
                x.SetQueueName("Consumer1");
                x.SetHost("localhost");
                x.SetThreads(20);
            });

            bus.StartConsuming();

            Console.ReadLine();

            bus.Dispose();
        }
    }
}
