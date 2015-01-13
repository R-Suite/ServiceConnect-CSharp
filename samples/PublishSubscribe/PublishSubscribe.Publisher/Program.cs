using System;
using PublishSubscribe.Messages;
using R.MessageBus;

namespace PublishSubscribe.Publisher
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize();

            while (true)
            {
                Console.WriteLine("Press enter to publish message");
                Console.ReadLine();

                var id = Guid.NewGuid();
                bus.Publish(new PublishSubscribeMessage(id));

                Console.WriteLine("Published message - {0}", id);
                Console.WriteLine("");
            }
        }
    }
}
