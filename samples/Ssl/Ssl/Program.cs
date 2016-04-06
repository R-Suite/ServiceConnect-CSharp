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
                config.TransportSettings.ServerName = "HOSTNAME";
                config.TransportSettings.CertPassphrase = "client cert pass";
                config.TransportSettings.CertPath = "keycert.p12";
                config.SetQueueName("Ssl.Consumer");
                config.ScanForMesssageHandlers = true;

            });

            bus.Send("Ssl.Consumer", new SslMessage(Guid.NewGuid()));
        }
    }
}
