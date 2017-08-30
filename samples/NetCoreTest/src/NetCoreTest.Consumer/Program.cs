using System;
using ServiceConnect;
using ServiceConnect.Container.Default;
using ServiceConnect.Container.StructureMap;
using StructureMap;

namespace NetCoreTest.Consumer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");

            //IContainer myContainer = new StructureMap.Container();

            var bus = Bus.Initialize(config =>
            {
                //config.SetContainer(myContainer);
                config.SetThreads(1);
                //config.SetContainerType<DefaultBusContainer>();
                config.SetContainerType<StructureMapContainer>();
                config.ScanForMesssageHandlers = true;
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
