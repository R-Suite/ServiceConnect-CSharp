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
            var bus = Bus.Initialize(x =>
            {
                x.SetHost("lonappdev04");
                x.TransportSettings.ClientSettings["AutoDelete"] = true;
            });

            while (true)
            {
                Console.WriteLine("Press enter to publish message");
                Console.ReadLine();

                for (int i = 0; i < 1000000; i++)
                {
                    var id = Guid.NewGuid();
                    bus.Publish(new PublishSubscribeMessage(id));
                }

                bus.Dispose();
            }
        }
    }
}
