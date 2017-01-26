using ServiceConnect;

namespace Ssl.Consumer
{
    class Program
    {
        static void Main(string[] args)
        {
            var bus = Bus.Initialize(config =>
            {
                config.TransportSettings.SslEnabled = true;
                config.SetQueueName("Ssl.Consumer");
                config.ScanForMesssageHandlers = true;
            });
            bus.StartConsuming();
        }
    }
}
