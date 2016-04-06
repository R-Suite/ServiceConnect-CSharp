using System;
using ServiceConnect;
using Ssl.Messages;

namespace Ssl.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            var bus = Bus.Initialize(config =>
            {
                config.TransportSettings.SslEnabled = true;
                config.TransportSettings.Password = "Pw)8dP8Vn]v<~n3M";
                config.TransportSettings.Username = "admin";
                config.TransportSettings.ServerName = "LONDATTST01";
                config.TransportSettings.CertPassphrase = "6zbdsE8muh25yBgc";
                config.TransportSettings.CertPath = "C:\\git\\Security\\1. Infrastructure\\c. RabbitMQ\\ssl\\client\\keycert.p12";
                config.SetHost("LONDATTST01");
                config.SetQueueName("Ssl.Producer");

            });

            bus.Send("Ssl.Consumer", new SslMessage(Guid.NewGuid()));
        }
    }
}
