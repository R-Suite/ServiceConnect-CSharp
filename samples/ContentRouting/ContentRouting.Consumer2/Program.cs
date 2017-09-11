using System;
using ServiceConnect;

namespace ContentRouting.Consumer2
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 2 ***********");
            var bus = Bus.Initialize(x =>
            {
                x.ScanForMesssageHandlers = true;
                x.SetQueueName("Consumer2");
                x.SetHost("localhost");
                x.SetThreads(20);
            });

            bus.StartConsuming();

            Console.ReadLine();

            bus.Dispose();
        }
    }
}
