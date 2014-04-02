using System;
using R.MessageBus;

namespace CompetingConsumers.Consumer2
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 2 ***********");
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
