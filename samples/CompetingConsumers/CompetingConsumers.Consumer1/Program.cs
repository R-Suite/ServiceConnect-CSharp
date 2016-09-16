using System;
using System.Collections.Generic;
using ServiceConnect;

namespace CompetingConsumers.Consumer1
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 1 ***********");
            var bus = Bus.Initialize(x =>
            {
               
                x.ScanForMesssageHandlers = true;
                x.SetQueueName("CompetingConsumers");
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
