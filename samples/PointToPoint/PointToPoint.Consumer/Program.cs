using System;
using System.Security.Cryptography.X509Certificates;
using R.MessageBus;
using R.MessageBus.Container;

namespace PointToPoint.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("PointToPoint2");
                config.SetThreads(10);
            });
            
            bus.StartConsuming();
            
            Console.ReadLine();
        }
    }
}
