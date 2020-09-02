using System;
using System.Configuration;
using System.Diagnostics;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ServiceConnect;
using ServiceConnect.Container.Default;
using ServiceConnect.Container.StructureMap;
using ServiceConnect.Interfaces;
using StructureMap;

namespace PointToPoint.Consumer
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
            Console.WriteLine("*********** Consumer ***********");

            IContainer myContainer = new StructureMap.Container();

            var bus = Bus.Initialize(config =>
            {
                config.SetContainer(myContainer);
                config.SetQueueName("PointToPoint.Consumer");
                config.SetHost(ConfigurationManager.AppSettings["RabbitMqHost"]);
                config.SetAuditingEnabled(false);
                config.SetNumberOfClients(20);
                config.SetLogger(new Logger());
            });
            bus.StartConsuming();

            Console.WriteLine("Connected");
            
            Console.ReadLine();

            bus.Dispose();
        }
    }
}
