using System;
using R.MessageBus;

namespace Retries.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            Bus.Initialize(x =>
            {
                x.SetQueueName("RetryTest");
            });
            
            Console.ReadLine();
        }
    }
}
