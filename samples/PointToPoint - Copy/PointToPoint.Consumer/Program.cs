using System;
using System.Configuration;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Ruffer.Security.Filters;
using ServiceConnect;
using ServiceConnect.Container.Default;
using ServiceConnect.Container.StructureMap;
using ServiceConnect.Filters.MessageDeduplication;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using StructureMap;

namespace PointToPoint.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");

            IContainer myContainer = new StructureMap.Container();

            var depdulicationSettings = DeduplicationFilterSettings.Instance;
            depdulicationSettings.ConnectionStringMongoDb = ConfigurationManager.AppSettings["ServiceConnectPersistorConnectionString"];
            depdulicationSettings.DatabaseNameMongoDb = "MessageDeduplication";
            depdulicationSettings.CollectionNameMongoDb = ConfigurationManager.AppSettings["EndPoint"];
            depdulicationSettings.MsgCleanupIntervalMinutes = 6 * 60; // Every 6 hours
            depdulicationSettings.MsgExpiryHours = 7 * 24;            // 1 week

            var bus = Bus.Initialize(config =>
            {
                config.TransportSettings.SslEnabled = true;
                config.TransportSettings.Certs = new X509Certificate2Collection
                    {
                        new X509Certificate2(Convert.FromBase64String(ConfigurationManager.AppSettings["RabbitMqCertBase64"]),
                                             ConfigurationManager.AppSettings["RabbitMqCertPassword"])
                    };
                config.TransportSettings.Username = ConfigurationManager.AppSettings["RabbitMQUsername"];
                config.TransportSettings.Password = ConfigurationManager.AppSettings["RabbitMqPassword"];
                config.TransportSettings.ServerName = ConfigurationManager.AppSettings["RabbitMqHostname"];
                config.TransportSettings.Version = SslProtocols.Default;
                config.ScanForMesssageHandlers = true;
                config.SetThreads(20);
                config.SetContainer(myContainer);
                config.SetAuditingEnabled(true);

                config.BeforeConsumingFilters.Add(typeof(IncomingDeduplicationFilterMongoDbSsl));
                config.BeforeConsumingFilters.Add(typeof(TokenDecryptFilter));
                config.AfterConsumingFilters.Add(typeof(OutgoingDeduplicationFilterMongoDbSsl));
                config.OutgoingFilters.Add(typeof(TokenInjectTokenFilter));

                config.TransportSettings.MaxRetries = 0;
                
            });
            bus.StartConsuming();
            
            Console.ReadLine();

            bus.Dispose();
        }
    }
}
