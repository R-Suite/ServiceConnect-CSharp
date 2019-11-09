using System;
using Kafka.Messages;
using ServiceConnect;

namespace Kafka.Producer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.SetQueueName("Kafka.Producer");
                config.AddQueueMapping(typeof(NetCoreTestMessage), "Kafka.Consumer");
                config.SetHost("localhost:9092");
                config.SetProducer<ServiceConnect.Client.Kafka.Producer>();
                config.SetConsumer<ServiceConnect.Client.Kafka.Consumer>();
            });

            //Console.WriteLine("Press enter to send message");
            //Console.ReadLine();

            Console.WriteLine("Start: {0}", DateTime.Now);

            for (int i = 0; i < 30; i++)
            {
                Console.WriteLine(i);
                var id = Guid.NewGuid();
                bus.Send("Kafka.Consumer", new NetCoreTestMessage(id)
                {
                    Data = new byte[10000],
                    SerialNumber = i
                });
                //Console.ReadLine();
            }

            Console.WriteLine("Sent messages");
            Console.WriteLine("");
            Console.ReadLine();
        }
    }
}
