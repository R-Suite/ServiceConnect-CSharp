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
                //x.SetHost("lonappdev04");
            });

            while (true)
            {
                Console.WriteLine("Press enter to send message");
                Console.ReadLine();

                var id = Guid.NewGuid();
                bus.Send("Consumer1", new PublishSubscribeMessage(id));

                Console.WriteLine("Published message - {0}", id);
                Console.WriteLine("");
            }
        }
    }
}
