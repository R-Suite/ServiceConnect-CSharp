using System;
using CompetingConsumers.Messages;
using R.MessageBus;

namespace CompetingConsumers.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config => config.AddQueueMapping(typeof(PointToPointMessage), "CompetingConsumers"));

            while (true)
            {
                Console.WriteLine("Press enter to send message");
                Console.ReadLine();

                var id = Guid.NewGuid();
                bus.Send(new PointToPointMessage(id));

                Console.WriteLine("Sent message - {0}", id);
                Console.WriteLine("");
            }
        }
    }
}
