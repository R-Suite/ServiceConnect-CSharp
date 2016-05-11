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
                config.SetQueueName("Ssl.Producer");
                config.ScanForMesssageHandlers = true;
            });
            
            while (true)
            {
                bus.Send("Ssl.Consumer", new SslMessage(Guid.NewGuid()));
                Console.ReadLine();
            }
        }
    }
}
