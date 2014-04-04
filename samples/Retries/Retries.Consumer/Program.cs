using System;
using R.MessageBus;

namespace Retries.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            var bus = Bus.Initialize(x =>
            {
                x.ScanForMesssageHandlers = true;
                x.SetQueueName("RetryTest");
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
