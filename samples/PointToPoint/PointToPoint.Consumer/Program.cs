using System;
using ServiceConnect;
using ServiceConnect.Container.Default;

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
                config.SetThreads(1);
                config.SetContainerType<DefaultBusContainer>();
                config.SetHost("localhost");
                config.TransportSettings.ClientSettings.Add("PrefetchCount", 7);
                config.TransportSettings.ClientSettings.Add("HeartbeatEnabled", true);
                //config.TransportSettings.ClientSettings.Add("DisablePrefetch", true);
            });
            bus.StartConsuming();
            
            Console.ReadLine();

            bus.Dispose();
        }
    }
}
