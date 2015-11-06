using System;
using ServiceConnect;

namespace RecipientList.Consumer2
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 2 ***********");
            var bus = Bus.Initialize(x =>
            {
                x.SetQueueName("Consumer2");
            });

            bus.StartConsuming();

            Console.ReadLine();
        }
    }
}
