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
            });

            bus.StartConsuming();

            Console.ReadLine();

            bus.Dispose();
        }
    }
}
