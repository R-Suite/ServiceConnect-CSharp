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
                config.TransportSettings.Password = "";
                config.TransportSettings.Username = "admin";
                config.TransportSettings.ServerName = "";
                config.TransportSettings.CertPassphrase = "";
                config.TransportSettings.CertPath = "";
                config.SetHost("");
                config.SetQueueName("Ssl.Consumer");
                config.ScanForMesssageHandlers = true;

            });
        }
    }
}
