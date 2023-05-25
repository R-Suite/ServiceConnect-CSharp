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

            var bus = Bus.Initialize(config =>
            {
                config.SetNumberOfClients(1);
                config.SetContainerType<StructureMapContainer>();
                config.ScanForMesssageHandlers = true;
                config.SetHost("localhost");
            });
            bus.StartConsuming();

            Console.ReadLine();

            bus.Dispose();
        }
    }
}
