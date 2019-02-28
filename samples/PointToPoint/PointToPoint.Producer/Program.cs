using System;
using System.Configuration;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using PointToPoint.Messages;
using ServiceConnect;

namespace PointToPoint.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.AddQueueMapping(typeof(PointToPointMessage), "PointToPoint.Consumer");
                config.AutoStartConsuming = false;
                //config.TransportSettings.SslEnabled = true;
                //config.TransportSettings.Username = ConfigurationManager.AppSettings["RabbitMqUsername"];
                //config.TransportSettings.Password = ConfigurationManager.AppSettings["RabbitMqPassword"];
                //config.TransportSettings.ServerName = ConfigurationManager.AppSettings["RabbitMqHost"];
                config.TransportSettings.Version = SslProtocols.Default;
                config.TransportSettings.CertificateValidationCallback += (sender, certificate, chain, errors) => true;
                config.SetHost(ConfigurationManager.AppSettings["RabbitMqHost"]);
                config.SetAuditingEnabled(false);

            });

            while (true)
            {
                Console.WriteLine("Press enter to send message");
                Console.ReadLine();

                Console.WriteLine("Start: {0}", DateTime.Now);

                for (int i = 0; i < 300000; i++)
                {
                    var id = Guid.NewGuid();
                    bus.Send(new PointToPointMessage(id)
                    {
                        Data = new byte[10000],
                        SerialNumber = i
                    });
                   // Thread.Sleep(1000);
                   // Console.ReadLine();
                }

                Console.WriteLine("Sent messages");
                Console.WriteLine("");
            }
        }
    }
}
