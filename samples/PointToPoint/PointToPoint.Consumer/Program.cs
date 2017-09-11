using System;
using ServiceConnect;
using ServiceConnect.Container.Default;
using ServiceConnect.Container.StructureMap;
using StructureMap;

namespace PointToPoint.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");

            IContainer myContainer = new StructureMap.Container();

            var bus = Bus.Initialize(config =>
            {
                config.SetContainer(myContainer);
                config.SetQueueName("PointToPoint.Consumer");
                config.SetThreads(20);
                //config.SetContainerType<DefaultBusContainer>();
                config.SetHost("localhost");
                //config.TransportSettings.ClientSettings.Add("DisablePrefetch", true);
            });
            bus.StartConsuming();
            
            Console.ReadLine();

            bus.Dispose();
        }
    }
}
