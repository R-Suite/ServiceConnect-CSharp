using System;
using ServiceConnect;

namespace Kafka.Consumer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("*********** Consumer ***********");
            
            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("Kafka.Consumer");
                config.SetNumberOfClients(2);
                config.ScanForMesssageHandlers = true;
                config.SetHost("localhost:9092");
                config.SetProducer<ServiceConnect.Client.Kafka.Producer>();
                config.SetConsumer<ServiceConnect.Client.Kafka.Consumer>();
            });
            bus.StartConsuming();

            Console.ReadLine();

            bus.Dispose();
        }
    }
}
