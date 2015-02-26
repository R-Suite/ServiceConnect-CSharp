using System;
using R.MessageBus;

namespace PublishSubscribe.Consumer2
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer 2 ***********");
            var bus = Bus.Initialize(x =>
            {
                x.ScanForMesssageHandlers = true;
                x.SetQueueName("Consumer2");
                x.SetHost("lonappdev04");
                x.TransportSettings.ClientSettings["AutoDelete"] = true;
                x.TransportSettings.ClientSettings["HeartbeatEnabled"] = false;
                x.TransportSettings.ClientSettings["HeartbeatTime"] = 1;
            });

            bus.StartConsuming();

            Console.ReadLine();

            bus.Dispose();
        }
    }
}
