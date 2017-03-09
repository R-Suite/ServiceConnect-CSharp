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
            deduplicationSettings.DatabaseNameMongoDb = "MessageDeduplication";
            deduplicationSettings.CollectionNameMongoDb = "JPBenchmark";

            var bus = Bus.Initialize(config =>
            {
                config.BeforeConsumingFilters = new List<Type>
                {
                    typeof(IncomingDeduplicationFilterRedis)
                };
                config.AfterConsumingFilters = new List<Type>
                {
                    typeof(OutgoingDeduplicationFilterRedis)
                };
                config.SetQueueName("MessageDeduplication.Consumer");
                config.SetThreads(10);
                config.SetContainerType<DefaultBusContainer>();
                config.SetHost("localhost");
                config.TransportSettings.ClientSettings.Add("PrefetchCount", 300);
                config.TransportSettings.ClientSettings.Add("HeartbeatEnabled", true);
                //config.TransportSettings.ClientSettings.Add("DisablePrefetch", true);
            });
            bus.StartConsuming();
            
            Console.ReadLine();

            bus.Dispose();
        }
    }
}
