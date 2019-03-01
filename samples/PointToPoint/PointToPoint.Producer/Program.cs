using System;
using System.Configuration;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using PointToPoint.Messages;
using ServiceConnect;
using ServiceConnect.Interfaces;

namespace PointToPoint.Producer
{
    class Logger : ILogger
    {
        public void Debug(string message)
        {
            Console.WriteLine(message);
        }

        public void Info(string message)
        {
            Console.WriteLine(message);
        }

        public void Error(string message, Exception ex = null)
        {
            Console.WriteLine(message);
        }

        public void Warn(string message, Exception ex = null)
        {
            Console.WriteLine(message);
        }

        public void Fatal(string message, Exception ex = null)
        {
            Console.WriteLine(message);
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.AddQueueMapping(typeof(PointToPointMessage), "PointToPoint.Consumer");
                config.AutoStartConsuming = false;
                config.SetHost(ConfigurationManager.AppSettings["RabbitMqHost"]);
                config.SetAuditingEnabled(false);
                config.SetLogger(new Logger());

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
                    Thread.Sleep(1000);
                   // Console.ReadLine();
                }

                Console.WriteLine("Sent messages");
                Console.WriteLine("");
            }
        }
    }
}
