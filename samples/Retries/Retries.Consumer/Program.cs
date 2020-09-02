using System;
using ServiceConnect;

namespace Retries.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            Bus.Initialize(x =>
            {
                x.SetHost("localhost");
                x.SetQueueName("RetryTest");
            });
            
            Console.ReadLine();
        }
    }
}
