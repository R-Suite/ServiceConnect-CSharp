using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using ServiceConnect;
using Ssl.Messages;

namespace Ssl.Consumer
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
            bus.StartConsuming();
        }
    }
}
