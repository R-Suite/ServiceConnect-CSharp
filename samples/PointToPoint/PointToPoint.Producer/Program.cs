using System;
using PointToPoint.Messages;
using ServiceConnect;

namespace PointToPoint.Producer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("*********** Producer ***********");
            var bus = Bus.Initialize(config =>
            {
                config.AddQueueMapping(typeof(PointToPointMessage), "PointToPoint.Consumer");
                config.SetHost("localhost");
            });

            while (true)
            {
                Console.WriteLine("Press enter to send message");
                Console.ReadLine();

                Console.WriteLine("Start: {0}", DateTime.Now);
                var id = Guid.NewGuid();

                for (int i = 0; i < 10000; i++)
                {
                    bus.Send(new PointToPointMessage(id)
                    {
                        Data = new byte[10000]
                    });
                    //Console.ReadLine();
                }

                Console.WriteLine("Sent message - {0}", id);
                Console.WriteLine("");
            }
        }
    }
}
