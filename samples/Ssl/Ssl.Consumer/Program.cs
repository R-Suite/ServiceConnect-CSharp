using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using R.MessageBus;
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
                config.TransportSettings.ServerName = "SslTest";
                config.SetQueueName("Ssl.Consumer");
        }
    }
}
