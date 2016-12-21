using System;
using ContentRouting.Messages;
using ServiceConnect;

namespace ContentRouting.Publisher
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
            });

            while (true)
            {
                Console.WriteLine("Press enter to publish message");
                Console.ReadLine();

                for (int i = 0; i < 1; i++)
                {
                    var id = Guid.NewGuid();
                    bus.Publish(new MyMessage(id), "routingkey0");
                }

                bus.Dispose();
            }
        }
    }
}
