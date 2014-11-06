using System;
using R.MessageBus;

namespace PolymorphicMessages.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            var bus = Bus.Initialize(x =>
            {
                x.ScanForMesssageHandlers = true;
                x.SetQueueName("PolymorphicConsumer");
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
