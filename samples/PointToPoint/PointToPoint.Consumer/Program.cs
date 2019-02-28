using System;
using System.Configuration;
using System.Diagnostics;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
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
                config.TransportSettings.Version = SslProtocols.Default;
                config.TransportSettings.CertificateValidationCallback += (sender, certificate, chain, errors) => true;
                config.SetHost(ConfigurationManager.AppSettings["RabbitMqHost"]);
                config.SetAuditingEnabled(false);
                config.SetNumberOfClients(20);
            });
            bus.StartConsuming();

            Console.WriteLine("Connected");
            
            Console.ReadLine();

            bus.Dispose();
        }
    }
}
