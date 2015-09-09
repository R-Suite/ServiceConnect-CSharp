using System;
using System.Threading;
using R.MessageBus;

namespace PointToPoint.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");

            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("PointToPoint.Consumer");
                config.SetHost("lonappdev04");
                config.SetThreads(10);
                
            });
            bus.StartConsuming();
            
            Console.ReadLine();
        }
    }
}
