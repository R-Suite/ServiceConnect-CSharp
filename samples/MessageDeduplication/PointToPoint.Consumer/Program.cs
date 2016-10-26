using System;
using System.Collections.Generic;
using log4net.Config;
using ServiceConnect;
using ServiceConnect.Container.Default;
using ServiceConnect.Filters.MessageDeduplication;
using ServiceConnect.Filters.MessageDeduplication.Filters;

namespace PointToPoint.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            XmlConfigurator.Configure();
            Console.WriteLine("*********** Consumer ***********");

            var deduplicationSettings = DeduplicationFilterSettings.Instance;
            deduplicationSettings.MsgCleanupIntervalMinutes = 12;
            deduplicationSettings.MsgExpiryHours = 2;

            var bus = Bus.Initialize(config =>
            {
                config.BeforeConsumingFilters = new List<Type>
                {
                    typeof(IncomingDeduplicationFilterMongoDb)
                };
                config.AfterConsumingFilters = new List<Type>
                {
                    typeof(OutgoingDeduplicationFilterMongoDb)
                };
                config.SetQueueName("MessageDeduplication.Consumer");
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
