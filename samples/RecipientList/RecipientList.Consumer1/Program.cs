using System;
using R.MessageBus;

namespace RecipientList.Consumer1
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 1 ***********");
            var bus = Bus.Initialize(x =>
            {
                x.SetQueueName("Consumer1");
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
