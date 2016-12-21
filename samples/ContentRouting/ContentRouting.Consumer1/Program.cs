using System;
using ServiceConnect;
using ServiceConnect.Container.StructureMap;
using StructureMap;

namespace ContentRouting.Consumer1
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 1 ***********");

            StructureMap.IContainer myContainer = new Container();

            var bus = Bus.Initialize(x =>
            {
                //x.SetContainer(myContainer);
                x.ScanForMesssageHandlers = true;
                x.SetQueueName("Consumer1");
                x.SetHost("localhost");
            });

            bus.StartConsuming();

            Console.ReadLine();

            bus.Dispose();
        }
    }
}
