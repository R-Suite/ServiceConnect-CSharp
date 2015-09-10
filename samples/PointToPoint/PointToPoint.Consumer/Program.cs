using System;
using System.Threading;
using R.MessageBus;
using R.MessageBus.Container.Default;

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
                config.SetThreads(2);
                config.SetContainerType<DefaultBusContainer>();
                config.SetHost("localhost");
                config.TransportSettings.ClientSettings.Add("PrefetchCount", 100);
            });
            bus.StartConsuming();
            
            Console.ReadLine();
        }
    }
}
