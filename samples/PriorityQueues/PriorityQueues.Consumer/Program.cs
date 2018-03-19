using System;
using System.Collections;
using System.Collections.Generic;
using ServiceConnect;
using ServiceConnect.Container.StructureMap;

namespace PriorityQueues.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");

            IDictionary<string, object> csArgs = new Dictionary<string, object> {{"x-max-priority", (int)10 }};

            var bus = Bus.Initialize(config =>
            {
                config.SetContainerType<StructureMapContainer>();
                config.SetThreads(1);
                config.TransportSettings.ClientSettings.Add("Arguments", csArgs);
                config.SetErrorQueueName("PriorityQueues.Consumer.Errors");
            });

            Console.ReadLine();

            bus.Dispose();
        }
    }
}
